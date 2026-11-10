use crate::state::{App, EditError, EditRequest, PinError, Role, MAX_TEXT_BYTES};
use serde_json::{json, Value};
use std::{
    collections::HashMap,
    io::{self, Read, Write},
    net::{IpAddr, TcpStream},
    sync::{
        atomic::{AtomicUsize, Ordering},
        Arc,
    },
    time::{Duration, Instant},
};

const MAX_HEADERS: usize = 32_768;
const MAX_BODY: usize = MAX_TEXT_BYTES + 8_192;
static ACTIVE_EVENTS: AtomicUsize = AtomicUsize::new(0);

struct Request {
    method: String,
    path: String,
    query: String,
    headers: HashMap<String, String>,
    body: Vec<u8>,
    peer: IpAddr,
}

impl Request {
    fn header(&self, name: &str) -> Option<&str> {
        self.headers.get(name).map(String::as_str)
    }

    fn cookie(&self) -> Option<&str> {
        self.header("cookie")?.split(';').find_map(|part| {
            part.trim()
                .strip_prefix("fi_session=")
                .filter(|v| v.len() == 64 && v.bytes().all(|b| b.is_ascii_hexdigit()))
        })
    }

    fn query_value(&self, name: &str) -> Option<&str> {
        self.query.split('&').find_map(|pair| {
            let (key, value) = pair.split_once('=')?;
            if key.trim() == name {
                Some(value.trim())
            } else {
                None
            }
        })
    }

    fn mutation_allowed(&self) -> bool {
        self.header("content-type")
            .is_some_and(|v| v.split(';').next().unwrap_or("").trim() == "application/json")
            && self.header("origin")
                == self
                    .header("host")
                    .map(|h| format!("http://{h}"))
                    .as_deref()
    }

    fn json(&self) -> Option<Value> {
        serde_json::from_slice(&self.body).ok()
    }
}

fn read_line_limited<R: Read>(
    reader: &mut R,
    limit: usize,
    deadline: Instant,
) -> io::Result<String> {
    let mut bytes = Vec::new();
    let mut byte = [0u8; 1];
    while bytes.len() < limit {
        if Instant::now() >= deadline {
            return Err(io::Error::new(io::ErrorKind::TimedOut, "request deadline"));
        }
        match reader.read(&mut byte) {
            Ok(0) => {
                if bytes.is_empty() {
                    return Err(io::Error::new(
                        io::ErrorKind::UnexpectedEof,
                        "empty connection",
                    ));
                }
                return Err(io::Error::new(
                    io::ErrorKind::UnexpectedEof,
                    "incomplete request",
                ));
            }
            Ok(_) => {}
            Err(e) if e.kind() == io::ErrorKind::Interrupted => continue,
            Err(e) if e.kind() == io::ErrorKind::WouldBlock => {
                std::thread::sleep(Duration::from_millis(10));
                continue;
            }
            Err(e) => return Err(e),
        }
        if byte[0] == b'\n' {
            return String::from_utf8(bytes)
                .map(|s| s.trim_end_matches('\r').to_owned())
                .map_err(|_| io::Error::new(io::ErrorKind::InvalidData, "invalid header"));
        }
        bytes.push(byte[0]);
    }
    Err(io::Error::new(
        io::ErrorKind::InvalidData,
        "header line too long",
    ))
}

fn read_request(stream: &mut TcpStream, file_request: &mut bool, realtime_request: &mut bool) -> Result<(Request, Instant), (u16, &'static str)> {
    let deadline = Instant::now() + Duration::from_secs(15);
    let peer = stream.peer_addr().map_err(|_| (400, "Bad request"))?.ip();
    let mut reader = stream; // No buffered read-ahead: file authorization must precede all body reads.

    // Skip any leading empty lines (RFC 7230 §3.5 robust parsing)
    let mut first = String::new();
    while first.is_empty() {
        if Instant::now() >= deadline {
            return Err((408, "Request timeout"));
        }
        first = match read_line_limited(&mut reader, 8_192, deadline) {
            Ok(line) => line,
            Err(e) if e.kind() == io::ErrorKind::TimedOut => return Err((408, "Request timeout")),
            Err(e) if e.kind() == io::ErrorKind::UnexpectedEof => {
                return Err((400, "Client disconnected"))
            }
            Err(_) => return Err((400, "Bad request")),
        };
    }

    let mut start = first.split_whitespace();
    let method = start.next().ok_or((400, "Bad request"))?.to_owned();
    let raw_target = start.next().ok_or((400, "Bad request"))?;
    let version = start.next().ok_or((400, "Bad request"))?;

    // Allow absolute-form URIs from proxies/browsers
    let target = if let Some(stripped) = raw_target
        .strip_prefix("http://")
        .or_else(|| raw_target.strip_prefix("https://"))
    {
        if let Some(pos) = stripped.find('/') {
            &stripped[pos..]
        } else {
            "/"
        }
    } else {
        raw_target
    };

    if !matches!(version, "HTTP/1.0" | "HTTP/1.1") || !target.starts_with('/') {
        return Err((400, "Bad request"));
    }

    let (path, query) = target.split_once('?').unwrap_or((target, ""));
    *file_request = path.starts_with("/api/files");
    *realtime_request = path.starts_with("/api/realtime");
    let mut headers = HashMap::new();
    let mut header_bytes = 0usize;
    let mut duplicate_security_header = false;
    loop {
        let line = match read_line_limited(&mut reader, 8_192, deadline) {
            Ok(line) => line,
            Err(e) if e.kind() == io::ErrorKind::TimedOut => return Err((408, "Request timeout")),
            Err(e) if e.kind() == io::ErrorKind::UnexpectedEof => {
                return Err((400, "Client disconnected"))
            }
            Err(_) => return Err((400, "Bad request")),
        };
        header_bytes += line.len();
        if header_bytes > MAX_HEADERS {
            return Err((431, "Headers too large"));
        }
        if line.is_empty() {
            break;
        }
        let Some((name, value)) = line.split_once(':') else {
            continue;
        };
        let key = name.trim().to_ascii_lowercase();
        if key.is_empty() {
            continue;
        }
        let val = value.trim().to_owned();
        // Prevent duplicate security-sensitive headers (smuggling protection)
        if key == "content-length" || key == "host" || key == "transfer-encoding"
            || (*realtime_request && matches!(key.as_str(), "x-fi-realtime" | "origin" | "content-type")) {
            if headers.contains_key(&key) {
                // Finish the bounded header section (never the body) before rejecting.
                // Closing a Windows socket with unread header bytes can discard the error response.
                duplicate_security_header = true;
                continue;
            }
            headers.insert(key, val);
        } else if let Some(existing) = headers.get_mut(&key) {
            // Standard RFC 7230 header merging
            if key == "cookie" {
                existing.push_str("; ");
            } else {
                existing.push_str(", ");
            }
            existing.push_str(&val);
        } else {
            headers.insert(key, val);
        }
    }
    if duplicate_security_header { return Err((400, "Duplicate security header")); }
    Ok((Request {
        method,
        path: path.to_owned(),
        query: query.to_owned(),
        headers,
        body: Vec::new(),
        peer,
    }, deadline))
}

const BASE_HEADERS: &str = concat!(
    "Cache-Control: no-store, no-cache, must-revalidate, max-age=0\r\n",
    "Pragma: no-cache\r\n",
    "Expires: 0\r\n",
    "Referrer-Policy: no-referrer\r\n",
    "X-Content-Type-Options: nosniff\r\n",
    "X-Frame-Options: DENY\r\n",
    "Content-Security-Policy: default-src 'self'; script-src 'self' 'unsafe-inline'; style-src 'self' 'unsafe-inline'; img-src 'self' data:; connect-src 'self'; base-uri 'none'; form-action 'none'; frame-ancestors 'none'\r\n"
);

fn reason(status: u16) -> &'static str {
    match status {
        200 => "OK",
        303 => "See Other",
        400 => "Bad Request",
        401 => "Unauthorized",
        403 => "Forbidden",
        404 => "Not Found",
        405 => "Method Not Allowed",
        408 => "Request Timeout",
        409 => "Conflict",
        410 => "Gone",
        413 => "Content Too Large",
        422 => "Unprocessable Content",
        429 => "Too Many Requests",
        431 => "Request Header Fields Too Large",
        500 => "Internal Server Error",
        503 => "Service Unavailable",
        507 => "Insufficient Storage",
        _ => "Error",
    }
}

fn respond(
    stream: &mut TcpStream,
    status: u16,
    content_type: &str,
    body: &[u8],
    extra: &[(&str, String)],
    is_head: bool,
) -> io::Result<()> {
    let mut headers = format!(
        "HTTP/1.1 {} {}\r\n{}Content-Type: {}\r\nContent-Length: {}\r\nConnection: close\r\n",
        status,
        reason(status),
        BASE_HEADERS,
        content_type,
        body.len()
    );
    for (name, value) in extra {
        headers.push_str(name);
        headers.push_str(": ");
        headers.push_str(value);
        headers.push_str("\r\n");
    }
    headers.push_str("\r\n");
    stream.write_all(headers.as_bytes())?;
    if !is_head {
        stream.write_all(body)?;
    }
    Ok(())
}

fn json_response(
    stream: &mut TcpStream,
    status: u16,
    value: &Value,
    is_head: bool,
) -> io::Result<()> {
    let body = serde_json::to_vec(value).map_err(io::Error::other)?;
    respond(
        stream,
        status,
        "application/json; charset=utf-8",
        &body,
        &[],
        is_head,
    )
}

fn error(stream: &mut TcpStream, status: u16, message: &str) -> io::Result<()> {
    json_response(stream, status, &json!({"error":message}), false)
}

fn redirect(stream: &mut TcpStream, token: Option<&str>, is_head: bool) -> io::Result<()> {
    let mut extra = vec![("Location", "/".to_owned())];
    if let Some(token) = token {
        extra.push((
            "Set-Cookie",
            format!("fi_session={token}; HttpOnly; SameSite=Lax; Path=/; Max-Age=31536000"),
        ));
    }
    respond(
        stream,
        303,
        "text/plain; charset=utf-8",
        b"Continue",
        &extra,
        is_head,
    )
}

struct EventSlot;
impl Drop for EventSlot {
    fn drop(&mut self) {
        ACTIVE_EVENTS.fetch_sub(1, Ordering::SeqCst);
    }
}

fn events(stream: &mut TcpStream, app: &App, token: Option<&str>, peer: IpAddr, cursor: Option<u64>) -> io::Result<()> {
    if !matches!(app.role(token, peer), Role::Desktop | Role::Phone) {
        return error(stream, 401, "Not paired");
    }
    if ACTIVE_EVENTS.fetch_add(1, Ordering::SeqCst) >= 32 {
        ACTIVE_EVENTS.fetch_sub(1, Ordering::SeqCst);
        return error(stream, 503, "Too many live connections");
    }
    let _slot = EventSlot;
    let _ = stream.set_nodelay(true);
    stream.write_all(format!(
        "HTTP/1.1 200 OK\r\n{}Content-Type: text/event-stream; charset=utf-8\r\nConnection: keep-alive\r\nX-Accel-Buffering: no\r\n\r\n",
        BASE_HEADERS
    ).as_bytes())?;
    stream.flush()?;
    let mut serial = 0;
    let credential = app.credential(token, peer).map(|(id, _)| id);
    let mut realtime_cursor = cursor.unwrap_or(0);
    let mut last_heartbeat = Instant::now();
    loop {
        let Some((event, next_serial)) = app.wait_event(token, peer, serial, credential.as_ref().map(|_| realtime_cursor)) else {
            break;
        };
        if next_serial != serial {
            let data = serde_json::to_string(&event).map_err(io::Error::other)?;
            stream.write_all(format!("data: {data}\n\n").as_bytes())?;
            stream.flush()?;
            serial = next_serial;
        } else if last_heartbeat.elapsed() >= Duration::from_secs(20) {
            stream.write_all(b": heartbeat\n\n")?;
            stream.flush()?;
            last_heartbeat = Instant::now();
        }
        if let Some(credential) = credential.as_deref() {
            let result = app.realtime.events(credential, realtime_cursor);
            if result["reset_required"] == true {
                let data = serde_json::to_string(&json!({
                    "event_id":result["event_id"],"reset_required":true,"code":"resync_required"
                })).map_err(io::Error::other)?;
                stream.write_all(format!("event: realtime\nid: {}\ndata: {data}\n\n", result["event_id"]).as_bytes())?;
            } else if let Some(events) = result["events"].as_array() {
                for event in events {
                    let data = serde_json::to_string(event).map_err(io::Error::other)?;
                    stream.write_all(format!("event: realtime\nid: {}\ndata: {data}\n\n", event["event_id"]).as_bytes())?;
                }
            }
            realtime_cursor = result["event_id"].as_u64().unwrap_or(realtime_cursor);
            stream.flush()?;
        }
    }
    Ok(())
}

fn id_from_body(request: &Request) -> Option<String> {
    let id = request.json()?.get("id")?.as_str()?.to_owned();
    if id.len() == 16 && id.bytes().all(|b| b.is_ascii_hexdigit()) {
        Some(id)
    } else {
        None
    }
}

fn read_body(stream: &mut TcpStream, request: &mut Request, deadline: Instant) -> Result<(), (u16, &'static str)> {
    if request.header("transfer-encoding").is_some() { return Err((400, "Chunked requests are unsupported")); }
    let len = request.header("content-length").map(str::parse::<usize>).transpose()
        .map_err(|_| (400, "Bad content length"))?.unwrap_or(0);
    if len > MAX_BODY { return Err((413, "Text is too large")); }
    request.body.resize(len, 0);
    let mut read = 0;
    while read < len {
        if Instant::now() >= deadline { return Err((408, "Request timeout")); }
        let count = stream.read(&mut request.body[read..]).map_err(|_| (400, "Incomplete body"))?;
        if count == 0 { return Err((400, "Incomplete body")); }
        read += count;
    }
    Ok(())
}

fn file_error(stream: &mut TcpStream, status: u16, code: &str, message: &str, head: bool) -> io::Result<()> {
    json_response(stream, status, &json!({"error":message,"code":code}), head)
}

fn file_store_error(stream: &mut TcpStream, error: crate::files::Error, head: bool) -> io::Result<()> {
    use crate::files::Error;
    let (status, code, message) = match error {
        Error::TooLarge => (413, "file_too_large", "檔案超過單檔上限"),
        Error::Quota => (507, "quota_exceeded", "檔案暫存區容量不足"),
        Error::Count => (409, "too_many_files", "檔案數量已達上限"),
        Error::Busy => (503, "busy", "同時上傳數已達上限，請稍後再試"),
        Error::Missing => (404, "not_found", "找不到檔案或檔案已過期"),
        Error::Io(e) => { eprintln!("File storage error: {e}"); (500, "storage_error", "檔案儲存失敗") },
    };
    file_error(stream, status, code, message, head)
}

fn handle_files(stream: &mut TcpStream, app: &App, mut request: Request, deadline: Instant) -> io::Result<()> {
    use crate::files;
    let head = request.method == "HEAD";
    let role = app.role(request.cookie(), request.peer);
    if !matches!(role, Role::Desktop | Role::Phone) {
        return file_error(stream, 401, "not_paired", "尚未配對", head);
    }
    if request.method == "POST" && request.header("origin") != request.header("host").map(|h| format!("http://{h}")).as_deref() {
        return file_error(stream, 403, "origin_rejected", "請求來源不符", head);
    }
    let length = match request.header("content-length") {
        Some(v) if !v.is_empty() && v.bytes().all(|b| b.is_ascii_digit()) => match v.parse::<u64>() {
            Ok(n) => Some(n),
            Err(_) => return file_error(stream, 400, "bad_request", "無效的 Content-Length", head),
        },
        None => None,
        _ => return file_error(stream, 400, "bad_request", "無效的 Content-Length", head),
    };
    if request.header("transfer-encoding").is_some() || (request.method == "POST" && length.is_none()) {
        return file_error(stream, 400, "bad_request", "必須提供 Content-Length，不支援分塊傳輸", head);
    }
    if let Err(e) = app.cleanup_files() { return file_store_error(stream, files::Error::Io(e), head); }
    match (request.method.as_str(), request.path.as_str()) {
        ("GET" | "HEAD", "/api/files") => json_response(stream, 200, &app.files.list(), head),
        ("POST", "/api/files") => {
            if length.unwrap() > files::MAX_FILE_BYTES { return file_store_error(stream, files::Error::TooLarge, head); }
            if request.header("content-type") != Some("application/octet-stream") {
                return file_error(stream, 400, "bad_request", "上傳格式必須為 application/octet-stream", head);
            }
            let name = request.query.split('&').filter_map(|p| p.split_once('='))
                .find(|(k, _)| *k == "name").and_then(|(_, v)| files::decode_name(v));
            let Some(name) = name else { return file_error(stream, 400, "bad_request", "缺少或無效的檔名", head); };
            let mut upload = match app.files.begin(length.unwrap()) {
                Ok(u) => u,
                Err(e) => return file_store_error(stream, e, head),
            };
            // Each successful read resets the socket's idle timeout. No overall upload deadline.
            stream.set_read_timeout(Some(Duration::from_secs(30)))?;
            if let Err(e) = upload.receive(stream) {
                drop(upload);
                return if matches!(e.kind(), io::ErrorKind::UnexpectedEof | io::ErrorKind::TimedOut | io::ErrorKind::WouldBlock | io::ErrorKind::ConnectionReset | io::ErrorKind::ConnectionAborted) {
                    file_error(stream, 400, "bad_request", "檔案內容不完整或上傳逾時", head)
                } else { file_store_error(stream, files::Error::Io(e), head) };
            }
            match upload.finish(files::sanitize_name(&name), if role == Role::Desktop { "desktop" } else { "phone" }) {
                Ok(value) => { app.files_changed(); json_response(stream, 200, &value, head) },
                Err(e) => file_store_error(stream, e, head),
            }
        }
        ("POST", "/api/files/delete") => {
            if request.header("content-type").is_none_or(|v| v.split(';').next().unwrap_or("").trim() != "application/json") || length.unwrap() > MAX_BODY as u64 {
                return file_error(stream, 400, "bad_request", "無效的刪除請求", head);
            }
            if read_body(stream, &mut request, deadline).is_err() { return file_error(stream, 400, "bad_request", "請求內容不完整", head); }
            let Some(value) = request.json() else { return file_error(stream, 400, "bad_request", "無效的 JSON", head); };
            let Some(id) = value.get("id").and_then(Value::as_str) else { return file_error(stream, 400, "bad_request", "缺少檔案識別碼", head); };
            match app.files.delete(id) {
                Ok(revision) => { app.files_changed(); json_response(stream, 200, &json!({"ok":true,"revision":revision}), head) },
                Err(e) => file_store_error(stream, e, head),
            }
        }
        ("GET" | "HEAD", path) if path.starts_with("/api/files/") => {
            let id = &path["/api/files/".len()..];
            let (entry, mut file) = match app.files.download(id) {
                Ok(v) => v,
                Err(e) => return file_store_error(stream, e, head),
            };
            write!(stream, "HTTP/1.1 200 OK\r\n{BASE_HEADERS}Content-Type: application/octet-stream\r\nContent-Length: {}\r\nContent-Disposition: {}\r\nConnection: close\r\n\r\n", entry.size, files::disposition(&entry.name))?;
            if !head {
                let mut buffer = [0u8; files::BUFFER_BYTES];
                let mut remaining = entry.size;
                while remaining > 0 {
                    let limit = remaining.min(buffer.len() as u64) as usize;
                    let count = file.read(&mut buffer[..limit])?;
                    if count == 0 { return Err(io::Error::new(io::ErrorKind::UnexpectedEof, "short disk file")); }
                    stream.write_all(&buffer[..count])?;
                    remaining -= count as u64;
                }
            }
            Ok(())
        }
        _ => file_error(stream, 404, "not_found", "找不到檔案端點", head),
    }
}

fn realtime_response(stream: &mut TcpStream, result: Result<Value, crate::realtime::RealtimeError>) -> io::Result<()> {
    match result {
        Ok(value) => json_response(stream, 200, &value, false),
        Err(mut error) => {
            error.body["code"] = json!(error.code);
            json_response(stream, error.status, &error.body, false)
        },
    }
}

fn handle_realtime(stream: &mut TcpStream, app: &App, mut request: Request, deadline: Instant) -> io::Result<()> {
    let Some((credential, _)) = app.credential(request.cookie(), request.peer) else {
        return file_error(stream, 401, "not_paired", "需要已配對手機憑證", false);
    };
    if !request.query.is_empty() {
        return file_error(stream, 400, "bad_request", "實時端點不接受 URL 參數", false);
    }
    if request.method == "GET" && request.path == "/api/realtime/status" {
        return realtime_response(stream, app.realtime.status(&credential));
    }
    if request.method != "POST" {
        return file_error(stream, 405, "method_not_allowed", "不支援的請求方法", false);
    }
    if !request.mutation_allowed() {
        return file_error(stream, 403, "origin_rejected", "請求來源或內容格式不符", false);
    }
    if !matches!(request.path.as_str(), "/api/realtime/request" | "/api/realtime/claim" | "/api/realtime/states" | "/api/realtime/heartbeat" | "/api/realtime/release" | "/api/realtime/enter") {
        return file_error(stream, 404, "not_found", "找不到實時端點", false);
    }
    let capability = request.header("x-fi-realtime").unwrap_or("").to_owned();
    if matches!(request.path.as_str(), "/api/realtime/states" | "/api/realtime/heartbeat" | "/api/realtime/release" | "/api/realtime/enter") {
        if let Err(error) = app.realtime.authorize(&credential, &capability) {
            return realtime_response(stream, Err(error));
        }
    }
    let length = request.header("content-length")
        .filter(|v| !v.is_empty() && v.bytes().all(|b| b.is_ascii_digit()))
        .and_then(|v| v.parse::<usize>().ok());
    let Some(length) = length else {
        return file_error(stream, 400, "bad_request", "必須提供有效的 Content-Length", false);
    };
    if request.header("transfer-encoding").is_some() {
        return file_error(stream, 400, "bad_request", "不支援分塊傳輸", false);
    }
    if length > crate::native_bridge::MAX_FRAME_BYTES {
        return file_error(stream, 413, "body_too_large", "實時請求超過 64 KiB", false);
    }
    if let Err((status, _)) = read_body(stream, &mut request, deadline) {
        return file_error(stream, status, "bad_request", "請求內容不完整", false);
    }
    let Some(body) = request.json() else {
        return file_error(stream, 400, "bad_request", "無效的 JSON 或 Unicode", false);
    };
    // Recheck after the body read and serialize the mutation with revocation.
    let result = app.with_credential(request.cookie(), request.peer, |credential, label| match request.path.as_str() {
        "/api/realtime/request" => app.realtime.request(credential, label, body),
        "/api/realtime/claim" => app.realtime.claim(credential, body),
        "/api/realtime/states" => app.realtime.states(credential, &capability, body),
        "/api/realtime/enter" => app.realtime.enter(credential, &capability, body),
        "/api/realtime/heartbeat" => app.realtime.heartbeat(credential, &capability, body),
        "/api/realtime/release" => app.realtime.release(credential, &capability, body),
        _ => unreachable!(),
    });
    let Some(result) = result else {
        return file_error(stream, 401, "not_paired", "配對憑證已撤銷", false);
    };
    realtime_response(stream, result)
}

pub fn handle(mut stream: TcpStream, app: Arc<App>) {
    let _ = stream.set_nonblocking(false);
    let _ = stream.set_read_timeout(Some(Duration::from_secs(15)));
    let _ = stream.set_write_timeout(Some(Duration::from_secs(10)));
    let mut file_request = false;
    let mut realtime_request = false;
    let (mut request, deadline) = match read_request(&mut stream, &mut file_request, &mut realtime_request) {
        Ok(request) => request,
        Err((status, message)) => {
            if message != "Client disconnected" {
                let _ = if file_request || realtime_request { file_error(&mut stream, status, "bad_request", "無效的請求標頭", false) } else { error(&mut stream, status, message) };
            }
            return;
        }
    };
    if !file_request && !realtime_request {
        if let Err((status, message)) = read_body(&mut stream, &mut request, deadline) {
            let _ = error(&mut stream, status, message);
            return;
        }
    }
    let Some(host) = request.header("host") else {
        let _ = if file_request || realtime_request { file_error(&mut stream, 400, "bad_request", "缺少 Host 標頭", request.method == "HEAD") } else { error(&mut stream, 400, "Missing Host header") };
        return;
    };
    if !app.host_allowed(host) {
        let _ = if file_request || realtime_request { file_error(&mut stream, 403, "origin_rejected", "不允許的主機", request.method == "HEAD") } else { error(&mut stream, 403, "Unknown host") };
        return;
    }
    if file_request {
        if let Err(e) = handle_files(&mut stream, &app, request, deadline) { eprintln!("File connection error: {e}"); }
        return;
    }
    if realtime_request {
        if let Err(e) = handle_realtime(&mut stream, &app, request, deadline) {
            if !matches!(e.kind(), io::ErrorKind::BrokenPipe | io::ErrorKind::ConnectionReset) { eprintln!("Realtime connection error: {e}"); }
        }
        return;
    }
    let is_head = request.method == "HEAD";
    let method = if is_head {
        "GET"
    } else {
        request.method.as_str()
    };
    let token = request.cookie();
    let peer = request.peer;
    let result = match (method, request.path.as_str()) {
        ("GET", "/") => respond(
            &mut stream,
            200,
            "text/html; charset=utf-8",
            include_str!("../web/index.html").as_bytes(),
            &[],
            is_head,
        ),
        ("GET", "/style.css") => respond(
            &mut stream,
            200,
            "text/css; charset=utf-8",
            include_str!("../web/style.css").as_bytes(),
            &[],
            is_head,
        ),
        ("GET", "/app-icon.svg") => respond(
            &mut stream,
            200,
            "image/svg+xml",
            include_str!("../assets/AppIcon.svg").as_bytes(),
            &[],
            is_head,
        ),
        ("GET", "/app.js") => respond(
            &mut stream,
            200,
            "text/javascript; charset=utf-8",
            include_str!("../web/app.js").as_bytes(),
            &[],
            is_head,
        ),
        ("GET", "/files.js") => respond(&mut stream, 200, "text/javascript; charset=utf-8", include_str!("../web/files.js").as_bytes(), &[], is_head),
        ("GET", "/files.css") => respond(&mut stream, 200, "text/css; charset=utf-8", include_str!("../web/files.css").as_bytes(), &[], is_head),
        ("GET", "/i18n.js") => respond(&mut stream, 200, "text/javascript; charset=utf-8", include_str!("../web/i18n.js").as_bytes(), &[], is_head),
        ("GET", "/realtime.js") => respond(&mut stream, 200, "text/javascript; charset=utf-8", include_str!("../web/realtime.js").as_bytes(), &[], is_head),
        ("GET", "/realtime.css") => respond(&mut stream, 200, "text/css; charset=utf-8", include_str!("../web/realtime.css").as_bytes(), &[], is_head),
        ("GET", "/desktop") => {
            if request
                .query_value("key")
                .is_some_and(|key| app.desktop_login(key, peer))
            {
                redirect(&mut stream, Some(&app.desktop_token()), is_head)
            } else {
                error(&mut stream, 403, "Invalid desktop link")
            }
        }
        ("GET", "/pair") => match app.role(token, peer) {
            Role::Phone | Role::Pending | Role::Desktop => redirect(&mut stream, None, is_head),
            Role::None => {
                if let Some(code) = request.query_value("code") {
                    if let Ok(pair_token) = app.accept_pin(code, peer) {
                        redirect(&mut stream, Some(&pair_token), is_head)
                    } else if let Some(pair_token) = app.accept_pair(code, peer) {
                        redirect(&mut stream, Some(&pair_token), is_head)
                    } else {
                        error(&mut stream, 403, "QR code expired or already used")
                    }
                } else {
                    error(&mut stream, 400, "Missing code")
                }
            }
        },
        ("GET", "/api/state") => json_response(&mut stream, 200, &app.state(token, peer), is_head),
        ("GET", "/api/pairing/qr.png") => match app.pairing_png(token, peer) {
            Ok(Some(png)) => respond(&mut stream, 200, "image/png", &png, &[], is_head),
            Ok(None) => error(&mut stream, 401, "Desktop access required"),
            Err(e) => {
                eprintln!("QR rendering failed: {e}");
                error(&mut stream, 500, "Could not render QR code")
            }
        },
        ("GET", "/api/events") => events(&mut stream, &app, token, peer, request.header("last-event-id").and_then(|s| s.parse().ok())),
        ("POST", path) if path.starts_with("/api/") => {
            if !request.mutation_allowed() {
                error(&mut stream, 403, "Origin or content type rejected")
            } else {
                match path {
                    "/api/text" => match serde_json::from_slice::<EditRequest>(&request.body) {
                        Ok(edit) => match app.edit(token, peer, edit) {
                            Ok(value) => json_response(&mut stream, 200, &value, false),
                            Err(EditError::Conflict(value)) => {
                                json_response(&mut stream, 409, &value, false)
                            }
                            Err(EditError::Unauthorized) => error(&mut stream, 401, "Not paired"),
                            Err(EditError::TooLarge) => {
                                error(&mut stream, 413, "Text is too large")
                            }
                            Err(EditError::Io(e)) => {
                                eprintln!("Save failed: {e}");
                                error(&mut stream, 500, "Could not save text")
                            }
                        },
                        Err(_) => error(&mut stream, 400, "Invalid edit"),
                    },
                    "/api/pairing/approve" | "/api/pairing/reject" | "/api/device/revoke" => {
                        let Some(id) = id_from_body(&request) else {
                            let _ = error(&mut stream, 400, "Invalid device id");
                            return;
                        };
                        let changed = match path {
                            "/api/pairing/approve" => app.approve(token, peer, &id),
                            "/api/device/revoke" => app.revoke(token, peer, &id),
                            _ => Ok(app.reject(token, peer, &id)),
                        };
                        match changed {
                            Ok(true) => json_response(&mut stream, 200, &json!({"ok":true}), false),
                            Ok(false) => {
                                error(&mut stream, 404, "Request not found or not authorized")
                            }
                            Err(e) => {
                                eprintln!("Save failed: {e}");
                                error(&mut stream, 500, "Could not save device")
                            }
                        }
                    }
                    "/api/pairing/rotate" => {
                        if app.rotate_pairing(token, peer) {
                            json_response(&mut stream, 200, &json!({"ok":true}), false)
                        } else {
                            error(&mut stream, 401, "Desktop access required")
                        }
                    }
                    "/api/pairing/pin" => {
                        #[derive(serde::Deserialize)]
                        struct PinBody {
                            pin: String,
                        }
                        match serde_json::from_slice::<PinBody>(&request.body) {
                            Ok(body) => match app.accept_pin(&body.pin, peer) {
                                Ok(pair_token) => {
                                    let cookie = format!(
                                        "fi_session={pair_token}; HttpOnly; SameSite=Lax; Path=/; Max-Age=31536000"
                                    );
                                    let extra = [("Set-Cookie", cookie)];
                                    respond(
                                        &mut stream,
                                        200,
                                        "application/json; charset=utf-8",
                                        b"{\"ok\":true}",
                                        &extra,
                                        false,
                                    )
                                }
                                Err(PinError::LockedOut(secs)) => error(
                                    &mut stream,
                                    429,
                                    &format!("嘗試次數過多，請於 {secs} 秒後再試"),
                                ),
                                Err(PinError::InvalidPin) => {
                                    error(&mut stream, 403, "配對碼錯誤，請重新確認主機畫面")
                                }
                                Err(PinError::Expired) => {
                                    error(&mut stream, 403, "配對碼已過期，請重新取得最新配對碼")
                                }
                                Err(PinError::TooManyDevices) => {
                                    error(&mut stream, 400, "已達裝置數量上限")
                                }
                                Err(PinError::Io) => error(&mut stream, 500, "儲存裝置失敗"),
                            },
                            Err(_) => error(&mut stream, 400, "無效的配對請求"),
                        }
                    }
                    "/api/shutdown" => {
                        if app.role(token, peer) == Role::Desktop {
                            let response =
                                json_response(&mut stream, 200, &json!({"ok":true}), false);
                            if response.is_ok() {
                                app.request_shutdown(token, peer);
                            }
                            response
                        } else {
                            error(&mut stream, 401, "Desktop access required")
                        }
                    }
                    _ => error(&mut stream, 404, "Not found"),
                }
            }
        }
        ("GET", "/favicon.ico") => respond(&mut stream, 404, "text/plain", b"", &[], is_head),
        ("GET", _) => error(&mut stream, 404, "Not found"),
        _ => error(&mut stream, 405, "Method not allowed"),
    };
    if let Err(e) = result {
        if !matches!(
            e.kind(),
            io::ErrorKind::BrokenPipe | io::ErrorKind::ConnectionReset
        ) {
            eprintln!("Connection error: {e}");
        }
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn i18n_static_route_get_and_head() {
        use std::net::{Ipv4Addr, TcpListener};
        let listener = TcpListener::bind((Ipv4Addr::LOCALHOST, 0)).unwrap();
        let port = listener.local_addr().unwrap().port();
        let directory = std::env::temp_dir().join(format!("fastinput-i18n-{}-{port}", std::process::id()));
        std::fs::create_dir_all(&directory).unwrap();
        let app = Arc::new(App::new(directory.join("state.json"), Ipv4Addr::LOCALHOST, port).unwrap());
        let worker = std::thread::spawn(move || {
            for _ in 0..2 { handle(listener.accept().unwrap().0, app.clone()); }
        });
        for method in ["GET", "HEAD"] {
            let mut stream = TcpStream::connect((Ipv4Addr::LOCALHOST, port)).unwrap();
            stream.set_read_timeout(Some(Duration::from_secs(5))).unwrap();
            write!(stream, "{method} /i18n.js HTTP/1.1\r\nHost: 127.0.0.1:{port}\r\n\r\n").unwrap();
            let mut response = String::new();
            stream.read_to_string(&mut response).unwrap();
            let (headers, body) = response.split_once("\r\n\r\n").unwrap();
            assert!(headers.starts_with("HTTP/1.1 200"));
            assert!(headers.contains("text/javascript; charset=utf-8"));
            assert!(headers.contains("Cache-Control: no-store"));
            assert!(headers.contains(&format!("Content-Length: {}", include_str!("../web/i18n.js").len())));
            if method == "HEAD" { assert!(body.is_empty()); }
            else { assert_eq!(body, include_str!("../web/i18n.js")); }
        }
        worker.join().unwrap();
        std::fs::remove_dir_all(directory).unwrap();
    }

    #[test]
    fn cookie_parser_rejects_partial_token() {
        let request = Request {
            method: "GET".into(),
            path: "/".into(),
            query: String::new(),
            headers: HashMap::from([("cookie".into(), "fi_session=abc".into())]),
            body: vec![],
            peer: IpAddr::V4(std::net::Ipv4Addr::LOCALHOST),
        };
        assert!(request.cookie().is_none());
    }

    #[test]
    fn cookie_parser_accepts_valid_hex_token() {
        let hex64 = "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";
        let request = Request {
            method: "GET".into(),
            path: "/".into(),
            query: String::new(),
            headers: HashMap::from([(
                "cookie".into(),
                format!("foo=bar; fi_session={hex64}; baz=1"),
            )]),
            body: vec![],
            peer: IpAddr::V4(std::net::Ipv4Addr::LOCALHOST),
        };
        assert_eq!(request.cookie(), Some(hex64));
    }

    #[test]
    fn request_query_and_mutation_check() {
        let request = Request {
            method: "POST".into(),
            path: "/pair".into(),
            query: "code=123456&other=val&pin=839201".into(),
            headers: HashMap::from([
                ("host".into(), "127.0.0.1:8765".into()),
                ("origin".into(), "http://127.0.0.1:8765".into()),
                ("content-type".into(), "application/json".into()),
            ]),
            body: vec![],
            peer: IpAddr::V4(std::net::Ipv4Addr::LOCALHOST),
        };
        assert_eq!(request.query_value("code"), Some("123456"));
        assert_eq!(request.query_value("pin"), Some("839201"));
        assert_eq!(request.query_value("other"), Some("val"));
        assert_eq!(request.query_value("missing"), None);
        assert!(request.mutation_allowed());
        assert_eq!(reason(408), "Request Timeout");
        assert_eq!(reason(429), "Too Many Requests");
    }
}
