mod http;
mod files;
mod state;
mod credential;
mod realtime;
mod native_bridge;
#[cfg(test)]
mod files_tests;
#[cfg(test)]
mod realtime_tests;

use if_addrs::get_if_addrs;
use state::App;
use std::{
    env, fs, io,
    net::{IpAddr, Ipv4Addr, SocketAddr, TcpListener},
    path::PathBuf,
    process::Command,
    sync::{
        atomic::{AtomicUsize, Ordering},
        Arc,
    },
    thread,
    time::Duration,
};

static ACTIVE_CONNECTIONS: AtomicUsize = AtomicUsize::new(0);

struct ConnectionSlot;
impl Drop for ConnectionSlot {
    fn drop(&mut self) {
        ACTIVE_CONNECTIONS.fetch_sub(1, Ordering::SeqCst);
    }
}

struct Options {
    port: u16,
    lan_ip: Option<Ipv4Addr>,
    open_browser: bool,
    native_stdio: bool,
}

fn usage() -> &'static str {
    "FastInput — 在同一個內網用手機與電腦同步文字\n\
     用法：fastinput [--port 8765] [--lan-ip 192.168.1.2] [--no-browser]\n\
     --port        伺服器連接埠，預設 8765\n\
     --lan-ip      電腦在內網的 IPv4 位址；多張網卡時可手動指定\n\
     --no-browser  不自動開啟電腦網頁\n"
}

fn parse_options() -> Result<Options, String> {
    let mut options = Options {
        port: 8765,
        lan_ip: None,
        open_browser: true,
        native_stdio: false,
    };
    let mut args = env::args().skip(1);
    while let Some(arg) = args.next() {
        match arg.as_str() {
            "--help" | "-h" => {
                print!("{}", usage());
                std::process::exit(0);
            }
            "--port" => {
                let value = args.next().ok_or("--port needs a number")?;
                options.port = value.parse::<u16>().map_err(|_| "Invalid port")?;
                if options.port == 0 {
                    return Err("Port must be above zero".into());
                }
            }
            "--lan-ip" => {
                let value = args.next().ok_or("--lan-ip needs an IPv4 address")?;
                let ip = value
                    .parse::<Ipv4Addr>()
                    .map_err(|_| "Invalid IPv4 address")?;
                if !ip.is_private() && !ip.is_loopback() {
                    return Err("--lan-ip must be a private LAN address".into());
                }
                options.lan_ip = Some(ip);
            }
            "--no-browser" => options.open_browser = false,
            "--native-stdio" => options.native_stdio = true,
            _ => return Err(format!("Unknown option: {arg}")),
        }
    }
    Ok(options)
}

fn select_lan_ip() -> Option<Ipv4Addr> {
    let mut candidates: Vec<(u8, Ipv4Addr)> = get_if_addrs()
        .ok()?
        .into_iter()
        .filter_map(|iface| {
            let IpAddr::V4(ip) = iface.ip() else {
                return None;
            };
            if iface.is_loopback() || !ip.is_private() {
                return None;
            }
            let name = iface.name.to_ascii_lowercase();
            let priority = if matches!(name.as_str(), "en0" | "wi-fi" | "wifi" | "wlan0") {
                0
            } else if name.starts_with("en") || name.starts_with("eth") {
                1
            } else {
                2
            };
            Some((priority, ip))
        })
        .collect();
    candidates.sort_by_key(|(priority, ip)| (*priority, *ip));
    candidates.first().map(|(_, ip)| *ip)
}

fn data_dir() -> io::Result<PathBuf> {
    if let Some(custom) = env::var_os("FASTINPUT_DATA_DIR") {
        let dir = PathBuf::from(custom);
        fs::create_dir_all(&dir)?;
        #[cfg(unix)]
        {
            use std::os::unix::fs::PermissionsExt;
            fs::set_permissions(&dir, fs::Permissions::from_mode(0o700))?;
        }
        return Ok(dir);
    }
    #[cfg(target_os = "windows")]
    let base = env::var_os("APPDATA")
        .map(PathBuf::from)
        .ok_or_else(|| io::Error::new(io::ErrorKind::NotFound, "APPDATA is unset"))?;
    #[cfg(target_os = "macos")]
    let base = env::var_os("HOME")
        .map(PathBuf::from)
        .ok_or_else(|| io::Error::new(io::ErrorKind::NotFound, "HOME is unset"))?
        .join("Library")
        .join("Application Support");
    #[cfg(not(any(target_os = "windows", target_os = "macos")))]
    let base = env::var_os("XDG_DATA_HOME")
        .map(PathBuf::from)
        .or_else(|| env::var_os("HOME").map(|h| PathBuf::from(h).join(".local/share")))
        .ok_or_else(|| io::Error::new(io::ErrorKind::NotFound, "HOME is unset"))?;
    let dir = base.join("FastInput");
    fs::create_dir_all(&dir)?;
    #[cfg(unix)]
    {
        use std::os::unix::fs::PermissionsExt;
        fs::set_permissions(&dir, fs::Permissions::from_mode(0o700))?;
    }
    Ok(dir)
}

fn open_browser(url: &str) {
    #[cfg(target_os = "macos")]
    let result = Command::new("open").arg(url).spawn();
    #[cfg(target_os = "windows")]
    let result = Command::new("cmd").args(["/C", "start", "", url]).spawn();
    #[cfg(not(any(target_os = "macos", target_os = "windows")))]
    let result = Command::new("xdg-open").arg(url).spawn();
    if let Err(e) = result {
        eprintln!("Could not open browser automatically: {e}");
    }
}

fn accept_connections(listener: TcpListener, app: Arc<App>) {
    if let Err(e) = listener.set_nonblocking(true) {
        eprintln!("Could not configure listener: {e}");
        return;
    }
    while !app.is_shutdown() {
        let stream = match listener.accept() {
            Ok((stream, _)) => stream,
            Err(e) if e.kind() == io::ErrorKind::WouldBlock => {
                thread::sleep(Duration::from_millis(20));
                continue;
            }
            Err(e) => {
                eprintln!("Accept failed: {e}");
                continue;
            }
        };
        let _ = stream.set_nonblocking(false);
        if ACTIVE_CONNECTIONS.fetch_add(1, Ordering::SeqCst) >= 128 {
            ACTIVE_CONNECTIONS.fetch_sub(1, Ordering::SeqCst);
            continue;
        }
        let app = Arc::clone(&app);
        thread::spawn(move || {
            let _slot = ConnectionSlot;
            let linger = stream.try_clone().ok();
            http::handle(stream, app);
            if let Some(socket) = linger { lingering_close(socket); }
        });
    }
}

/// 先送 FIN 再短暫讀掉對方尚未送完的資料，避免「提早拒絕後直接關閉」造成 TCP RST，
/// 讓客戶端（手機）收不到 401／403／409 而只看到連線錯誤。最多 256 ms、64 KiB，不放大讀 body 的 DoS 面。
fn lingering_close(socket: std::net::TcpStream) {
    use std::io::Read;
    let _ = socket.shutdown(std::net::Shutdown::Write);
    let _ = socket.set_read_timeout(Some(std::time::Duration::from_millis(64)));
    let started = std::time::Instant::now();
    let mut remaining = 64 * 1024usize;
    let mut buffer = [0u8; 4096];
    let mut socket = socket;
    while remaining > 0 && started.elapsed() < std::time::Duration::from_millis(256) {
        match socket.read(&mut buffer) {
            Ok(0) => break,
            Ok(n) => remaining = remaining.saturating_sub(n),
            Err(_) => break,
        }
    }
}

fn run() -> Result<(), Box<dyn std::error::Error>> {
    let options = parse_options().map_err(|e| format!("{e}\n{}", usage()))?;
    let lan_ip = options
        .lan_ip
        .or_else(select_lan_ip)
        .unwrap_or(Ipv4Addr::LOCALHOST);
    let app = Arc::new(App::new(
        data_dir()?.join("state.json"),
        lan_ip,
        options.port,
    )?);
    App::start_file_cleanup(&app);
    App::start_realtime(&app);
    let desktop_listener =
        TcpListener::bind(SocketAddr::from((Ipv4Addr::LOCALHOST, options.port)))?;
    let lan_listener = if lan_ip.is_loopback() {
        None
    } else {
        Some(TcpListener::bind(SocketAddr::from((lan_ip, options.port)))?)
    };
    let desktop_url = format!(
        "http://127.0.0.1:{}/desktop?key={}",
        options.port,
        app.desktop_token()
    );
    if options.native_stdio {
        native_bridge::start(Arc::clone(&app), desktop_url.clone())?;
    } else {
        println!("FastInput is running.");
        println!("Computer: {desktop_url}");
        println!("Phone: scan the QR code shown on the computer (same LAN).");
    }
    if lan_ip.is_loopback() {
        eprintln!("No private LAN IPv4 address detected. Use --lan-ip after connecting to Wi-Fi.");
    }
    if options.open_browser && !options.native_stdio {
        open_browser(&desktop_url);
    }
    if let Some(listener) = lan_listener {
        let lan_app = Arc::clone(&app);
        thread::spawn(move || accept_connections(listener, lan_app));
    }
    accept_connections(desktop_listener, app);
    Ok(())
}

fn main() {
    if let Err(e) = run() {
        eprintln!("FastInput failed: {e}");
        std::process::exit(1);
    }
}
