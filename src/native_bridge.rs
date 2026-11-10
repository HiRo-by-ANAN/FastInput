//! Private parent/child IPC. These types deliberately expose text intent only.
//! Stdout belongs exclusively to this framing protocol in native mode.

use crate::{realtime::Selection, state::App};
use serde::{Deserialize, Serialize};
use std::{
    io::{self, Read, Write},
    sync::{mpsc, Arc},
    thread,
};

pub const PROTOCOL_VERSION: u32 = 1;
pub const MAX_FRAME_BYTES: usize = 64 * 1024;

/// Native code owns the final authority. None of these messages can specify a
/// window handle, input key, shortcut, or pointer position.
#[derive(Clone, Debug, Serialize)]
#[serde(tag = "type", rename_all = "snake_case")]
pub enum NativeCommand {
    Request {
        request_id: String,
        check_code: String,
        device_label: String,
    },
    ConfirmEmpty {
        request_id: String,
        target_epoch: String,
    },
    Apply {
        request_id: String,
        target_epoch: String,
        seq: u64,
        prev_seq: u64,
        text: String,
        selection: Selection,
        composition: String,
    },
    PressEnter {
        request_id: String,
        target_epoch: String,
        enter_id: String,
        after_seq: u64,
    },
    Revoke {
        request_id: Option<String>,
        reason: String,
    },
}

/// A fake link can collect commands without touching any operating-system input.
/// Implementations must return promptly; the stdio implementation uses a bounded
/// nonblocking channel rather than writing under the realtime state lock.
pub trait NativeLink: Send + Sync {
    fn send(&self, message: NativeCommand) -> io::Result<()>;
}

#[derive(Clone, Debug, Deserialize, PartialEq)]
#[serde(tag = "type", rename_all = "snake_case", deny_unknown_fields)]
pub enum NativeMessage {
    Capabilities { virtual_enter: bool },
    Grant {
        request_id: String,
    },
    TargetBound {
        request_id: String,
        target_epoch: String,
        #[serde(default)]
        phase: Option<String>,
        #[serde(default, deserialize_with = "deserialize_app_label")]
        app_label: Option<String>,
        #[serde(default)]
        enter_allowed: bool,
    },
    EnterResult {
        request_id: String,
        target_epoch: String,
        enter_id: String,
        status: String,
        #[serde(default)]
        reason: Option<String>,
    },
    Result {
        request_id: String,
        target_epoch: String,
        seq: u64,
        status: String,
        #[serde(default)]
        reason: Option<String>,
        #[serde(default)]
        proof: Option<String>,
    },
    Revoke {
        #[serde(default)]
        request_id: Option<String>,
        reason: String,
    },
    Heartbeat {},
}

/// Explicit alphabet: no titles, paths, URLs, markup, controls or bidi marks.
pub fn valid_app_label(label: &str) -> bool {
    label.chars().count() <= 32 && label.chars().all(|c| {
        c.is_ascii_alphanumeric() || matches!(c, ' ' | '.' | '-' | '_') ||
        matches!(c as u32, 0x3400..=0x4dbf | 0x4e00..=0x9fff | 0xf900..=0xfaff |
            0x3041..=0x3096 | 0x30a1..=0x30fa | 0x31f0..=0x31ff |
            0x1100..=0x11ff | 0x3131..=0x318e | 0xac00..=0xd7a3 |
            0x20000..=0x2fa1f | 0x30000..=0x323af)
    })
}
fn deserialize_app_label<'de, D: serde::Deserializer<'de>>(d: D) -> Result<Option<String>, D::Error> {
    let label = Option::<String>::deserialize(d)?;
    if label.as_deref().is_some_and(|s| !valid_app_label(s)) {
        return Err(serde::de::Error::custom("invalid app label"));
    }
    Ok(label)
}

/// Incremental, bounded decoder. A split frame is retained until complete;
/// disconnecting at any boundary (including inside a frame) is fatal.
pub struct FrameDecoder {
    header: [u8; 4],
    header_len: usize,
    expected: Option<usize>,
    body: Vec<u8>,
    failed: bool,
}

impl Default for FrameDecoder {
    fn default() -> Self {
        Self::new()
    }
}

impl FrameDecoder {
    pub fn new() -> Self {
        Self {
            header: [0; 4],
            header_len: 0,
            expected: None,
            body: Vec::new(),
            failed: false,
        }
    }

    pub fn feed(&mut self, mut bytes: &[u8]) -> io::Result<Vec<NativeMessage>> {
        if self.failed {
            return Err(invalid_frame());
        }
        let mut messages = Vec::new();
        while !bytes.is_empty() {
            if self.expected.is_none() {
                let take = (4 - self.header_len).min(bytes.len());
                self.header[self.header_len..self.header_len + take]
                    .copy_from_slice(&bytes[..take]);
                self.header_len += take;
                bytes = &bytes[take..];
                if self.header_len != 4 {
                    break;
                }
                let length = u32::from_le_bytes(self.header) as usize;
                if length == 0 || length > MAX_FRAME_BYTES {
                    self.failed = true;
                    return Err(invalid_frame());
                }
                self.expected = Some(length);
                self.body.clear();
            }
            let expected = self.expected.unwrap();
            let take = (expected - self.body.len()).min(bytes.len());
            self.body.extend_from_slice(&bytes[..take]);
            bytes = &bytes[take..];
            if self.body.len() == expected {
                let message = match serde_json::from_slice::<NativeMessage>(&self.body) {
                    Ok(message) => message,
                    Err(_) => {
                        self.failed = true;
                        return Err(invalid_frame());
                    }
                };
                messages.push(message);
                self.body.clear();
                self.expected = None;
                self.header_len = 0;
            }
        }
        Ok(messages)
    }

    pub fn finish(&mut self) -> io::Result<()> {
        self.failed = true;
        self.body.clear();
        Err(io::Error::new(
            io::ErrorKind::UnexpectedEof,
            "native peer disconnected",
        ))
    }
}

fn invalid_frame() -> io::Error {
    // Never include peer bytes: native messages may carry private information.
    io::Error::new(io::ErrorKind::InvalidData, "invalid native frame")
}

pub fn write_frame<W: Write, T: Serialize>(writer: &mut W, value: &T) -> io::Result<()> {
    let bytes = serde_json::to_vec(value).map_err(|_| invalid_frame())?;
    if bytes.is_empty() || bytes.len() > MAX_FRAME_BYTES {
        return Err(invalid_frame());
    }
    writer.write_all(&(bytes.len() as u32).to_le_bytes())?;
    writer.write_all(&bytes)?;
    writer.flush()
}

struct StdioLink {
    sender: mpsc::SyncSender<NativeCommand>,
}

impl NativeLink for StdioLink {
    fn send(&self, message: NativeCommand) -> io::Result<()> {
        self.sender.try_send(message).map_err(|error| match error {
            mpsc::TrySendError::Full(_) => {
                io::Error::new(io::ErrorKind::WouldBlock, "native channel full")
            }
            mpsc::TrySendError::Disconnected(_) => {
                io::Error::new(io::ErrorKind::BrokenPipe, "native channel disconnected")
            }
        })
    }
}

#[derive(Serialize)]
struct Hello<'a> {
    #[serde(rename = "type")]
    kind: &'static str,
    protocol_version: u32,
    server_boot_id: String,
    desktop_url: &'a str,
}

fn disconnect(app: &App) {
    app.native_shutdown();
}

/// Call once, after listeners are bound and before advertising native capability.
/// The hello is flushed before installing the link, so it is always frame one.
pub fn start(app: Arc<App>, desktop_url: String) -> io::Result<()> {
    let server_boot_id = app.realtime.server_boot_id();
    write_frame(
        &mut io::stdout().lock(),
        &Hello {
            kind: "hello",
            protocol_version: PROTOCOL_VERSION,
            server_boot_id,
            desktop_url: &desktop_url,
        },
    )?;

    let (sender, receiver) = mpsc::sync_channel::<NativeCommand>(64);
    app.realtime.attach_native(Arc::new(StdioLink { sender }));
    let writer_app = Arc::clone(&app);
    thread::spawn(move || {
        let stdout = io::stdout();
        let mut writer = stdout.lock();
        while let Ok(command) = receiver.recv() {
            if writer_app.is_shutdown() {
                break;
            }
            if !writer_app.realtime.native_command_allowed(&command) {
                continue;
            }
            if write_frame(&mut writer, &command).is_err() {
                eprintln!("Native output disconnected.");
                disconnect(&writer_app);
                break;
            }
        }
    });

    let reader_app = Arc::clone(&app);
    thread::spawn(move || {
        let stdin = io::stdin();
        let mut reader = stdin.lock();
        let mut decoder = FrameDecoder::new();
        let mut bytes = [0u8; 8192];
        loop {
            let count = match reader.read(&mut bytes) {
                Ok(0) => {
                    let _ = decoder.finish();
                    break;
                }
                Ok(count) => count,
                Err(error) if error.kind() == io::ErrorKind::Interrupted => continue,
                Err(_) => break,
            };
            let messages = match decoder.feed(&bytes[..count]) {
                Ok(messages) => messages,
                Err(_) => {
                    eprintln!("Invalid native input frame.");
                    break;
                }
            };
            let mut failed = false;
            for message in messages {
                if let Err(error) = reader_app.realtime.handle_native(message) {
                    // Valid late results and repeated grants cannot restore or
                    // extend an old lease. They also must not kill the process.
                    if error.status == 400 {
                        eprintln!("Invalid native state transition.");
                        failed = true;
                        break;
                    }
                }
            }
            if failed || reader_app.is_shutdown() {
                break;
            }
        }
        disconnect(&reader_app);
    });

    Ok(())
}
