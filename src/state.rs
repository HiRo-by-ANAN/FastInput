use qrcode::{render::svg, Color, QrCode};
use rand::{rngs::OsRng, RngCore};
use serde::{Deserialize, Serialize};
use serde_json::{json, Value};
use std::{
    fs::{self, OpenOptions},
    io::{self, Write},
    net::{IpAddr, Ipv4Addr},
    path::PathBuf,
    sync::{
        atomic::{AtomicBool, Ordering},
        Condvar, Mutex,
    },
    time::{Duration, SystemTime, UNIX_EPOCH},
};

pub const MAX_TEXT_BYTES: usize = 1_000_000;
const PAIR_LIFETIME_SECONDS: u64 = 300;
const PENDING_LIFETIME_SECONDS: u64 = 600;

#[derive(Clone, Serialize, Deserialize)]
struct Device {
    id: String,
    label: String,
    token: String,
    #[serde(default)]
    older_tokens: Vec<String>,
    paired_at: u64,
}

#[derive(Clone, Serialize, Deserialize)]
struct Persisted {
    generation: String,
    revision: u64,
    text: String,
    desktop_token: String,
    devices: Vec<Device>,
}

struct Pending {
    id: String,
    token: String,
    code: String,
    ip: String,
    created_at: u64,
}

struct Pairing {
    token: String,
    pin: String,
    expires_at: u64,
}

struct Inner {
    saved: Persisted,
    pending: Vec<Pending>,
    pairing: Pairing,
    serial: u64,
    failed_pin_attempts: u32,
    pin_lockout_until: u64,
}

#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub enum Role {
    Desktop,
    Phone,
    Pending,
    None,
}

#[derive(Deserialize)]
pub struct EditRequest {
    pub text: String,
    pub revision: u64,
    pub generation: String,
}

#[derive(Debug)]
pub enum EditError {
    Unauthorized,
    TooLarge,
    Conflict(Value),
    Io(io::Error),
}

#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub enum PinError {
    LockedOut(u64),
    InvalidPin,
    Expired,
    TooManyDevices,
    Io,
}

pub struct App {
    pub files: crate::files::Files,
    pub realtime: crate::realtime::RealtimeState,
    credential_gate: Mutex<()>,
    inner: Mutex<Inner>,
    changed: Condvar,
    shutdown: AtomicBool,
    path: PathBuf,
    lan_ip: Ipv4Addr,
    port: u16,
}

fn now() -> u64 {
    SystemTime::now()
        .duration_since(UNIX_EPOCH)
        .unwrap_or_default()
        .as_secs()
}

fn random_token() -> String {
    let mut bytes = [0u8; 32];
    OsRng.fill_bytes(&mut bytes);
    let mut out = String::with_capacity(64);
    for b in bytes {
        use std::fmt::Write as _;
        let _ = write!(out, "{b:02x}");
    }
    out
}

fn random_id() -> String {
    random_token()[..16].to_string()
}

fn fresh_pairing() -> Pairing {
    let digits = OsRng.next_u32() % 1_000_000;
    Pairing {
        token: random_token(),
        pin: format!("{digits:06}"),
        expires_at: now() + PAIR_LIFETIME_SECONDS,
    }
}

fn constant_time_equal(a: &str, b: &str) -> bool {
    let a = a.as_bytes();
    let b = b.as_bytes();
    if a.len() != b.len() {
        return false;
    }
    let mut difference = 0u8;
    for (x, y) in a.iter().zip(b) {
        difference |= x ^ y;
    }
    difference == 0
}

impl Device {
    fn has_token(&self, token: &str) -> bool {
        constant_time_equal(token, &self.token)
            || self
                .older_tokens
                .iter()
                .any(|old| constant_time_equal(token, old))
    }

    fn add_token(&mut self, token: String, paired_at: u64) {
        if self.token != token {
            self.older_tokens
                .retain(|old| old != &token && old != &self.token);
            self.older_tokens
                .push(std::mem::replace(&mut self.token, token));
            // Repeated scans must not create an unlimited number of credentials.
            if self.older_tokens.len() > 63 {
                self.older_tokens.drain(..self.older_tokens.len() - 63);
            }
        }
        self.paired_at = paired_at;
    }
}

fn consolidate_devices(devices: &mut Vec<Device>) -> bool {
    let before = devices.len();
    let mut consolidated: Vec<Device> = Vec::with_capacity(before);
    for device in devices.drain(..) {
        // These labels were generated from the connecting device's LAN address.
        // Keep distinct named devices separate rather than guessing their identity.
        if device.label.starts_with("裝置 (") && device.label.ends_with(')') {
            if let Some(existing) = consolidated.iter_mut().find(|d| d.label == device.label) {
                for old in device.older_tokens {
                    if !existing.has_token(&old) {
                        existing.older_tokens.push(old);
                    }
                }
                if !existing.has_token(&device.token) {
                    existing.add_token(device.token, device.paired_at);
                } else if device.paired_at > existing.paired_at {
                    existing.paired_at = device.paired_at;
                }
                continue;
            }
        }
        consolidated.push(device);
    }
    *devices = consolidated;
    before != devices.len()
}

impl Inner {
    fn role(&self, token: Option<&str>, peer: IpAddr) -> Role {
        let Some(token) = token else {
            return Role::None;
        };
        if peer.is_loopback() && constant_time_equal(token, &self.saved.desktop_token) {
            return Role::Desktop;
        }
        if self.saved.devices.iter().any(|d| d.has_token(token)) {
            return Role::Phone;
        }
        if self
            .pending
            .iter()
            .any(|p| constant_time_equal(token, &p.token))
        {
            return Role::Pending;
        }
        Role::None
    }

    fn expire_pending(&mut self) {
        let cutoff = now().saturating_sub(PENDING_LIFETIME_SECONDS);
        self.pending.retain(|p| p.created_at >= cutoff);
    }
}

impl App {
    fn pairing_url(&self, code: &str) -> String {
        format!("http://{}:{}/pair?code={code}", self.lan_ip, self.port)
    }

    pub fn pairing_png(&self, token: Option<&str>, peer: IpAddr) -> io::Result<Option<Vec<u8>>> {
        let url = {
            let mut inner = self.inner.lock().unwrap();
            if inner.role(token, peer) != Role::Desktop {
                return Ok(None);
            }
            if now() >= inner.pairing.expires_at {
                inner.pairing = fresh_pairing();
            }
            self.pairing_url(&inner.pairing.pin)
        };
        let qr = QrCode::new(url.as_bytes()).map_err(io::Error::other)?;
        let quiet = 4usize;
        let scale = (512 / (qr.width() + quiet * 2)).max(1);
        let side = (qr.width() + quiet * 2) * scale;
        let mut pixels = vec![255u8; side * side];
        for y in 0..qr.width() {
            for x in 0..qr.width() {
                if qr[(x, y)] == Color::Dark {
                    for yy in (y + quiet) * scale..(y + quiet + 1) * scale {
                        for xx in (x + quiet) * scale..(x + quiet + 1) * scale {
                            pixels[yy * side + xx] = 0;
                        }
                    }
                }
            }
        }
        let mut output = Vec::new();
        {
            let mut encoder = png::Encoder::new(&mut output, side as u32, side as u32);
            encoder.set_color(png::ColorType::Grayscale);
            encoder.set_depth(png::BitDepth::Eight);
            let mut writer = encoder.write_header().map_err(io::Error::other)?;
            writer.write_image_data(&pixels).map_err(io::Error::other)?;
        }
        Ok(Some(output))
    }

    pub fn host_allowed(&self, host: &str) -> bool {
        host == format!("127.0.0.1:{}", self.port)
            || host == format!("localhost:{}", self.port)
            || host == format!("[::1]:{}", self.port)
            || host == format!("{}:{}", self.lan_ip, self.port)
    }

    pub fn is_shutdown(&self) -> bool {
        self.shutdown.load(Ordering::SeqCst)
    }

    pub fn request_shutdown(&self, token: Option<&str>, peer: IpAddr) -> bool {
        if self.role(token, peer) != Role::Desktop {
            return false;
        }
        self.realtime.native_disconnected();
        self.shutdown.store(true, Ordering::SeqCst);
        self.changed.notify_all();
        true
    }

    pub fn native_shutdown(&self) {
        self.realtime.native_disconnected();
        self.shutdown.store(true, Ordering::SeqCst);
        self.changed.notify_all();
    }

    /// Realtime never sees a pairing secret or a device-wide identity.
    pub fn credential(&self, token: Option<&str>, peer: IpAddr) -> Option<(String, String)> {
        let inner = self.inner.lock().unwrap();
        if inner.role(token, peer) != Role::Phone { return None; }
        let token = token?;
        let device = inner.saved.devices.iter().find(|d| d.has_token(token))?;
        Some((crate::credential::id(token), device.label.clone()))
    }

    /// Serialize credential removal with an authorized realtime mutation, without
    /// holding the persisted-text lock during native dispatch or event callbacks.
    pub fn with_credential<R>(&self, token: Option<&str>, peer: IpAddr, action: impl FnOnce(&str, &str) -> R) -> Option<R> {
        let _gate = self.credential_gate.lock().unwrap();
        let (credential, label) = self.credential(token, peer)?;
        Some(action(&credential, &label))
    }

    pub fn start_realtime(app: &std::sync::Arc<Self>) {
        let weak = std::sync::Arc::downgrade(app);
        app.realtime.set_notifier(std::sync::Arc::new(move || {
            if let Some(app) = weak.upgrade() {
                // Pair with wait_event's condition check. Realtime invokes this only
                // after releasing its own lock; text serial remains untouched.
                let _guard = app.inner.lock().unwrap();
                app.changed.notify_all();
            }
        }));
        let weak = std::sync::Arc::downgrade(app);
        std::thread::spawn(move || loop {
            std::thread::sleep(Duration::from_millis(25));
            let Some(app) = weak.upgrade() else { break; };
            if app.is_shutdown() { break; }
            app.realtime.tick();
        });
    }

    pub fn new(path: PathBuf, lan_ip: Ipv4Addr, port: u16) -> io::Result<Self> {
        let mut saved = if path.exists() {
            let bytes = fs::read(&path)?;
            let state: Persisted = serde_json::from_slice(&bytes)
                .map_err(|e| io::Error::new(io::ErrorKind::InvalidData, e))?;
            if state.text.len() > MAX_TEXT_BYTES {
                return Err(io::Error::new(
                    io::ErrorKind::InvalidData,
                    "saved text is too large",
                ));
            }
            state
        } else {
            let state = Persisted {
                generation: random_token(),
                revision: 0,
                text: String::new(),
                desktop_token: random_token(),
                devices: Vec::new(),
            };
            Self::save_to(&path, &state)?;
            state
        };
        if consolidate_devices(&mut saved.devices) {
            Self::save_to(&path, &saved)?;
        }
        Ok(Self {
            files: crate::files::Files::open(path.parent().unwrap_or_else(|| std::path::Path::new(".")).join("files"))?,
            realtime: crate::realtime::RealtimeState::new(),
            credential_gate: Mutex::new(()),
            inner: Mutex::new(Inner {
                saved,
                pending: Vec::new(),
                pairing: fresh_pairing(),
                serial: 1,
                failed_pin_attempts: 0,
                pin_lockout_until: 0,
            }),
            changed: Condvar::new(),
            shutdown: AtomicBool::new(false),
            path,
            lan_ip,
            port,
        })
    }

    fn save_to(path: &PathBuf, data: &Persisted) -> io::Result<()> {
        let tmp = path.with_extension("json.tmp");
        let bytes = serde_json::to_vec_pretty(data).map_err(io::Error::other)?;
        let mut options = OpenOptions::new();
        options.write(true).create(true).truncate(true);
        #[cfg(unix)]
        {
            use std::os::unix::fs::OpenOptionsExt;
            options.mode(0o600);
        }
        let mut file = options.open(&tmp)?;
        file.write_all(&bytes)?;
        file.sync_all()?;
        fs::rename(&tmp, path)?;
        Ok(())
    }

    pub fn desktop_token(&self) -> String {
        self.inner.lock().unwrap().saved.desktop_token.clone()
    }

    pub fn files_changed(&self) {
        let mut inner = self.inner.lock().unwrap();
        inner.serial += 1;
        self.changed.notify_all();
    }

    pub fn cleanup_files(&self) -> io::Result<()> {
        if self.files.cleanup(crate::files::now())? { self.files_changed(); }
        Ok(())
    }

    pub fn start_file_cleanup(app: &std::sync::Arc<Self>) {
        let weak = std::sync::Arc::downgrade(app);
        std::thread::spawn(move || loop {
            std::thread::sleep(Duration::from_secs(60));
            let Some(app) = weak.upgrade() else { break; };
            if app.is_shutdown() { break; }
            if let Err(e) = app.cleanup_files() { eprintln!("File expiration failed: {e}"); }
        });
    }

    pub fn desktop_login(&self, key: &str, peer: IpAddr) -> bool {
        peer.is_loopback()
            && constant_time_equal(key, &self.inner.lock().unwrap().saved.desktop_token)
    }

    pub fn role(&self, token: Option<&str>, peer: IpAddr) -> Role {
        self.inner.lock().unwrap().role(token, peer)
    }

    pub fn accept_pin(&self, pin: &str, peer: IpAddr) -> Result<String, PinError> {
        let _gate = self.credential_gate.lock().unwrap();
        let mut inner = self.inner.lock().unwrap();
        inner.expire_pending();
        let now_sec = now();
        if now_sec < inner.pin_lockout_until {
            return Err(PinError::LockedOut(inner.pin_lockout_until - now_sec));
        }
        if now_sec >= inner.pairing.expires_at {
            inner.pairing = fresh_pairing();
            return Err(PinError::Expired);
        }
        let clean_pin: String = pin.chars().filter(|c| c.is_ascii_digit()).collect();
        if clean_pin.len() != 6 || !constant_time_equal(&clean_pin, &inner.pairing.pin) {
            inner.failed_pin_attempts += 1;
            if inner.failed_pin_attempts >= 5 {
                inner.pin_lockout_until = now_sec + 60;
                return Err(PinError::LockedOut(60));
            }
            return Err(PinError::InvalidPin);
        }
        let label = format!("裝置 ({peer})");
        let existing_index = inner.saved.devices.iter().position(|d| d.label == label);
        if existing_index.is_none() && inner.saved.devices.len() >= 64 {
            return Err(PinError::TooManyDevices);
        }
        inner.failed_pin_attempts = 0;
        inner.pin_lockout_until = 0;

        let token = random_token();
        let mut updated = inner.saved.clone();
        if let Some(index) = existing_index {
            updated.devices[index].add_token(token.clone(), now_sec);
        } else {
            updated.devices.push(Device {
                id: random_id(),
                label,
                token: token.clone(),
                older_tokens: Vec::new(),
                paired_at: now_sec,
            });
        }
        Self::save_to(&self.path, &updated).map_err(|_| PinError::Io)?;
        let retired: Vec<String> = inner.saved.devices.iter()
            .flat_map(|d| std::iter::once(&d.token).chain(d.older_tokens.iter()))
            .filter(|t| !updated.devices.iter().any(|d| d.has_token(t)))
            .map(|t| crate::credential::id(t)).collect();
        inner.saved = updated;
        inner.pairing = fresh_pairing();
        inner.serial += 1;
        self.changed.notify_all();
        drop(inner);
        for credential in retired { self.realtime.credential_revoked(&credential); }
        Ok(token)
    }

    pub fn accept_pair(&self, code: &str, peer: IpAddr) -> Option<String> {
        let mut inner = self.inner.lock().unwrap();
        inner.expire_pending();
        if inner.pending.len() >= 5
            || now() >= inner.pairing.expires_at
            || (!constant_time_equal(code, &inner.pairing.token)
                && !constant_time_equal(code, &inner.pairing.pin))
        {
            return None;
        }
        let token = random_token();
        let digits = OsRng.next_u32() % 1_000_000;
        inner.pending.push(Pending {
            id: random_id(),
            token: token.clone(),
            code: format!("{digits:06}"),
            ip: peer.to_string(),
            created_at: now(),
        });
        inner.pairing = fresh_pairing();
        inner.serial += 1;
        self.changed.notify_all();
        Some(token)
    }

    pub fn state(&self, token: Option<&str>, peer: IpAddr) -> Value {
        let mut inner = self.inner.lock().unwrap();
        inner.expire_pending();
        if now() >= inner.pairing.expires_at {
            inner.pairing = fresh_pairing();
        }
        let mut result = match inner.role(token, peer) {
            Role::Desktop => {
                let url = self.pairing_url(&inner.pairing.pin);
                let qr = QrCode::new(url.as_bytes())
                    .expect("short pairing URL always fits in a QR code")
                    .render::<svg::Color>()
                    .min_dimensions(240, 240)
                    .build();
                json!({
                    "status":"ready", "role":"desktop",
                    "text":inner.saved.text, "revision":inner.saved.revision,
                    "generation":inner.saved.generation,
                    "pending":inner.pending.iter().map(|p| json!({
                        "id":p.id,"code":p.code,"ip":p.ip
                    })).collect::<Vec<_>>(),
                    "devices":inner.saved.devices.iter().map(|d| json!({
                        "id":d.id,"label":d.label,"last_seen":d.paired_at
                    })).collect::<Vec<_>>(),
                    "pairing":{"url":url,"svg":qr,"pin":inner.pairing.pin,
                        "expires_seconds":inner.pairing.expires_at.saturating_sub(now()),
                        "lan_reachable":!self.lan_ip.is_loopback()}
                })
            }
            Role::Phone => json!({
                "status":"ready", "role":"phone",
                "text":inner.saved.text,
                "revision":inner.saved.revision,"generation":inner.saved.generation,
            }),
            Role::Pending => {
                let pending = inner
                    .pending
                    .iter()
                    .find(|p| Some(p.token.as_str()) == token)
                    .unwrap();
                json!({"status":"pending","code":pending.code})
            }
            Role::None => json!({"status":"unpaired"}),
        };
        result["files_revision"] = json!(self.files.revision());
        result
    }

    pub fn edit(
        &self,
        token: Option<&str>,
        peer: IpAddr,
        edit: EditRequest,
    ) -> Result<Value, EditError> {
        if edit.text.len() > MAX_TEXT_BYTES {
            return Err(EditError::TooLarge);
        }
        let mut inner = self.inner.lock().unwrap();
        if !matches!(inner.role(token, peer), Role::Desktop | Role::Phone) {
            return Err(EditError::Unauthorized);
        }
        if edit.revision != inner.saved.revision || edit.generation != inner.saved.generation {
            return Err(EditError::Conflict(json!({
                "status":"ready", "text":inner.saved.text,
                "revision":inner.saved.revision,"generation":inner.saved.generation
            })));
        }
        if edit.text != inner.saved.text {
            let mut updated = inner.saved.clone();
            updated.text = edit.text;
            updated.revision = updated
                .revision
                .checked_add(1)
                .ok_or_else(|| EditError::Io(io::Error::other("revision exhausted")))?;
            Self::save_to(&self.path, &updated).map_err(EditError::Io)?;
            inner.saved = updated;
            inner.serial += 1;
            self.changed.notify_all();
        }
        Ok(json!({
            "status":"ready", "text":inner.saved.text,
            "revision":inner.saved.revision,"generation":inner.saved.generation
        }))
    }

    pub fn approve(&self, token: Option<&str>, peer: IpAddr, id: &str) -> io::Result<bool> {
        let mut inner = self.inner.lock().unwrap();
        if inner.role(token, peer) != Role::Desktop {
            return Ok(false);
        }
        inner.expire_pending();
        let Some(index) = inner.pending.iter().position(|p| p.id == id) else {
            return Ok(false);
        };
        let pending = &inner.pending[index];
        let mut updated = inner.saved.clone();
        updated.devices.push(Device {
            id: pending.id.clone(),
            label: format!("手機 {}", &pending.id[..4]),
            token: pending.token.clone(),
            older_tokens: Vec::new(),
            paired_at: now(),
        });
        Self::save_to(&self.path, &updated)?;
        inner.saved = updated;
        inner.pending.remove(index);
        inner.serial += 1;
        self.changed.notify_all();
        Ok(true)
    }

    pub fn reject(&self, token: Option<&str>, peer: IpAddr, id: &str) -> bool {
        let mut inner = self.inner.lock().unwrap();
        if inner.role(token, peer) != Role::Desktop {
            return false;
        }
        let old_len = inner.pending.len();
        inner.pending.retain(|p| p.id != id);
        let changed = inner.pending.len() != old_len;
        if changed {
            inner.serial += 1;
            self.changed.notify_all();
        }
        changed
    }

    pub fn revoke(&self, token: Option<&str>, peer: IpAddr, id: &str) -> io::Result<bool> {
        let _gate = self.credential_gate.lock().unwrap();
        let mut inner = self.inner.lock().unwrap();
        if inner.role(token, peer) != Role::Desktop {
            return Ok(false);
        }
        let mut updated = inner.saved.clone();
        let old_len = updated.devices.len();
        updated.devices.retain(|d| d.id != id);
        if old_len == updated.devices.len() {
            return Ok(false);
        }
        Self::save_to(&self.path, &updated)?;
        let credentials: Vec<String> = inner.saved.devices.iter().filter(|d| d.id == id)
            .flat_map(|d| std::iter::once(&d.token).chain(d.older_tokens.iter()))
            .map(|t| crate::credential::id(t)).collect();
        inner.saved = updated;
        inner.serial += 1;
        self.changed.notify_all();
        drop(inner);
        for credential in credentials { self.realtime.credential_revoked(&credential); }
        Ok(true)
    }

    pub fn rotate_pairing(&self, token: Option<&str>, peer: IpAddr) -> bool {
        let mut inner = self.inner.lock().unwrap();
        if inner.role(token, peer) != Role::Desktop {
            return false;
        }
        inner.pairing = fresh_pairing();
        inner.serial += 1;
        self.changed.notify_all();
        true
    }

    pub fn wait_event(
        &self,
        token: Option<&str>,
        peer: IpAddr,
        last_serial: u64,
        realtime_cursor: Option<u64>,
    ) -> Option<(Value, u64)> {
        let mut inner = self.inner.lock().unwrap();
        if inner.serial == last_serial
            && realtime_cursor.is_none_or(|cursor| self.realtime.latest_event_id() == cursor) {
            let (new_inner, _) = self
                .changed
                .wait_timeout(inner, Duration::from_secs(20))
                .unwrap();
            inner = new_inner;
        }
        if self.is_shutdown() {
            return None;
        }
        if !matches!(inner.role(token, peer), Role::Desktop | Role::Phone) {
            return None;
        }
        Some((
            json!({
                "text": inner.saved.text,
                "revision": inner.saved.revision,
                "generation": inner.saved.generation,
                "files_revision": self.files.revision(),
            }),
            inner.serial,
        ))
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn old_revision_cannot_overwrite_new_text() {
        let dir = std::env::temp_dir().join(format!("fastinput-test-{}", random_id()));
        fs::create_dir_all(&dir).unwrap();
        let app = App::new(dir.join("state.json"), Ipv4Addr::LOCALHOST, 8765).unwrap();
        let desktop = app.desktop_token();
        let peer = IpAddr::V4(Ipv4Addr::LOCALHOST);
        let initial = app.state(Some(&desktop), peer);
        let generation = initial["generation"].as_str().unwrap().to_owned();
        let first = app
            .edit(
                Some(&desktop),
                peer,
                EditRequest {
                    text: "new".into(),
                    revision: 0,
                    generation: generation.clone(),
                },
            )
            .unwrap();
        assert_eq!(first["revision"], 1);
        let stale = app.edit(
            Some(&desktop),
            peer,
            EditRequest {
                text: "old".into(),
                revision: 0,
                generation,
            },
        );
        assert!(matches!(stale, Err(EditError::Conflict(_))));
        assert_eq!(app.state(Some(&desktop), peer)["text"], "new");
        fs::remove_dir_all(dir).unwrap();
    }

    #[test]
    fn pin_pairing_verification() {
        let dir = std::env::temp_dir().join(format!("fastinput-test-{}", random_id()));
        fs::create_dir_all(&dir).unwrap();
        let app = App::new(dir.join("state.json"), Ipv4Addr::LOCALHOST, 8765).unwrap();
        let desktop = app.desktop_token();
        let peer = IpAddr::V4(Ipv4Addr::LOCALHOST);

        let desktop_state = app.state(Some(&desktop), peer);
        let pin = desktop_state["pairing"]["pin"].as_str().unwrap();
        assert_eq!(pin.len(), 6);

        // Invalid PIN
        let bad = app.accept_pin("000000", peer);
        assert_eq!(bad, Err(PinError::InvalidPin));

        // Formatted PIN with spaces and hyphen should be accepted
        let formatted = format!("{} - {}", &pin[..3], &pin[3..]);
        let token = app
            .accept_pin(&formatted, peer)
            .expect("PIN should be accepted");

        // The newly paired device should have Role::Phone / Device
        assert_eq!(app.role(Some(&token), peer), Role::Phone);

        // State should reflect the new device
        let updated_desktop = app.state(Some(&desktop), peer);
        assert_eq!(updated_desktop["devices"].as_array().unwrap().len(), 1);

        fs::remove_dir_all(dir).unwrap();
    }

    #[test]
    fn repeat_pairing_keeps_one_device_and_previous_browser_session() {
        let dir = std::env::temp_dir().join(format!("fastinput-test-{}", random_id()));
        fs::create_dir_all(&dir).unwrap();
        let path = dir.join("state.json");
        let app = App::new(path.clone(), Ipv4Addr::LOCALHOST, 8765).unwrap();
        let desktop = app.desktop_token();
        let peer = IpAddr::V4(Ipv4Addr::new(192, 168, 1, 23));
        let desktop_peer = IpAddr::V4(Ipv4Addr::LOCALHOST);

        let first_pin = app.state(Some(&desktop), desktop_peer)["pairing"]["pin"]
            .as_str()
            .unwrap()
            .to_owned();
        let first = app.accept_pin(&first_pin, peer).unwrap();
        let first_id = app.state(Some(&desktop), desktop_peer)["devices"][0]["id"]
            .as_str()
            .unwrap()
            .to_owned();
        let second_pin = app.state(Some(&desktop), desktop_peer)["pairing"]["pin"]
            .as_str()
            .unwrap()
            .to_owned();
        let second = app.accept_pin(&second_pin, peer).unwrap();
        let devices = app.state(Some(&desktop), desktop_peer)["devices"]
            .as_array()
            .unwrap()
            .clone();
        assert_eq!(devices.len(), 1);
        assert_eq!(devices[0]["id"], first_id);
        assert_eq!(app.role(Some(&first), peer), Role::Phone);
        assert_eq!(app.role(Some(&second), peer), Role::Phone);

        drop(app);
        let reopened = App::new(path, Ipv4Addr::LOCALHOST, 8765).unwrap();
        assert_eq!(reopened.role(Some(&first), peer), Role::Phone);
        assert_eq!(reopened.role(Some(&second), peer), Role::Phone);
        assert!(reopened
            .revoke(Some(&desktop), desktop_peer, &first_id)
            .unwrap());
        assert_eq!(reopened.role(Some(&first), peer), Role::None);
        assert_eq!(reopened.role(Some(&second), peer), Role::None);
        fs::remove_dir_all(dir).unwrap();
    }

    #[test]
    fn old_duplicate_records_are_consolidated_without_losing_sessions() {
        let dir = std::env::temp_dir().join(format!("fastinput-test-{}", random_id()));
        fs::create_dir_all(&dir).unwrap();
        let path = dir.join("state.json");
        let desktop_token = random_token();
        let first = random_token();
        let second = random_token();
        let saved = Persisted {
            generation: random_token(),
            revision: 0,
            text: "saved text".into(),
            desktop_token: desktop_token.clone(),
            devices: vec![
                Device {
                    id: random_id(),
                    label: "裝置 (192.168.1.23)".into(),
                    token: first.clone(),
                    older_tokens: Vec::new(),
                    paired_at: 1,
                },
                Device {
                    id: random_id(),
                    label: "裝置 (192.168.1.23)".into(),
                    token: second.clone(),
                    older_tokens: Vec::new(),
                    paired_at: 2,
                },
            ],
        };
        let mut legacy = serde_json::to_value(&saved).unwrap();
        for device in legacy["devices"].as_array_mut().unwrap() {
            device.as_object_mut().unwrap().remove("older_tokens");
        }
        fs::write(&path, serde_json::to_vec(&legacy).unwrap()).unwrap();
        let app = App::new(path.clone(), Ipv4Addr::LOCALHOST, 8765).unwrap();
        let desktop_peer = IpAddr::V4(Ipv4Addr::LOCALHOST);
        let phone_peer = IpAddr::V4(Ipv4Addr::new(192, 168, 1, 23));
        assert_eq!(
            app.state(Some(&desktop_token), desktop_peer)["devices"]
                .as_array()
                .unwrap()
                .len(),
            1
        );
        assert_eq!(
            app.state(Some(&desktop_token), desktop_peer)["text"],
            "saved text"
        );
        assert_eq!(app.role(Some(&first), phone_peer), Role::Phone);
        assert_eq!(app.role(Some(&second), phone_peer), Role::Phone);
        let stored: Persisted = serde_json::from_slice(&fs::read(path).unwrap()).unwrap();
        assert_eq!(stored.devices.len(), 1);
        fs::remove_dir_all(dir).unwrap();
    }
}
