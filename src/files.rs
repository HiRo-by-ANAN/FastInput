//! Disk-backed file staging. Only metadata and a fixed I/O buffer live in memory.
use rand::{rngs::OsRng, RngCore};
use serde::{Deserialize, Serialize};
use serde_json::{json, Value};
use std::{
    collections::{HashMap, HashSet},
    fs::{self, File, OpenOptions},
    io::{self, Read, Write},
    path::{Path, PathBuf},
    sync::{atomic::{AtomicU64, Ordering}, Mutex},
    time::{SystemTime, UNIX_EPOCH},
};

pub const MAX_FILE_BYTES: u64 = 209_715_200;
pub const MAX_TOTAL_BYTES: u64 = 2_147_483_648;
pub const MAX_FILES: usize = 200;
pub const TTL_SECONDS: u64 = 86_400;
pub const MAX_UPLOADS: usize = 4;
pub const BUFFER_BYTES: usize = 65_536;

pub fn now() -> u64 {
    SystemTime::now().duration_since(UNIX_EPOCH).unwrap_or_default().as_secs()
}

pub fn valid_id(id: &str) -> bool {
    id.len() == 16 && id.bytes().all(|b| b.is_ascii_digit() || (b'a'..=b'f').contains(&b))
}

fn reserved_name(name: &str) -> bool {
    let stem = name.split('.').next().unwrap_or("").trim_end().to_ascii_uppercase();
    matches!(stem.as_str(), "CON" | "PRN" | "AUX" | "NUL")
        || ["COM", "LPT"].iter().any(|p| stem.strip_prefix(p)
            .is_some_and(|n| n.len() == 1 && matches!(n.as_bytes()[0], b'1'..=b'9')))
}

fn shorten_name(name: &str) -> String {
    if name.chars().count() <= 120 { return name.to_owned(); }
    let (base, extension) = name.rsplit_once('.').filter(|(b, _)| !b.is_empty())
        .map(|(b, e)| (b, format!(".{e}"))).unwrap_or((name, String::new()));
    // An extension alone cannot exceed the entire filename budget.
    let extension: String = extension.chars().take(119).collect();
    base.chars().take(120 - extension.chars().count()).collect::<String>() + &extension
}

pub fn sanitize_name(name: &str) -> String {
    let last = name.rsplit(['/', '\\']).next().unwrap_or("");
    let mut name: String = last.chars()
        .filter(|c| !c.is_control() && !"<>:\"/\\|?*".contains(*c)).collect();
    name = name.trim_end_matches(|c: char| c.is_whitespace() || c == '.').to_owned();
    if reserved_name(&name) { name.insert(0, '_'); }
    name = shorten_name(&name);
    name = name.trim_end_matches(|c: char| c.is_whitespace() || c == '.').to_owned();
    // Truncation can itself turn a longer stem into a reserved name (e.g. CONxx).
    if reserved_name(&name) { name = shorten_name(&format!("_{name}")); }
    if name.is_empty() { "file".into() } else { name }
}

pub fn decode_name(value: &str) -> Option<String> {
    let mut bytes = Vec::with_capacity(value.len());
    let mut input = value.bytes();
    while let Some(b) = input.next() {
        if b == b'%' {
            let hi = (input.next()? as char).to_digit(16)?;
            let lo = (input.next()? as char).to_digit(16)?;
            bytes.push((hi * 16 + lo) as u8);
        } else { bytes.push(b); }
    }
    String::from_utf8(bytes).ok()
}

pub fn disposition(name: &str) -> String {
    let ascii: String = name.chars().map(|c| if c.is_ascii_alphanumeric() || " ._-".contains(c) { c } else { '_' }).collect();
    let mut encoded = String::new();
    for b in name.bytes() {
        use std::fmt::Write as _;
        if b.is_ascii_alphanumeric() || b"!#$&+-.^_`|~".contains(&b) { encoded.push(b as char); }
        else { let _ = write!(encoded, "%{b:02X}"); }
    }
    format!("attachment; filename=\"{ascii}\"; filename*=UTF-8''{encoded}")
}

#[derive(Clone, Debug, Serialize, Deserialize)]
pub struct Entry {
    pub id: String,
    pub name: String,
    pub size: u64,
    pub created_at: u64,
    pub expires_at: u64,
    pub source: String,
}

#[derive(Clone, Default, Serialize, Deserialize)]
struct Index { files: Vec<Entry>, revision: u64 }
struct Inner { index: Index, reserved: HashMap<String, u64> }
pub struct Files { dir: PathBuf, inner: Mutex<Inner>, revision: AtomicU64 }

#[derive(Debug)]
pub enum Error { TooLarge, Quota, Count, Busy, Missing, Io(io::Error) }
impl From<io::Error> for Error { fn from(e: io::Error) -> Self { Self::Io(e) } }

fn quota(index: &Index, reserved: &HashMap<String, u64>, size: u64) -> Result<(), Error> {
    if size > MAX_FILE_BYTES { return Err(Error::TooLarge); }
    if reserved.len() >= MAX_UPLOADS { return Err(Error::Busy); }
    if index.files.len() + reserved.len() >= MAX_FILES { return Err(Error::Count); }
    let used: u64 = index.files.iter().map(|e| e.size).chain(reserved.values().copied()).sum();
    if size > MAX_TOTAL_BYTES.saturating_sub(used) { return Err(Error::Quota); }
    Ok(())
}

impl Files {
    pub fn open(dir: PathBuf) -> io::Result<Self> {
        fs::create_dir_all(&dir)?;
        let path = dir.join("index.json");
        let mut index: Index = if path.exists() {
            serde_json::from_slice(&fs::read(&path)?).map_err(io::Error::other)?
        } else { Index::default() };
        let before = index.files.len();
        let mut seen = HashSet::new();
        index.files.retain(|e| valid_id(&e.id) && seen.insert(e.id.clone()) && e.expires_at > now()
            && e.size <= MAX_FILE_BYTES && matches!(e.source.as_str(), "phone" | "desktop")
            && fs::metadata(dir.join(format!("{}.bin", e.id))).is_ok_and(|m| m.is_file() && m.len() == e.size));
        index.revision = index.revision.checked_add((before - index.files.len()) as u64)
            .ok_or_else(|| io::Error::other("files revision exhausted"))?;
        for e in &mut index.files { e.name = sanitize_name(&e.name); }
        let files = Self { dir, revision: AtomicU64::new(index.revision), inner: Mutex::new(Inner { index, reserved: HashMap::new() }) };
        {
            let inner = files.inner.lock().unwrap();
            files.save(&inner.index)?;
            let names: HashSet<_> = inner.index.files.iter().map(|e| format!("{}.bin", e.id)).collect();
            for item in fs::read_dir(&files.dir)? {
                let item = item?;
                if item.file_type()?.is_file() && item.file_name() != "index.json"
                    && !names.contains(&item.file_name().to_string_lossy().into_owned()) {
                    fs::remove_file(item.path())?;
                }
            }
        }
        Ok(files)
    }

    fn save(&self, index: &Index) -> io::Result<()> {
        let tmp = self.dir.join("index.json.tmp");
        let mut file = OpenOptions::new().write(true).create(true).truncate(true).open(&tmp)?;
        serde_json::to_writer(&mut file, index).map_err(io::Error::other)?;
        file.flush()?;
        file.sync_all()?;
        drop(file);
        atomic_replace(&tmp, &self.dir.join("index.json"))
    }

    fn commit(&self, inner: &mut Inner, index: Index) -> io::Result<()> {
        self.save(&index)?;
        self.revision.store(index.revision, Ordering::SeqCst);
        inner.index = index;
        Ok(())
    }

    pub fn revision(&self) -> u64 { self.revision.load(Ordering::SeqCst) }

    pub fn cleanup(&self, at: u64) -> io::Result<bool> {
        let mut inner = self.inner.lock().unwrap();
        let expired: Vec<_> = inner.index.files.iter().filter(|e| e.expires_at <= at).map(|e| e.id.clone()).collect();
        if expired.is_empty() { return Ok(false); }
        let mut next = inner.index.clone();
        next.files.retain(|e| e.expires_at > at);
        next.revision = next.revision.checked_add(expired.len() as u64).ok_or_else(|| io::Error::other("files revision exhausted"))?;
        self.commit(&mut inner, next)?;
        for id in expired { self.remove_blob(&id); }
        Ok(true)
    }

    fn remove_blob(&self, id: &str) {
        if let Err(e) = fs::remove_file(self.dir.join(format!("{id}.bin"))) {
            if e.kind() != io::ErrorKind::NotFound { eprintln!("File cleanup failed: {e}"); }
        }
    }

    pub fn list(&self) -> Value {
        let inner = self.inner.lock().unwrap();
        let mut entries: Vec<_> = inner.index.files.iter().filter(|e| e.expires_at > now()).cloned().collect();
        entries.sort_by(|a,b| b.created_at.cmp(&a.created_at));
        let used: u64 = entries.iter().map(|e| e.size).sum();
        json!({"files":entries,"used_bytes":used,"max_file_bytes":MAX_FILE_BYTES,
            "max_total_bytes":MAX_TOTAL_BYTES,"max_files":MAX_FILES,"ttl_seconds":TTL_SECONDS,"revision":inner.index.revision})
    }

    pub fn begin(&self, size: u64) -> Result<Upload<'_>, Error> {
        let mut inner = self.inner.lock().unwrap();
        quota(&inner.index, &inner.reserved, size)?;
        loop {
            let id = format!("{:016x}", OsRng.next_u64());
            if inner.reserved.contains_key(&id) || inner.index.files.iter().any(|e| e.id == id)
                || self.dir.join(format!("{id}.bin")).exists() { continue; }
            let part = self.dir.join(format!("{id}.part"));
            let file = match OpenOptions::new().create_new(true).write(true).open(&part) {
                Err(e) if e.kind() == io::ErrorKind::AlreadyExists => continue,
                other => other?,
            };
            inner.reserved.insert(id.clone(), size);
            return Ok(Upload { store: self, id, size, file: Some(file), committed: false });
        }
    }

    pub fn download(&self, id: &str) -> Result<(Entry, File), Error> {
        if !valid_id(id) { return Err(Error::Missing); }
        let inner = self.inner.lock().unwrap();
        let entry = inner.index.files.iter().find(|e| e.id == id && e.expires_at > now()).ok_or(Error::Missing)?.clone();
        let file = File::open(self.dir.join(format!("{id}.bin"))).map_err(|e| if e.kind() == io::ErrorKind::NotFound { Error::Missing } else { Error::Io(e) })?;
        Ok((entry, file))
    }

    pub fn delete(&self, id: &str) -> Result<u64, Error> {
        if !valid_id(id) { return Err(Error::Missing); }
        let mut inner = self.inner.lock().unwrap();
        if !inner.index.files.iter().any(|e| e.id == id && e.expires_at > now()) { return Err(Error::Missing); }
        let mut next = inner.index.clone();
        next.files.retain(|e| e.id != id);
        next.revision = next.revision.checked_add(1).ok_or_else(|| io::Error::other("files revision exhausted"))?;
        self.commit(&mut inner, next)?;
        self.remove_blob(id);
        Ok(inner.index.revision)
    }
}

pub struct Upload<'a> { store: &'a Files, id: String, size: u64, file: Option<File>, committed: bool }
impl Upload<'_> {
    pub fn receive(&mut self, reader: &mut impl Read) -> io::Result<()> {
        let mut buffer = [0u8; BUFFER_BYTES];
        let mut remaining = self.size;
        while remaining > 0 {
            let limit = remaining.min(buffer.len() as u64) as usize;
            let count = match reader.read(&mut buffer[..limit]) {
                Err(e) if e.kind() == io::ErrorKind::Interrupted => continue,
                other => other?,
            };
            if count == 0 { return Err(io::Error::new(io::ErrorKind::UnexpectedEof, "short file body")); }
            self.file.as_mut().unwrap().write_all(&buffer[..count])?;
            remaining -= count as u64;
        }
        Ok(())
    }

    pub fn finish(mut self, name: String, source: &str) -> Result<Value, Error> {
        let file = self.file.take().unwrap();
        file.sync_all()?;
        drop(file);
        fs::rename(self.store.dir.join(format!("{}.part", self.id)), self.store.dir.join(format!("{}.bin", self.id)))?;
        let created_at = now();
        let entry = Entry { id: self.id.clone(), name, size: self.size, created_at, expires_at: created_at + TTL_SECONDS, source: source.into() };
        let mut inner = self.store.inner.lock().unwrap();
        let mut next = inner.index.clone();
        next.files.push(entry.clone());
        next.revision = next.revision.checked_add(1).ok_or_else(|| io::Error::other("files revision exhausted"))?;
        self.store.commit(&mut inner, next)?;
        // Convert the reservation into a committed entry under the same lock;
        // another admission must never count a finished upload twice.
        inner.reserved.remove(&self.id);
        self.committed = true;
        Ok(json!({"file":entry,"revision":inner.index.revision}))
    }
}
impl Drop for Upload<'_> {
    fn drop(&mut self) {
        self.file.take(); // Close first: Windows must allow the subsequent deletion.
        if !self.committed {
            let _ = fs::remove_file(self.store.dir.join(format!("{}.part", self.id)));
            let _ = fs::remove_file(self.store.dir.join(format!("{}.bin", self.id)));
        }
        self.store.inner.lock().unwrap().reserved.remove(&self.id);
    }
}

#[cfg(not(windows))]
fn atomic_replace(from: &Path, to: &Path) -> io::Result<()> { fs::rename(from, to) }
#[cfg(windows)]
fn atomic_replace(from: &Path, to: &Path) -> io::Result<()> {
    use std::os::windows::ffi::OsStrExt;
    #[link(name = "kernel32")]
    extern "system" { fn MoveFileExW(from: *const u16, to: *const u16, flags: u32) -> i32; }
    let from: Vec<u16> = from.as_os_str().encode_wide().chain(Some(0)).collect();
    let to: Vec<u16> = to.as_os_str().encode_wide().chain(Some(0)).collect();
    // REPLACE_EXISTING | WRITE_THROUGH; never unlink the old index before replacing it.
    if unsafe { MoveFileExW(from.as_ptr(), to.as_ptr(), 1 | 8) } == 0 { Err(io::Error::last_os_error()) } else { Ok(()) }
}

#[cfg(test)]
mod tests {
    use super::*;

    fn entry(id: &str, size: u64, expires_at: u64) -> Entry {
        Entry { id: id.into(), name: "test.txt".into(), size, created_at: 1, expires_at, source: "phone".into() }
    }

    #[test]
    fn filename_sanitization() {
        for (input, expected) in [
            ("../../hello.txt", "hello.txt"), ("C:\\tmp\\note.txt", "note.txt"),
            ("CON.txt", "_CON.txt"), ("prn", "_prn"), ("AUX.png", "_AUX.png"),
            ("NUL", "_NUL"), ("com1.exe", "_com1.exe"), ("LPT9.txt", "_LPT9.txt"),
            ("COM0.txt", "COM0.txt"), ("COM10", "COM10"),
            ("a\0\n\u{7f}\u{85}<>:\"|?*b.txt . ", "ab.txt"),
            ("", "file"), ("... ", "file"), ("folder/", "file"),
        ] { assert_eq!(sanitize_name(input), expected, "{input:?}"); }
        let long = format!("{}.照片.png", "好".repeat(200));
        let name = sanitize_name(&long);
        assert_eq!(name.chars().count(), 120);
        assert!(name.ends_with(".png"));
        assert!(sanitize_name(&format!("x.{}", "e".repeat(200))).chars().count() <= 120);
        let truncated_reserved = sanitize_name(&format!("CONxx.{}", "e".repeat(116)));
        assert!(truncated_reserved.starts_with('_'));
        assert_eq!(truncated_reserved.chars().count(), 120);
        assert_eq!(decode_name("%E7%85%A7%E7%89%87%20a%2Bb.png").unwrap(), "照片 a+b.png");
        for bad in ["%", "%GG", "%ff"] { assert!(decode_name(bad).is_none()); }
        let header = disposition("照片 a+b.png");
        assert!(header.contains("filename*=UTF-8''%E7%85%A7%E7%89%87%20a+b.png"));
        assert!(header.is_ascii());
    }

    #[test]
    fn lowercase_id_validation() {
        assert!(valid_id("0123456789abcdef"));
        for bad in ["", "0123456789abcde", "0123456789abcdef0", "0123456789abcdeF", "../0123456789abcd", "0123456789abcdeg", "照片0123456789"] {
            assert!(!valid_id(bad), "{bad}");
        }
    }

    #[test]
    fn quota_includes_inflight_reservations() {
        let mut index = Index::default();
        let mut reserved = HashMap::new();
        assert!(quota(&index, &reserved, MAX_FILE_BYTES).is_ok());
        assert!(matches!(quota(&index, &reserved, MAX_FILE_BYTES + 1), Err(Error::TooLarge)));
        // Check the exact total limit, and a reservation taking the last byte.
        index.files.push(entry("0000000000000001", MAX_TOTAL_BYTES - 100, now() + 100));
        assert!(quota(&index, &reserved, 100).is_ok());
        reserved.insert("0000000000000002".into(), 100);
        assert!(quota(&index, &reserved, 0).is_ok());
        assert!(matches!(quota(&index, &reserved, 1), Err(Error::Quota)));
        index.files = vec![entry("0000000000000001", 0, now() + 100); MAX_FILES - 1];
        assert!(matches!(quota(&index, &reserved, 0), Err(Error::Count)));
        index.files.clear();
        for n in 0..MAX_UPLOADS { reserved.insert(format!("{n:016x}"), 0); }
        assert!(matches!(quota(&index, &reserved, 0), Err(Error::Busy)));
    }

    #[test]
    fn expiry_reconciliation_and_atomic_index() {
        let dir = std::env::temp_dir().join(format!("fastinput-files-{:016x}", OsRng.next_u64()));
        fs::create_dir_all(&dir).unwrap();
        let good = entry("0000000000000001", 3, now() + 100);
        let expired = entry("0000000000000002", 3, now());
        let missing = entry("0000000000000003", 3, now() + 100);
        let mismatch = entry("0000000000000004", 4, now() + 100);
        let unsafe_id = entry("../../outside", 0, now() + 100);
        fs::write(dir.join("index.json"), serde_json::to_vec(&Index { files: vec![good, expired, missing, mismatch, unsafe_id], revision: 10 }).unwrap()).unwrap();
        for id in ["0000000000000001", "0000000000000002", "0000000000000004", "0000000000000005"] { fs::write(dir.join(format!("{id}.bin")), b"abc").unwrap(); }
        fs::write(dir.join("0000000000000006.part"), b"incomplete").unwrap();
        fs::write(dir.join("index.json.tmp"), b"incomplete index").unwrap();
        let store = Files::open(dir.clone()).unwrap();
        assert_eq!(store.revision(), 14);
        assert_eq!(store.list()["files"].as_array().unwrap().len(), 1);
        assert_eq!(fs::read_dir(&dir).unwrap().count(), 2);
        assert!(matches!(store.download("0000000000000002"), Err(Error::Missing)));
        assert!(!store.cleanup(now()).unwrap());
        assert!(store.cleanup(now() + 101).unwrap());
        assert_eq!(store.revision(), 15);
        assert!(!store.cleanup(now() + 101).unwrap());
        assert_eq!(fs::read_dir(&dir).unwrap().count(), 1);
        drop(store);
        let reopened = Files::open(dir.clone()).unwrap();
        assert_eq!(reopened.revision(), 15);
        assert_eq!(reopened.list()["used_bytes"], 0);
        drop(reopened);
        fs::remove_dir_all(dir).unwrap();
    }

    #[test]
    fn failed_upload_releases_part_and_reservation() {
        let dir = std::env::temp_dir().join(format!("fastinput-files-{:016x}", OsRng.next_u64()));
        let store = Files::open(dir.clone()).unwrap();
        {
            let mut upload = store.begin(10).unwrap();
            assert!(upload.receive(&mut &b"short"[..]).is_err());
        }
        assert_eq!(store.inner.lock().unwrap().reserved.len(), 0);
        assert_eq!(fs::read_dir(&dir).unwrap().count(), 1);
        drop(store);
        fs::remove_dir_all(dir).unwrap();
    }
}
