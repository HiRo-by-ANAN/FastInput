//! Wire-level tests use the production accept loop and handwritten HTTP over TCP.
use crate::{files, state::App};
use rand::{rngs::OsRng, RngCore};
use serde_json::{json, Value};
use std::{
    collections::HashMap,
    fs::{self, File},
    io::{BufRead, BufReader, Read, Write},
    net::{IpAddr, Ipv4Addr, Shutdown, TcpListener, TcpStream},
    path::PathBuf,
    sync::Arc,
    thread::{self, JoinHandle},
    time::{Duration, Instant},
};

struct Server { app: Arc<App>, port: u16, dir: PathBuf, accept: Option<JoinHandle<()>> }
impl Server {
    fn new() -> Self { Self::seeded(&[]) }
    fn seeded(sizes: &[u64]) -> Self {
        Self::with_expiry(sizes, files::now() + files::TTL_SECONDS)
    }
    fn with_expiry(sizes: &[u64], expires_at: u64) -> Self {
        let listener = TcpListener::bind((Ipv4Addr::LOCALHOST, 0)).unwrap();
        let port = listener.local_addr().unwrap().port();
        let dir = std::env::temp_dir().join(format!("fastinput-http-{:016x}", OsRng.next_u64()));
        fs::create_dir_all(dir.join("files")).unwrap();
        let entries: Vec<_> = sizes.iter().enumerate().map(|(i, &size)| {
            let id = format!("{i:016x}");
            let file = File::create(dir.join("files").join(format!("{id}.bin"))).unwrap();
            make_sparse(&file);
            file.set_len(size).unwrap();
            json!({"id":id,"name":"seed.bin","size":size,"created_at":files::now(),"expires_at":expires_at,"source":"phone"})
        }).collect();
        fs::write(dir.join("files/index.json"), serde_json::to_vec(&json!({"files":entries,"revision":0})).unwrap()).unwrap();
        let app = Arc::new(App::new(dir.join("state.json"), Ipv4Addr::LOCALHOST, port).unwrap());
        let server_app = app.clone();
        let accept = Some(thread::spawn(move || crate::accept_connections(listener, server_app)));
        Self { app, port, dir, accept }
    }
    fn connect(&self) -> TcpStream {
        let stream = TcpStream::connect((Ipv4Addr::LOCALHOST, self.port)).unwrap();
        stream.set_read_timeout(Some(Duration::from_secs(3))).unwrap();
        stream.set_write_timeout(Some(Duration::from_secs(5))).unwrap();
        stream
    }
    fn headers(&self, method: &str, path: &str, token: Option<&str>, extra: &str) -> String {
        format!("{method} {path} HTTP/1.1\r\nHost: 127.0.0.1:{}\r\n{}{}\r\n", self.port,
            token.map(|t| format!("Cookie: fi_session={t}\r\n")).unwrap_or_default(), extra)
    }
    fn post_headers(&self, path: &str, len: u64, token: Option<&str>, content_type: &str) -> String {
        self.headers("POST", path, token, &format!("Origin: http://127.0.0.1:{}\r\nContent-Type: {content_type}\r\nContent-Length: {len}\r\n", self.port))
    }
    fn request(&self, headers: &str, body: &[u8]) -> Response {
        let mut stream = self.connect();
        stream.write_all(headers.as_bytes()).unwrap();
        stream.write_all(body).unwrap();
        let mut bytes = Vec::new();
        if let Err(e) = stream.read_to_end(&mut bytes) {
            // Rejecting a malformed header before its terminator can reset TCP on Windows.
            // Only accept that reset when the complete declared JSON response arrived.
            assert_eq!(e.kind(), std::io::ErrorKind::ConnectionReset, "server must reply without waiting for a rejected body: {e}");
            let response = Response::parse(&bytes);
            assert_eq!(response.body.len().to_string(), response.headers["content-length"]);
        }
        Response::parse(&bytes)
    }
    fn get(&self, path: &str, token: &str) -> Response {
        self.request(&self.headers("GET", path, Some(token), ""), &[])
    }
    fn parts(&self) -> usize {
        fs::read_dir(self.dir.join("files")).unwrap().filter_map(Result::ok)
            .filter(|e| e.path().extension().is_some_and(|s| s == "part")).count()
    }
    fn wait_parts(&self, count: usize) {
        let start = Instant::now();
        while self.parts() != count {
            assert!(start.elapsed() < Duration::from_secs(3), "expected {count} part files, got {}", self.parts());
            thread::sleep(Duration::from_millis(10));
        }
    }
}
impl Drop for Server {
    fn drop(&mut self) {
        self.app.request_shutdown(Some(&self.app.desktop_token()), IpAddr::V4(Ipv4Addr::LOCALHOST));
        self.accept.take().unwrap().join().unwrap();
        // Any disconnected upload worker has its own RAII cleanup.
        let start = Instant::now();
        while self.parts() != 0 && start.elapsed() < Duration::from_secs(3) { thread::sleep(Duration::from_millis(10)); }
        fs::remove_dir_all(&self.dir).unwrap();
    }
}

#[cfg(windows)]
fn make_sparse(file: &File) {
    use std::os::windows::io::AsRawHandle;
    #[link(name = "kernel32")]
    extern "system" { fn DeviceIoControl(handle: *mut std::ffi::c_void, code: u32, input: *const u8, input_len: u32, output: *mut u8, output_len: u32, returned: *mut u32, overlapped: *mut u8) -> i32; }
    let mut returned = 0;
    assert_ne!(unsafe { DeviceIoControl(file.as_raw_handle(), 0x900c4, std::ptr::null(), 0, std::ptr::null_mut(), 0, &mut returned, std::ptr::null_mut()) }, 0, "create sparse quota fixture");
}
#[cfg(not(windows))]
fn make_sparse(_: &File) {}

struct Response { status: u16, headers: HashMap<String, String>, body: Vec<u8> }
impl Response {
    fn parse(bytes: &[u8]) -> Self {
        let end = bytes.windows(4).position(|s| s == b"\r\n\r\n").expect("HTTP response headers");
        let text = std::str::from_utf8(&bytes[..end]).unwrap();
        let mut lines = text.split("\r\n");
        let status = lines.next().unwrap().split_whitespace().nth(1).unwrap().parse().unwrap();
        let headers = lines.map(|l| { let (k,v) = l.split_once(':').unwrap(); (k.to_ascii_lowercase(), v.trim().to_owned()) }).collect();
        Self { status, headers, body: bytes[end+4..].to_vec() }
    }
    fn json(&self) -> Value { serde_json::from_slice(&self.body).unwrap() }
    fn error(&self, status: u16, code: &str) {
        assert_eq!(self.status, status, "{}", String::from_utf8_lossy(&self.body));
        let value = self.json();
        assert_eq!(value["code"], code);
        assert!(value["error"].as_str().is_some_and(|m| !m.is_ascii()));
        println!("HTTP {status} {code}: verified");
    }
}

#[test]
fn http_upload_list_download_head_delete_and_phone_source() {
    let server = Server::new();
    let desktop = server.app.desktop_token();
    let peer = IpAddr::V4(Ipv4Addr::LOCALHOST);
    let state = server.app.state(Some(&desktop), peer);
    let phone = server.app.accept_pin(state["pairing"]["pin"].as_str().unwrap(), peer).unwrap();
    let body: Vec<u8> = (0..200_003).map(|i| (i % 251) as u8).collect();
    let uploaded = server.request(&server.post_headers("/api/files?name=..%2F%E7%85%A7%E7%89%87.bin", body.len() as u64, Some(&phone), "application/octet-stream"), &body);
    assert_eq!(uploaded.status, 200);
    let value = uploaded.json();
    assert_eq!(value["file"]["name"], "照片.bin");
    assert_eq!(value["file"]["source"], "phone");
    assert_eq!(value["file"]["expires_at"].as_u64().unwrap() - value["file"]["created_at"].as_u64().unwrap(), files::TTL_SECONDS);
    let id = value["file"]["id"].as_str().unwrap();
    assert!(files::valid_id(id));
    let list = server.get("/api/files", &desktop).json();
    assert_eq!(list["files"].as_array().unwrap().len(), 1);
    assert_eq!(list["used_bytes"], body.len());
    assert_eq!(list["max_file_bytes"], files::MAX_FILE_BYTES);
    assert_eq!(list["max_total_bytes"], files::MAX_TOTAL_BYTES);
    assert_eq!(list["max_files"], files::MAX_FILES);
    assert_eq!(list["ttl_seconds"], files::TTL_SECONDS);
    assert_eq!(list["revision"], 1);
    let path = format!("/api/files/{id}");
    let downloaded = server.get(&path, &desktop);
    assert_eq!(downloaded.status, 200);
    assert_eq!(downloaded.body, body);
    assert_eq!(downloaded.headers["content-length"], body.len().to_string());
    assert_eq!(downloaded.headers["content-type"], "application/octet-stream");
    assert_eq!(downloaded.headers["x-content-type-options"], "nosniff");
    assert_eq!(downloaded.headers["content-disposition"], "attachment; filename=\"__.bin\"; filename*=UTF-8''%E7%85%A7%E7%89%87.bin");
    let head = server.request(&server.headers("HEAD", &path, Some(&phone), ""), &[]);
    assert_eq!(head.status, 200);
    assert_eq!(head.headers["content-length"], body.len().to_string());
    assert_eq!(head.headers["content-disposition"], downloaded.headers["content-disposition"]);
    assert!(head.body.is_empty());
    assert_eq!(server.get("/api/state", &desktop).json()["files_revision"], 1);
    let deletion = serde_json::to_vec(&json!({"id":id})).unwrap();
    let deleted = server.request(&server.post_headers("/api/files/delete", deletion.len() as u64, Some(&phone), "application/json"), &deletion);
    assert_eq!(deleted.json(), json!({"ok":true,"revision":2}));
    assert_eq!(server.get("/api/files", &desktop).json()["used_bytes"], 0);
    server.get(&path, &desktop).error(404, "not_found");
    server.request(&server.post_headers("/api/files/delete", deletion.len() as u64, Some(&phone), "application/json"), &deletion).error(404, "not_found");
    let invalid = br#"{"id":"0123456789abcdeF"}"#;
    server.request(&server.post_headers("/api/files/delete", invalid.len() as u64, Some(&phone), "application/json"), invalid).error(404, "not_found");
    let bad_head = server.request(&server.headers("HEAD", &path, Some(&phone), ""), &[]);
    assert_eq!(bad_head.status, 404);
    assert!(bad_head.body.is_empty());
    for (path, content_type) in [("/files.js", "text/javascript; charset=utf-8"), ("/files.css", "text/css; charset=utf-8")] {
        let result = server.get(path, &desktop);
        assert_eq!(result.status, 200);
        assert_eq!(result.headers["content-type"], content_type);
    }
}

#[test]
fn http_rejections_before_body_and_security() {
    let server = Server::new();
    let token = server.app.desktop_token();
    let desktop_state = server.app.state(Some(&token), IpAddr::V4(Ipv4Addr::LOCALHOST));
    // QR pairing creates a pending identity until the desktop approves it.
    let code = desktop_state["pairing"]["url"].as_str().unwrap().split("code=").nth(1).unwrap();
    let pending = server.app.accept_pair(code, IpAddr::V4(Ipv4Addr::new(192,168,1,77))).unwrap();
    server.request(&server.post_headers("/api/files?name=x", 100, Some(&pending), "application/octet-stream"), &[]).error(401, "not_paired");
    // Send headers only, keep the write half open. A server reading a rejected body would time out this test.
    server.request(&server.post_headers("/api/files?name=x", 100, None, "application/octet-stream"), &[]).error(401, "not_paired");
    for path in ["/api/files", "/api/files/0123456789abcdef", "/api/files/delete", "/api/files-invalid"] {
        server.request(&server.headers("GET", path, None, ""), &[]).error(401, "not_paired");
    }
    let headers = server.post_headers("/api/files?name=x", 100, Some(&token), "application/octet-stream");
    server.request(&headers.replace(&format!("Origin: http://127.0.0.1:{}", server.port), "Origin: http://evil.test"), &[]).error(403, "origin_rejected");
    server.request(&headers.replace(&format!("Origin: http://127.0.0.1:{}\r\n", server.port), ""), &[]).error(403, "origin_rejected");
    server.request(&headers.replace("Content-Length: 100", "Content-Length: 100\r\nOrigin: http://evil.test"), &[]).error(403, "origin_rejected");
    let delete = server.post_headers("/api/files/delete", 100, Some(&token), "application/json");
    server.request(&server.post_headers("/api/files/delete", 100, None, "application/json"), &[]).error(401, "not_paired");
    server.request(&delete.replace(&format!("Origin: http://127.0.0.1:{}", server.port), "Origin: http://evil.test"), &[]).error(403, "origin_rejected");
    server.request(&delete.replace("Content-Length: 100\r\n", ""), &[]).error(400, "bad_request");
    server.request(&server.post_headers("/api/files?name=x", files::MAX_FILE_BYTES+1, Some(&token), "application/octet-stream"), &[]).error(413, "file_too_large");
    for changed in [
        headers.replace("?name=x", ""), headers.replace("?name=x", "?name=%FF"),
        headers.replace("Content-Length: 100\r\n", ""), headers.replace("Content-Length: 100", "Content-Length: -1"),
        headers.replace("Content-Length: 100", "Content-Length: 100\r\nContent-Length: 100"),
        headers.replace("Content-Length: 100", "Transfer-Encoding: chunked"),
        headers.replace("application/octet-stream", "text/plain"),
    ] { server.request(&changed, &[]).error(400, "bad_request"); }
    let wrong_host = headers.replace(&format!("Host: 127.0.0.1:{}", server.port), "Host: evil.test");
    server.request(&wrong_host, &[]).error(403, "origin_rejected");
    for id in ["0123456789abcdeF", "../../state.json", "%2e%2e%2fstate.json", "0123456789abcdef", "x"] {
        server.get(&format!("/api/files/{id}"), &token).error(404, "not_found");
    }
    let huge_headers = headers.replace("Content-Length: 100", &format!("Content-Length: 100\r\n{}", (0..5).map(|n| format!("X-{n}: {}\r\n", "x".repeat(7000))).collect::<String>()));
    // Stop exactly after the over-limit header line. The server must reject before
    // the header terminator, so do not leave an unread terminator that resets TCP.
    let huge_headers = huge_headers.trim_end_matches(['\r', '\n']).to_owned() + "\r\n";
    server.request(&huge_headers, &[]).error(431, "bad_request");
    assert_eq!(server.parts(), 0);
    assert_eq!(server.get("/api/files", &token).json()["revision"], 0);
}

#[test]
fn http_short_body_and_disconnect_cleanup() {
    let server = Server::new();
    let token = server.app.desktop_token();
    let mut socket = server.connect();
    socket.write_all(server.post_headers("/api/files?name=x", 1000, Some(&token), "application/octet-stream").as_bytes()).unwrap();
    socket.write_all(b"short").unwrap();
    server.wait_parts(1);
    socket.shutdown(Shutdown::Write).unwrap();
    let mut bytes = Vec::new();
    socket.read_to_end(&mut bytes).unwrap();
    Response::parse(&bytes).error(400, "bad_request");
    server.wait_parts(0);
    let mut socket = server.connect();
    socket.write_all(server.post_headers("/api/files?name=x", 1000, Some(&token), "application/octet-stream").as_bytes()).unwrap();
    socket.write_all(b"partial").unwrap();
    server.wait_parts(1);
    drop(socket);
    server.wait_parts(0);
    assert_eq!(server.get("/api/files", &token).json()["used_bytes"], 0);
    assert_eq!(server.get("/api/files", &token).json()["revision"], 0);
}

#[test]
fn http_quota_507_and_file_count_409() {
    let mut sizes = vec![files::MAX_FILE_BYTES; 10];
    sizes.push(files::MAX_TOTAL_BYTES - 10 * files::MAX_FILE_BYTES - 1);
    let server = Server::seeded(&sizes);
    let token = server.app.desktop_token();
    server.request(&server.post_headers("/api/files?name=x", 2, Some(&token), "application/octet-stream"), &[]).error(507, "quota_exceeded");
    let at_limit = server.request(&server.post_headers("/api/files?name=x", 1, Some(&token), "application/octet-stream"), b"x");
    assert_eq!(at_limit.status, 200);
    assert_eq!(server.get("/api/files", &token).json()["used_bytes"], files::MAX_TOTAL_BYTES);
    drop(server);
    let server = Server::seeded(&vec![0; files::MAX_FILES]);
    let token = server.app.desktop_token();
    server.request(&server.post_headers("/api/files?name=x", 0, Some(&token), "application/octet-stream"), &[]).error(409, "too_many_files");
}

#[test]
fn http_busy_503_and_text_remains_responsive() {
    let server = Server::new();
    let token = server.app.desktop_token();
    let mut uploads = Vec::new();
    for n in 0..files::MAX_UPLOADS {
        let mut socket = server.connect();
        socket.write_all(server.post_headers("/api/files?name=x", 1000, Some(&token), "application/octet-stream").as_bytes()).unwrap();
        uploads.push(socket);
        server.wait_parts(n+1);
    }
    server.request(&server.post_headers("/api/files?name=x", 1, Some(&token), "application/octet-stream"), &[]).error(503, "busy");
    let state = server.get("/api/state", &token).json();
    let edit = serde_json::to_vec(&json!({"text":"still responsive","revision":state["revision"],"generation":state["generation"]})).unwrap();
    let start = Instant::now();
    let result = server.request(&server.post_headers("/api/text", edit.len() as u64, Some(&token), "application/json"), &edit);
    assert_eq!(result.status, 200);
    assert_eq!(result.json()["text"], "still responsive");
    assert!(start.elapsed() < Duration::from_secs(2));
    println!("text mutation during 4 stalled uploads: {} ms", start.elapsed().as_millis());
    drop(uploads);
    server.wait_parts(0);
    let success = server.request(&server.post_headers("/api/files?name=empty", 0, Some(&token), "application/octet-stream"), &[]);
    assert_eq!(success.status, 200);
    assert_eq!(success.json()["file"]["source"], "desktop");
}

fn read_event(reader: &mut BufReader<TcpStream>) -> Value {
    loop {
        let mut line = String::new();
        assert!(reader.read_line(&mut line).unwrap() > 0);
        if let Some(data) = line.strip_prefix("data: ") { return serde_json::from_str(data).unwrap(); }
    }
}

#[test]
fn http_events_wake_on_add_delete_and_expiry() {
    let server = Server::new();
    let token = server.app.desktop_token();
    let mut socket = server.connect();
    socket.write_all(server.headers("GET", "/api/events", Some(&token), "").as_bytes()).unwrap();
    let mut reader = BufReader::new(socket);
    assert_eq!(read_event(&mut reader)["files_revision"], 0);
    let uploaded = server.request(&server.post_headers("/api/files?name=a", 1, Some(&token), "application/octet-stream"), b"a").json();
    assert_eq!(read_event(&mut reader)["files_revision"], 1);
    let body = serde_json::to_vec(&json!({"id":uploaded["file"]["id"]})).unwrap();
    assert_eq!(server.request(&server.post_headers("/api/files/delete", body.len() as u64, Some(&token), "application/json"), &body).status, 200);
    assert_eq!(read_event(&mut reader)["files_revision"], 2);
    let uploaded = server.request(&server.post_headers("/api/files?name=b", 1, Some(&token), "application/octet-stream"), b"b").json();
    assert_eq!(read_event(&mut reader)["files_revision"], 3);
    assert!(server.app.files.cleanup(uploaded["file"]["expires_at"].as_u64().unwrap()).unwrap());
    server.app.files_changed();
    assert_eq!(read_event(&mut reader)["files_revision"], 4);
    assert_eq!(server.get("/api/state", &token).json()["files_revision"], 4);
    assert_eq!(server.get("/api/files", &token).json()["files"], json!([]));
}

#[cfg(windows)]
fn memory() -> (usize, usize) {
    #[repr(C)]
    struct Counters { cb: u32, faults: u32, peak: usize, current: usize, other: [usize; 6] }
    #[link(name = "psapi")]
    extern "system" { fn GetProcessMemoryInfo(process: *mut std::ffi::c_void, counters: *mut Counters, size: u32) -> i32; }
    let mut counters = Counters { cb: std::mem::size_of::<Counters>() as u32, faults: 0, peak: 0, current: 0, other: [0;6] };
    assert_ne!(unsafe { GetProcessMemoryInfo(-1isize as *mut _, &mut counters, std::mem::size_of::<Counters>() as u32) }, 0);
    (counters.current, counters.peak)
}

#[test]
fn http_streaming_64_mib_fixed_memory() {
    let server = Server::new();
    let token = server.app.desktop_token();
    #[cfg(windows)]
    let before = memory();
    let mut buffer = [0u8; files::BUFFER_BYTES];
    for (n, b) in buffer.iter_mut().enumerate() { *b = (n % 251) as u8; }
    let mut last = Value::Null;
    #[cfg(windows)]
    let mut warm_peak = 0;
    for size in [1u64 << 20, 64u64 << 20] {
        let mut socket = server.connect();
        socket.set_read_timeout(Some(Duration::from_secs(10))).unwrap();
        let start = Instant::now();
        socket.write_all(server.post_headers("/api/files?name=large.bin", size, Some(&token), "application/octet-stream").as_bytes()).unwrap();
        for _ in 0..size / buffer.len() as u64 { socket.write_all(&buffer).unwrap(); }
        let mut response = Vec::new();
        socket.read_to_end(&mut response).unwrap();
        let result = Response::parse(&response);
        assert_eq!(result.status, 200);
        last = result.json();
        assert_eq!(last["file"]["size"], size);
        #[cfg(windows)]
        {
            let measured = memory();
            println!("STREAM size_bytes={size} upload_ms={} fixed_buffer_bytes={} memory_before_bytes={} peak_before_bytes={} memory_now_bytes={} peak_bytes={}", start.elapsed().as_millis(), files::BUFFER_BYTES, before.0, before.1, measured.0, measured.1);
            if size == 1 << 20 { warm_peak = measured.1; }
            else { assert!(measured.1.saturating_sub(warm_peak) < 16 << 20, "streaming must not allocate memory proportional to 64 MiB"); }
        }
    }
    // Download the entire large file with a bounded client buffer and verify every byte.
    let id = last["file"]["id"].as_str().unwrap();
    let mut socket = server.connect();
    socket.write_all(server.headers("GET", &format!("/api/files/{id}"), Some(&token), "").as_bytes()).unwrap();
    let mut reader = BufReader::new(socket);
    loop { let mut line = String::new(); reader.read_line(&mut line).unwrap(); if line == "\r\n" { break; } }
    let mut received = [0u8; files::BUFFER_BYTES];
    for _ in 0..(64 << 20) / buffer.len() { reader.read_exact(&mut received).unwrap(); assert_eq!(received, buffer); }
    assert_eq!(reader.read(&mut received).unwrap(), 0);
    assert_eq!(files::BUFFER_BYTES, 65_536);
    #[cfg(windows)]
    println!("STREAM completed upload+download peak_bytes={}", memory().1);
}

#[test]
fn http_upload_idle_timeout_and_no_overall_deadline() {
    let server = Server::new();
    let token = server.app.desktop_token();
    let mut socket = server.connect();
    socket.write_all(server.post_headers("/api/files?name=slow", 2, Some(&token), "application/octet-stream").as_bytes()).unwrap();
    socket.write_all(b"a").unwrap();
    server.wait_parts(1);
    let start = Instant::now();
    thread::sleep(Duration::from_secs(16));
    socket.write_all(b"b").unwrap();
    let mut bytes = Vec::new();
    socket.read_to_end(&mut bytes).unwrap();
    assert_eq!(Response::parse(&bytes).status, 200);
    println!("SLOW successful upload elapsed_ms={} (exceeds old 15s deadline)", start.elapsed().as_millis());
    let mut socket = server.connect();
    socket.set_read_timeout(Some(Duration::from_secs(36))).unwrap();
    socket.write_all(server.post_headers("/api/files?name=idle", 2, Some(&token), "application/octet-stream").as_bytes()).unwrap();
    socket.write_all(b"a").unwrap();
    server.wait_parts(1);
    let idle = Instant::now();
    let mut bytes = Vec::new();
    socket.read_to_end(&mut bytes).unwrap();
    Response::parse(&bytes).error(400, "bad_request");
    assert!(idle.elapsed() >= Duration::from_secs(29) && idle.elapsed() < Duration::from_secs(36));
    server.wait_parts(0);
    println!("IDLE aborted and part removed after_ms={}", idle.elapsed().as_millis());
}

#[test]
fn http_background_cleanup_after_60_seconds() {
    let server = Server::with_expiry(&[1], files::now() + 2);
    let token = server.app.desktop_token();
    let mut socket = server.connect();
    socket.set_read_timeout(Some(Duration::from_secs(65))).unwrap();
    socket.write_all(server.headers("GET", "/api/events", Some(&token), "").as_bytes()).unwrap();
    let mut reader = BufReader::new(socket);
    assert_eq!(read_event(&mut reader)["files_revision"], 0);
    let start = Instant::now();
    App::start_file_cleanup(&server.app);
    assert_eq!(read_event(&mut reader)["files_revision"], 1);
    assert!(start.elapsed() >= Duration::from_secs(59));
    assert!(start.elapsed() < Duration::from_secs(65));
    assert!(!server.dir.join("files/0000000000000000.bin").exists());
    assert_eq!(server.get("/api/files", &token).json()["used_bytes"], 0);
    println!("BACKGROUND expired file removed and SSE woke after_ms={}", start.elapsed().as_millis());
}

#[test]
fn http_expired_download_404_and_list_cleanup() {
    let download_server = Server::with_expiry(&[1], files::now() + 1);
    let list_server = Server::with_expiry(&[1], files::now() + 1);
    thread::sleep(Duration::from_millis(1100));
    download_server.get("/api/files/0000000000000000", &download_server.app.desktop_token()).error(404, "not_found");
    let list = list_server.get("/api/files", &list_server.app.desktop_token()).json();
    assert_eq!(list["files"], json!([]));
    assert_eq!(list["revision"], 1);
    assert!(!list_server.dir.join("files/0000000000000000.bin").exists());
}
