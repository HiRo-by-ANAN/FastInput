//! In-memory realtime protocol. This module never reads or writes the shared notepad.
use crate::native_bridge::{NativeCommand, NativeLink, NativeMessage};
use rand::{rngs::OsRng, RngCore};
use serde::{Deserialize, Serialize};
use serde_json::{json, Value};
use std::{collections::{BTreeMap, HashSet, VecDeque}, sync::{Arc, Mutex}, time::Instant};

pub const MAX_UTF16: usize = 4096;
pub const MAX_UTF8: usize = 16 * 1024;
pub const MAX_BATCH: usize = 8;
pub const MAX_QUEUE: usize = 32;
pub const MAX_EPOCH_STATES: u64 = 10_000;
pub const LEASE_MS: u64 = 300_000;
pub const IDLE_MS: u64 = 60_000;
pub const HEARTBEAT_MS: u64 = 3_000;
// Phone heartbeat is more tolerant: mobile browsers throttle timers (screen dim, voice input, Wi-Fi power save).
pub const PHONE_HEARTBEAT_MS: u64 = 8_000;
pub const STALE_MS: u64 = 1500;
pub const EVENT_CAPACITY: usize = 128;
const REQUEST_MS: u64 = 30_000;

pub trait Clock: Send + Sync { fn now_ms(&self) -> u64; }
struct MonotonicClock(Instant);
impl Clock for MonotonicClock { fn now_ms(&self) -> u64 { self.0.elapsed().as_millis().min(u64::MAX as u128) as u64 } }

#[derive(Clone, Debug, Serialize, Deserialize, PartialEq, Eq)]
#[serde(deny_unknown_fields)]
pub struct Selection { pub start: u64, pub end: u64, pub direction: String }

#[derive(Clone, Debug, Serialize, Deserialize)]
#[serde(deny_unknown_fields)]
pub struct TextState {
    pub seq: u64,
    pub prev_seq: u64,
    pub text: String,
    pub selection_start: u64,
    pub selection_end: u64,
    pub selection_direction: String,
    pub composition: String,
    #[serde(default)]
    pub input_kind: Option<String>,
    #[serde(default)]
    pub client_elapsed_ms: Option<u64>,
}
impl PartialEq for TextState {
    fn eq(&self,other:&Self)->bool {
        self.seq==other.seq && self.prev_seq==other.prev_seq && self.text==other.text
            && self.selection_start==other.selection_start && self.selection_end==other.selection_end
            && self.selection_direction==other.selection_direction && self.composition==other.composition
    }
}
impl Eq for TextState {}

#[derive(Clone, Copy, Debug, Serialize, PartialEq, Eq)]
#[serde(rename_all="SCREAMING_SNAKE_CASE")]
pub enum Phase { Disabled, Requested, Armed, Binding, Active, Paused, Unknown, Revoked }

#[derive(Clone, Copy, Debug, Serialize, Deserialize, PartialEq, Eq)]
#[serde(rename_all="snake_case")]
pub enum Reason {
    FocusChanged, ContentDiverged, Expired, Unsupported, Busy, Released,
    CredentialRevoked, NativeDisconnected, NativeTimeout, HeartbeatTimeout, IdleTimeout,
    RequestExpired, ProtocolError, SequenceConflict, StaleState, EpochMismatch,
    QueueFull, RateLimited, EpochLimit, InvalidState, TargetChanged, PhysicalInput,
    Locked, Suspended, EmergencyStop, Partial, Unknown, Rejected,
    EnterSent, EnterDisabled, EnterBlockedTarget, TextPending, EnterBusy,
}
impl Reason { fn text(self) -> String { serde_json::to_value(self).unwrap().as_str().unwrap().to_owned() } }

#[derive(Debug)]
pub struct RealtimeError { pub status: u16, pub code: &'static str, pub body: Value }
pub type RealtimeResult = Result<Value, RealtimeError>;
fn error(status:u16, code:&'static str) -> RealtimeError { RealtimeError {status,code,body:json!({"code":code})} }
fn random_id() -> String { let mut bytes=[0u8;16]; OsRng.fill_bytes(&mut bytes); bytes.iter().map(|b|format!("{b:02x}")).collect() }

/// Fixed-width comparison, including malformed input: every call examines 32 bytes.
/// Rust/LLVM does not provide a formal constant-time compilation guarantee.
pub fn constant_time_capability_equal(expected:&str, supplied:&str) -> bool {
    let mut diff=expected.len() ^ supplied.len();
    for n in 0..32 { diff |= std::hint::black_box((expected.as_bytes().get(n).copied().unwrap_or(0) ^ supplied.as_bytes().get(n).copied().unwrap_or(0)) as usize); }
    std::hint::black_box(diff) == 0 && expected.len()==32
}

pub fn validate_text(state:&TextState) -> Result<(),RealtimeError> {
    if state.seq > MAX_EPOCH_STATES {return Err(error(409,"epoch_limit"));}
    if state.seq == 0 || state.prev_seq != state.seq-1 { return Err(error(422,"invalid_sequence")); }
    if state.composition != "committed" { return Err(error(422,"invalid_composition")); }
    if state.input_kind.as_deref().is_some_and(|kind| !matches!(kind,
        "insertText"|"insertReplacementText"|"insertCompositionText"|"insertFromPaste"|"insertFromDictation"|
        "deleteContentBackward"|"deleteContentForward"|"deleteWordBackward"|"deleteWordForward"|"deleteByCut"|"historyUndo"|"historyRedo")) {
        return Err(error(422,"invalid_input_kind"));
    }
    if !matches!(state.selection_direction.as_str(),"none"|"forward"|"backward") { return Err(error(422,"invalid_selection")); }
    if state.text.len()>MAX_UTF8 { return Err(error(413,"text_too_large")); }
    let mut len=0u64;
    let mut boundaries=HashSet::new(); boundaries.insert(0u64);
    for ch in state.text.chars() {
        let c=ch as u32;
        if c<=0x1f || (0x7f..=0x9f).contains(&c) || matches!(c,0x2028|0x2029|0x061c|0x200e|0x200f|0x202a..=0x202e|0x2066..=0x2069) { return Err(error(422,"forbidden_character")); }
        len+=ch.len_utf16() as u64; boundaries.insert(len);
    }
    if len>MAX_UTF16 as u64 { return Err(error(413,"text_too_large")); }
    if state.selection_start>state.selection_end || state.selection_end>len || !boundaries.contains(&state.selection_start) || !boundaries.contains(&state.selection_end) { return Err(error(422,"invalid_selection")); }
    Ok(())
}

#[derive(Deserialize)]
#[serde(deny_unknown_fields)]
struct RequestBody { client_nonce:String, #[serde(default)] v:Option<u64> }
#[derive(Deserialize)]
#[serde(deny_unknown_fields)]
struct ClaimBody { request_id:String, client_nonce:String, #[serde(default)] v:Option<u64> }
#[derive(Deserialize)]
#[serde(deny_unknown_fields)]
struct StatesBody { request_id:String, target_epoch:String, states:Vec<TextState>, #[serde(default)] v:Option<u64> }
#[derive(Deserialize)]
#[serde(deny_unknown_fields)]
struct HeartbeatBody { #[serde(default)] request_id:Option<String>, #[serde(default)] target_epoch:Option<String>, #[serde(default)] confirm_empty:bool, #[serde(default)] v:Option<u64> }
#[derive(Deserialize)]
#[serde(deny_unknown_fields)]
struct ReleaseBody { #[serde(default)] request_id:Option<String>, #[serde(default)] v:Option<u64> }
#[derive(Clone, Debug, Deserialize, PartialEq, Eq)]
#[serde(deny_unknown_fields)]
struct EnterBody { v:u64, request_id:String, target_epoch:String, enter_id:String, after_seq:u64, hold_ms:u64 }
fn parse<T:for<'de> Deserialize<'de>>(body:Value)->Result<T,RealtimeError> { serde_json::from_value(body).map_err(|_|error(400,"invalid_json")) }
fn version(v:Option<u64>)->Result<(),RealtimeError> { if v.is_some_and(|v|v!=1) { Err(error(400,"unsupported_version")) } else { Ok(()) } }
fn enter_rate_remaining(last:Option<u64>,now:u64)->u64 {last.map_or(0,|at|1000u64.saturating_sub(now.saturating_sub(at)))}

struct Entry { state:TextState, accepted_at:u64, status:&'static str, reason:Option<Reason>, proof:bool }
struct EnterEntry { body:EnterBody, accepted_at:u64, status:String }
struct Session {
    owner:String, request_id:String, session_id:String, nonce:String, check_code:String,
    requested_at:u64, granted_at:Option<u64>, capability:Option<String>, claimed:bool,
    phone_heartbeat:u64, last_text:u64, phase:Phase, reason:Option<Reason>,
    epoch:Option<String>, used_epochs:HashSet<String>, confirmed_empty:bool, uncertain:bool,
    accepted:u64, applied:u64, entries:BTreeMap<u64,Entry>, queue:VecDeque<u64>, inflight:Option<u64>,
    rate_units:u64, rate_at:u64,
    target_app:Option<String>, enter_allowed:bool, enters:BTreeMap<String,EnterEntry>,
    last_enter:Option<String>, enter_inflight:Option<String>, enter_rate_at:Option<u64>,
}
struct Event { id:u64, owner:String, body:Value }
struct Inner { boot_id:String, link:Option<Arc<dyn NativeLink>>, virtual_enter:bool, native_heartbeat:u64, session:Option<Session>, event_id:u64, events:VecDeque<Event> }
pub struct RealtimeState { clock:Arc<dyn Clock>, inner:Mutex<Inner>, notifier:Mutex<Option<Arc<dyn Fn()+Send+Sync>>> }

impl Default for RealtimeState { fn default()->Self { Self::new() } }
impl RealtimeState {
    pub fn new()->Self { Self::with_clock(Arc::new(MonotonicClock(Instant::now()))) }
    pub fn with_clock(clock:Arc<dyn Clock>)->Self { Self {clock,inner:Mutex::new(Inner{boot_id:random_id(),link:None,virtual_enter:false,native_heartbeat:0,session:None,event_id:0,events:VecDeque::new()}),notifier:Mutex::new(None)} }
    pub fn native_command_allowed(&self,command:&NativeCommand)->bool {
        self.update(|i,_| {
            match command {
                NativeCommand::Revoke{..} => true,
                NativeCommand::Request{request_id,..} => i.session.as_ref().is_some_and(|s|s.phase==Phase::Requested && &s.request_id==request_id),
                NativeCommand::ConfirmEmpty{request_id,target_epoch} => i.session.as_ref().is_some_and(|s|s.phase==Phase::Binding && s.confirmed_empty && &s.request_id==request_id && s.epoch.as_ref()==Some(target_epoch)),
                NativeCommand::Apply{request_id,target_epoch,seq,..} => i.session.as_ref().is_some_and(|s|s.phase==Phase::Active && &s.request_id==request_id && s.epoch.as_ref()==Some(target_epoch) && s.inflight==Some(*seq)),
                NativeCommand::PressEnter{request_id,target_epoch,enter_id,after_seq} => i.virtual_enter && i.session.as_ref().is_some_and(|s|s.phase==Phase::Active && s.enter_allowed && &s.request_id==request_id && s.epoch.as_ref()==Some(target_epoch) && s.enter_inflight.as_ref()==Some(enter_id) && s.accepted==*after_seq && s.applied==*after_seq && s.inflight.is_none() && s.queue.is_empty()),
            }
        })
    }
    pub fn server_boot_id(&self)->String { self.inner.lock().unwrap().boot_id.clone() }
    pub fn latest_event_id(&self)->u64 { self.inner.lock().unwrap().event_id }
    pub fn authorize(&self,credential:&str,capability:&str)->Result<(),RealtimeError> { self.update(|i,now|i.authorize(credential,capability,now)) }
    pub fn set_notifier(&self, notifier:Arc<dyn Fn()+Send+Sync>) { *self.notifier.lock().unwrap()=Some(notifier); }
    fn update<T>(&self, f:impl FnOnce(&mut Inner,u64)->T)->T {
        let now=self.clock.now_ms(); let mut i=self.inner.lock().unwrap(); let before=i.event_id;
        i.expire(now); let result=f(&mut i,now); let changed=i.event_id!=before; drop(i);
        if changed { let notify=self.notifier.lock().unwrap().clone(); if let Some(notify)=notify { notify(); } }
        result
    }
    pub fn attach_native(&self, link:Arc<dyn NativeLink>) { self.update(|i,now| { if i.link.is_some() { i.terminate(Phase::Revoked,Reason::NativeDisconnected,now,false); } i.virtual_enter=false;i.link=Some(link);i.native_heartbeat=now; }); }
    pub fn native_disconnected(&self) { self.update(|i,now| { i.virtual_enter=false;i.link=None;i.terminate(Phase::Revoked,Reason::NativeDisconnected,now,false); }); }
    pub fn tick(&self) { self.update(|i,now|i.dispatch(now)); }
    pub fn credential_revoked(&self, credential:&str) { self.update(|i,now| { if i.session.as_ref().is_some_and(|s|s.owner==credential) {i.terminate(Phase::Revoked,Reason::CredentialRevoked,now,true);} }); }
    pub fn request(&self,credential:&str,label:&str,body:Value)->RealtimeResult {
        let b:RequestBody=parse(body)?;version(b.v)?;
        if b.client_nonce.len()!=32 || !b.client_nonce.bytes().all(|b|b.is_ascii_hexdigit()) {return Err(error(422,"invalid_nonce"));}
        self.update(|i,now| {
            if i.link.is_none() {return Err(error(503,"native_unavailable"));}
            if let Some(s)=&i.session {if !matches!(s.phase,Phase::Disabled|Phase::Revoked) {
                if s.phase==Phase::Requested && s.owner==credential && constant_time_capability_equal(&s.nonce,&b.client_nonce) {let mut r=i.snapshot(credential,now);r["check_code"]=json!(s.check_code);return Ok(r);}
                return Err(i.failure(409,"busy",credential,now));
            }}
            let request_id=random_id(); let check_code=format!("{:02}",OsRng.next_u32()%100);
            i.session=Some(Session{owner:credential.to_owned(),request_id:request_id.clone(),session_id:random_id(),nonce:b.client_nonce,check_code:check_code.clone(),requested_at:now,granted_at:None,capability:None,claimed:false,phone_heartbeat:now,last_text:now,phase:Phase::Requested,reason:None,epoch:None,used_epochs:HashSet::new(),confirmed_empty:false,uncertain:false,accepted:0,applied:0,entries:BTreeMap::new(),queue:VecDeque::new(),inflight:None,rate_units:60_000,rate_at:now,target_app:None,enter_allowed:false,enters:BTreeMap::new(),last_enter:None,enter_inflight:None,enter_rate_at:None});
            i.emit(now,None);
            i.send(NativeCommand::Request{request_id,check_code:check_code.clone(),device_label:label.chars().filter(|c|!c.is_control()).take(80).collect()},now)?;
            let mut result=i.snapshot(credential,now);result["check_code"]=json!(check_code);Ok(result)
        })
    }
    pub fn claim(&self,credential:&str,body:Value)->RealtimeResult {
        let b:ClaimBody=parse(body)?;version(b.v)?;
        self.update(|i,now| {
            let s=i.session.as_mut().ok_or_else(||error(409,"no_request"))?;
            if s.owner!=credential || s.request_id!=b.request_id || !constant_time_capability_equal(&s.nonce,&b.client_nonce) { return Err(error(403,"invalid_claim")); }
            if s.phase==Phase::Requested { return Err(i.failure(409,"approval_pending",credential,now)); }
            if s.granted_at.is_none() || matches!(s.phase,Phase::Revoked|Phase::Disabled) {return Err(i.failure(410,"expired",credential,now));}
            if !s.claimed && now.saturating_sub(s.granted_at.unwrap())>=REQUEST_MS {i.terminate(Phase::Revoked,Reason::Expired,now,true);return Err(i.failure(410,"expired",credential,now));}
            s.claimed=true;
            let cap=s.capability.clone().unwrap();let mut result=i.snapshot(credential,now);result["capability"]=json!(cap);Ok(result)
        })
    }
    pub fn status(&self,credential:&str)->RealtimeResult {self.update(|i,now|Ok(i.snapshot(credential,now)))}
    pub fn heartbeat(&self,credential:&str,capability:&str,body:Value)->RealtimeResult {
        let b:HeartbeatBody=parse(body)?;version(b.v)?;
        self.update(|i,now| {
            i.authorize(credential,capability,now)?;
            let s=i.session.as_mut().unwrap();
            if b.request_id.as_ref().is_some_and(|id|id!=&s.request_id) {return Err(i.failure(409,"request_mismatch",credential,now));}
            if b.target_epoch.as_ref().is_some_and(|e|Some(e)!=s.epoch.as_ref()) {return Err(i.failure(409,"epoch_mismatch",credential,now));}
            s.phone_heartbeat=now;
            if b.confirm_empty {
                if s.phase!=Phase::Binding || b.target_epoch.as_ref()!=s.epoch.as_ref() || b.target_epoch.is_none() {return Err(i.failure(409,"invalid_phase",credential,now));}
                s.confirmed_empty=true;
                let command=NativeCommand::ConfirmEmpty{request_id:s.request_id.clone(),target_epoch:s.epoch.clone().unwrap()};i.send(command,now)?;
            }
            Ok(i.snapshot(credential,now))
        })
    }
    pub fn release(&self,credential:&str,capability:&str,body:Value)->RealtimeResult {
        let b:ReleaseBody=parse(body)?;version(b.v)?;
        self.update(|i,now| {i.authorize(credential,capability,now)?;if b.request_id.as_ref().is_some_and(|id|id!=&i.session.as_ref().unwrap().request_id) {return Err(i.failure(409,"request_mismatch",credential,now));} i.terminate(Phase::Revoked,Reason::Released,now,true);Ok(i.snapshot(credential,now))})
    }
    pub fn states(&self,credential:&str,capability:&str,body:Value)->RealtimeResult {
        let b:StatesBody=parse(body)?;version(b.v)?;
        self.update(|i,now| {
            i.authorize(credential,capability,now)?;
            let s=i.session.as_ref().unwrap();
            if s.request_id!=b.request_id {return Err(i.failure(409,"request_mismatch",credential,now));}
            if s.epoch.as_deref()!=Some(&b.target_epoch) {return Err(i.failure(409,"epoch_mismatch",credential,now));}
            if s.phase!=Phase::Active {return Err(i.failure(409,"invalid_phase",credential,now));}
            if s.enter_inflight.is_some() {return Err(i.failure(409,"enter_busy",credential,now));}
            if b.states.is_empty() || b.states.len()>MAX_BATCH {return Err(i.failure(422,"invalid_batch",credential,now));}
            let mut expected=s.accepted+1;let mut fresh=0usize;
            for (n,state) in b.states.iter().enumerate() {
                if n>0 && state.seq!=b.states[n-1].seq+1 {return Err(i.failure(409,"sequence_gap",credential,now));}
                if let Some(old)=s.entries.get(&state.seq) {if old.state!=*state {i.terminate(Phase::Paused,Reason::SequenceConflict,now,true);return Err(i.failure(409,"sequence_conflict",credential,now));}}
                else {if state.seq!=expected {return Err(i.failure(409,"sequence_gap",credential,now));}expected+=1;fresh+=1;}
                if state.seq>MAX_EPOCH_STATES {return Err(i.failure(409,"epoch_limit",credential,now));}
                if let Err(e)=validate_text(state) {return Err(i.failure(e.status,e.code,credential,now));}
            }
            if expected-1>MAX_EPOCH_STATES {return Err(i.failure(409,"epoch_limit",credential,now));}
            if s.queue.len()+usize::from(s.inflight.is_some())+fresh>MAX_QUEUE {i.terminate(Phase::Paused,Reason::QueueFull,now,true);return Err(i.failure(429,"queue_full",credential,now));}
            let s=i.session.as_mut().unwrap();
            s.rate_units=(s.rate_units.saturating_add(now.saturating_sub(s.rate_at).saturating_mul(30))).min(60_000);s.rate_at=now;
            if s.rate_units<(fresh as u64)*1000 {let retry=((fresh as u64)*1000-s.rate_units).div_ceil(30);let mut err=i.failure(429,"rate_limited",credential,now);err.body["retry_after_ms"]=json!(retry);return Err(err);}
            s.rate_units-=(fresh as u64)*1000;
            for state in &b.states {
                if !s.entries.contains_key(&state.seq) {
                    // A heartbeat, retransmission, or selection-only state cannot
                    // extend the text-idle lease. Each epoch starts from empty.
                    let text_changed=s.entries.get(&s.accepted)
                        .map_or(!state.text.is_empty(),|previous|previous.state.text!=state.text);
                    if text_changed {s.last_text=now;}
                    s.accepted=state.seq;s.queue.push_back(state.seq);
                    s.entries.insert(state.seq,Entry{state:state.clone(),accepted_at:now,status:"accepted",reason:None,proof:false});
                }
            }
            if fresh>0 {for state in &b.states {i.emit(now,Some(state.seq));}}
            i.dispatch(now);
            let mut result=i.snapshot(credential,now);
            result["results"]=json!(b.states.iter().map(|s|i.sequence(s.seq)).collect::<Vec<_>>());Ok(result)
        })
    }
    pub fn enter(&self,credential:&str,capability:&str,body:Value)->RealtimeResult {
        let b:EnterBody=parse(body)?;version(Some(b.v))?;
        if b.enter_id.len()!=32 || !b.enter_id.bytes().all(|c|c.is_ascii_hexdigit()) {return Err(error(422,"invalid_enter_id"));}
        self.update(|i,now| {
            i.authorize(credential,capability,now)?;
            let s=i.session.as_ref().unwrap();
            if s.request_id!=b.request_id {return Err(i.failure(409,"request_mismatch",credential,now));}
            // Idempotency precedes epoch/phase checks so completed requests can be queried.
            if let Some(old)=s.enters.get(&b.enter_id) {
                if old.body!=b {return Err(i.failure(409,"sequence_conflict",credential,now));}
                let mut result=i.snapshot(credential,now);
                result["enter"]=json!({"enter_id":b.enter_id,"status":old.status});return Ok(result);
            }
            if s.phase!=Phase::Active {return Err(i.failure(409,"invalid_phase",credential,now));}
            if s.epoch.as_deref()!=Some(&b.target_epoch) {return Err(i.failure(409,"epoch_mismatch",credential,now));}
            if !i.virtual_enter || !s.enter_allowed {return Err(i.failure(409,"enter_disabled",credential,now));}
            if b.after_seq!=s.accepted || s.accepted!=s.applied || s.inflight.is_some() || !s.queue.is_empty() {return Err(i.failure(409,"text_pending",credential,now));}
            if b.hold_ms<600 {return Err(i.failure(422,"invalid_hold",credential,now));}
            if s.enter_inflight.is_some() {return Err(i.failure(409,"busy",credential,now));}
            let retry=enter_rate_remaining(s.enter_rate_at,now);
            if retry>0 {let mut e=i.failure(429,"rate_limited",credential,now);e.body["retry_after_ms"]=json!(retry);return Err(e);}
            let s=i.session.as_mut().unwrap();
            s.enter_rate_at=Some(now);s.last_enter=Some(b.enter_id.clone());s.enter_inflight=Some(b.enter_id.clone());
            let command=NativeCommand::PressEnter{request_id:b.request_id.clone(),target_epoch:b.target_epoch.clone(),enter_id:b.enter_id.clone(),after_seq:b.after_seq};
            s.enters.insert(b.enter_id.clone(),EnterEntry{body:b,accepted_at:now,status:"requested".into()});
            i.emit(now,None);i.send(command,now)?;Ok(i.snapshot(credential,now))
        })
    }
    pub fn events(&self,credential:&str,after:u64)->Value {
        self.update(|i,now| {
            let reset=after>i.event_id || i.events.front().is_some_and(|e|after<e.id.saturating_sub(1));
            let events=if reset {Vec::new()}else{i.events.iter().filter(|e|e.id>after && e.owner==credential).map(|e|e.body.clone()).collect()};
            json!({"events":events,"reset_required":reset,"event_id":i.event_id,"status":if reset {Some(i.snapshot(credential,now))}else{None}})
        })
    }
    pub fn handle_native(&self,message:NativeMessage)->Result<(),RealtimeError> {
        self.update(|i,now| {
            if i.link.is_none() {return Err(error(409,"native_unavailable"));}
            i.native_heartbeat=now;
            match message {
                NativeMessage::Heartbeat{} => {},
                NativeMessage::Capabilities{virtual_enter} => {
                    i.virtual_enter=virtual_enter;
                    if !virtual_enter && i.session.as_ref().is_some_and(|s|s.enter_inflight.is_some()) {
                        // The pipe command may already have left Rust. Its delivery cannot be undone.
                        i.finish_enter("unknown",now,true);
                    } else {i.emit(now,None);}
                },
                NativeMessage::Grant{request_id} => {
                    let s=i.session.as_mut().ok_or_else(||error(409,"no_request"))?;
                    if s.request_id!=request_id || s.phase!=Phase::Requested {return Err(error(409,"invalid_grant"));}
                    s.granted_at=Some(now);s.phone_heartbeat=now;s.last_text=now;s.capability=Some(random_id());s.phase=Phase::Armed;i.emit(now,None);
                },
                NativeMessage::TargetBound{request_id,target_epoch,phase,app_label,enter_allowed} => {
                    if app_label.as_deref().is_some_and(|s|!crate::native_bridge::valid_app_label(s)) {
                        i.terminate(Phase::Revoked,Reason::ProtocolError,now,true);return Err(error(400,"invalid_app_label"));
                    }
                    if target_epoch.is_empty() || target_epoch.len()>128 || !target_epoch.bytes().all(|c|c.is_ascii_alphanumeric()||c==b'-'||c==b'_') {return Err(error(400,"invalid_epoch"));}
                    let s=i.session.as_mut().ok_or_else(||error(409,"no_request"))?;
                    if s.request_id!=request_id || !s.claimed {return Err(error(409,"invalid_binding"));}
                    match phase.as_deref().unwrap_or("binding") {
                        "binding" => {
                            if !matches!(s.phase,Phase::Armed|Phase::Paused|Phase::Active) || s.inflight.is_some() || s.enter_inflight.is_some() || s.uncertain || s.used_epochs.contains(&target_epoch) || s.used_epochs.len()>=MAX_EPOCH_STATES as usize {return Err(error(409,"invalid_binding"));}
                            s.queue.clear();s.entries.clear();s.accepted=0;s.applied=0;s.used_epochs.insert(target_epoch.clone());s.epoch=Some(target_epoch);s.confirmed_empty=false;s.phase=Phase::Binding;s.reason=None;s.rate_units=60_000;s.rate_at=now;
                            s.target_app=app_label;s.enter_allowed=enter_allowed;s.last_enter=None;s.enter_rate_at=None;
                        },
                        "active" => {if s.phase!=Phase::Binding || s.epoch.as_deref()!=Some(&target_epoch) || !s.confirmed_empty {return Err(error(409,"invalid_binding"));}s.phase=Phase::Active;s.target_app=app_label;s.enter_allowed=enter_allowed;},
                        _ => return Err(error(400,"invalid_phase")),
                    }
                    i.emit(now,None);
                },
                NativeMessage::EnterResult{request_id,target_epoch,enter_id,status,reason} => {
                    let _reason=reason.map(|r|serde_json::from_value::<Reason>(json!(r)).map_err(|_|error(400,"invalid_reason"))).transpose()?;
                    if !matches!(status.as_str(),"applied"|"rejected"|"unknown") {return Err(error(400,"invalid_result"));}
                    let s=i.session.as_ref().ok_or_else(||error(409,"no_request"))?;
                    if s.request_id!=request_id || s.epoch.as_deref()!=Some(&target_epoch) || s.enter_inflight.as_deref()!=Some(&enter_id) || s.phase!=Phase::Active {return Err(error(409,"stale_result"));}
                    i.finish_enter(&status,now,false);
                },
                NativeMessage::Result{request_id,target_epoch,seq,status,reason,proof} => {
                    let reason=reason.map(|r|serde_json::from_value::<Reason>(json!(r)).map_err(|_|error(400,"invalid_reason"))).transpose()?;
                    if !matches!(status.as_str(),"applied"|"cancelled"|"rejected"|"partial"|"unknown") || proof.as_deref().is_some_and(|p|p!="verified_readback") {return Err(error(400,"invalid_result"));}
                    let s=i.session.as_mut().ok_or_else(||error(409,"no_request"))?;
                    if s.request_id!=request_id || s.epoch.as_deref()!=Some(&target_epoch) || s.inflight!=Some(seq) || s.phase!=Phase::Active {return Err(error(409,"stale_result"));}
                    if status=="applied" && (proof.as_deref()!=Some("verified_readback") || seq!=s.applied+1) {return Err(error(400,"invalid_proof"));}
                    let e=s.entries.get_mut(&seq).unwrap();
                    e.status=match status.as_str(){"applied"=>"applied","cancelled"=>"cancelled","rejected"=>"rejected","partial"=>"partial",_=>"unknown"};e.reason=reason;e.proof=status=="applied";s.inflight=None;
                    if status=="applied" {s.applied=seq;i.emit(now,Some(seq));i.dispatch(now);}
                    else {let phase=if matches!(status.as_str(),"partial"|"unknown"){Phase::Unknown}else{Phase::Paused};i.emit(now,Some(seq));i.terminate(phase,reason.unwrap_or(if status=="partial"{Reason::Partial}else if status=="unknown"{Reason::Unknown}else{Reason::Rejected}),now,true);}
                },
                NativeMessage::Revoke{request_id,reason} => {
                    let reason=serde_json::from_value::<Reason>(json!(reason)).map_err(|_|error(400,"invalid_reason"))?;
                    if request_id.as_ref().is_some_and(|id|i.session.as_ref().is_none_or(|s|id!=&s.request_id)) {return Err(error(409,"request_mismatch"));}
                    i.terminate(Phase::Revoked,reason,now,false);
                },
            }
            Ok(())
        })
    }
}

impl Inner {
    fn failure(&self,status:u16,code:&'static str,owner:&str,now:u64)->RealtimeError {let mut body=self.snapshot(owner,now);body["code"]=json!(code);RealtimeError{status,code,body}}
    fn authorize(&self,owner:&str,cap:&str,now:u64)->Result<(),RealtimeError> {
        let Some(s)=&self.session else {return Err(error(403,"invalid_capability"));};
        // The capability comparison always runs, including an incorrect credential.
        let equal=constant_time_capability_equal(s.capability.as_deref().unwrap_or("00000000000000000000000000000000"),cap);
        if s.owner!=owner || !equal || !s.claimed {return Err(error(403,"invalid_capability"));}
        if matches!(s.phase,Phase::Disabled|Phase::Revoked) {return Err(self.failure(410,"expired",owner,now));}
        Ok(())
    }
    fn sequence(&self,seq:u64)->Value {
        if let Some(e)=self.session.as_ref().and_then(|s|s.entries.get(&seq)) {json!({"seq":seq,"seq_status":e.status,"reason":e.reason,"proof":if e.proof{Some("verified_readback")}else{None}})} else {json!({"seq":seq,"seq_status":"unknown","reason":Reason::Unknown,"proof":null})}
    }
    fn snapshot(&self,owner:&str,now:u64)->Value {
        let Some(s)=self.session.as_ref().filter(|s|s.owner==owner) else {return json!({"v":1,"server_boot_id":self.boot_id,"phase":Phase::Disabled,"available":self.link.is_some(),"enter_available":false,"target_app":null,"enter":null,"accepted_seq":0,"applied_seq":0,"expected_seq":1,"seq_status":null,"proof":null,"reason":null,"retry_after_ms":null,"lease_remaining_ms":0,"event_id":self.event_id});};
        let last=s.entries.get(&s.accepted);
        let enter=s.last_enter.as_ref().and_then(|id|s.enters.get(id)).map(|e|json!({"enter_id":e.body.enter_id,"status":e.status}));
        json!({"v":1,"server_boot_id":self.boot_id,"request_id":s.request_id,"session_id":s.session_id,"target_epoch":s.epoch,"phase":s.phase,"available":self.link.is_some(),"enter_available":self.virtual_enter && s.phase==Phase::Active && s.enter_allowed,"target_app":s.target_app,"enter":enter,"accepted_seq":s.accepted,"applied_seq":s.applied,"expected_seq":s.accepted+1,"seq_status":last.map(|e|e.status),"proof":if last.is_some_and(|e|e.proof){Some("verified_readback")}else{None},"reason":s.reason,"retry_after_ms":null,"lease_remaining_ms":if matches!(s.phase,Phase::Revoked|Phase::Disabled){0}else{s.granted_at.map(|at|LEASE_MS.saturating_sub(now.saturating_sub(at))).unwrap_or(0)},"event_id":self.event_id})
    }
    fn emit(&mut self,now:u64,seq:Option<u64>) {
        if let Some(s)=&self.session {let owner=s.owner.clone();self.event_id+=1;let mut body=self.snapshot(&owner,now);if let Some(seq)=seq {let status=self.sequence(seq);for (key,value) in status.as_object().unwrap(){body[key]=value.clone();}}self.events.push_back(Event{id:self.event_id,owner,body});while self.events.len()>EVENT_CAPACITY {self.events.pop_front();}}
    }
    fn send(&mut self,command:NativeCommand,now:u64)->Result<(),RealtimeError> {
        let result=self.link.as_ref().ok_or_else(||error(503,"native_unavailable"))?.send(command);
        if result.is_err() {self.link=None;self.terminate(Phase::Revoked,Reason::NativeDisconnected,now,false);return Err(error(503,"native_unavailable"));}Ok(())
    }
    fn terminate(&mut self,phase:Phase,reason:Reason,now:u64,notify_native:bool) {
        let Some(s)=self.session.as_mut() else{return;};
        if matches!(s.phase,Phase::Revoked|Phase::Disabled) {return;}
        let request_id=s.request_id.clone();
        let queued:Vec<_>=s.queue.drain(..).collect();
        for seq in &queued {if let Some(e)=s.entries.get_mut(seq){e.status="cancelled";e.reason=Some(reason);}}
        let inflight=s.inflight.take();s.uncertain |= inflight.is_some();if let Some(seq)=inflight {if let Some(e)=s.entries.get_mut(&seq){e.status="unknown";e.reason=Some(reason);}}
        if let Some(id)=s.enter_inflight.take() {if let Some(e)=s.enters.get_mut(&id) {e.status="unknown".into();}}
        s.phase=phase;s.reason=Some(reason);s.epoch=None;s.confirmed_empty=false;
        if phase==Phase::Revoked {s.capability=None;s.nonce.clear();}
        for seq in queued {self.emit(now,Some(seq));}if let Some(seq)=inflight {self.emit(now,Some(seq));}self.emit(now,None);
        if notify_native {if let Some(link)=&self.link {if link.send(NativeCommand::Revoke{request_id:Some(request_id),reason:reason.text()}).is_err(){self.link=None;}}}
    }
    fn finish_enter(&mut self,status:&str,now:u64,notify_native:bool) {
        let Some(s)=self.session.as_mut() else{return;};
        let Some(id)=s.enter_inflight.take() else{return;};
        s.enters.get_mut(&id).unwrap().status=status.to_owned();
        self.terminate(Phase::Paused,Reason::EnterSent,now,notify_native);
    }
    fn expire(&mut self,now:u64) {
        let Some(s)=&self.session else{return;};if matches!(s.phase,Phase::Revoked|Phase::Disabled){return;}
        let expired=if self.link.is_none(){Some(Reason::NativeDisconnected)}else if now.saturating_sub(self.native_heartbeat)>=HEARTBEAT_MS {Some(Reason::NativeTimeout)} else if s.phase==Phase::Requested {if now.saturating_sub(s.requested_at)>=REQUEST_MS{Some(Reason::RequestExpired)}else{None}} else if s.granted_at.is_some_and(|at|now.saturating_sub(at)>=LEASE_MS){Some(Reason::Expired)}else if now.saturating_sub(s.last_text)>=IDLE_MS{Some(Reason::IdleTimeout)}else if now.saturating_sub(s.phone_heartbeat)>=PHONE_HEARTBEAT_MS{Some(Reason::HeartbeatTimeout)}else{None};
        if let Some(reason)=expired {self.terminate(Phase::Revoked,reason,now,true);return;}
        if s.enter_inflight.as_ref().and_then(|id|s.enters.get(id)).is_some_and(|e|now.saturating_sub(e.accepted_at)>=STALE_MS) {
            self.finish_enter("unknown",now,true);return;
        }
        if s.phase==Phase::Active {
            if s.queue.front().and_then(|seq|s.entries.get(seq)).is_some_and(|e|now.saturating_sub(e.accepted_at)>=STALE_MS) {self.terminate(Phase::Paused,Reason::StaleState,now,true);}
            else if s.inflight.and_then(|seq|s.entries.get(&seq)).is_some_and(|e|now.saturating_sub(e.accepted_at)>=STALE_MS) {self.terminate(Phase::Unknown,Reason::StaleState,now,true);}
        }
    }
    fn dispatch(&mut self,now:u64) {
        let Some(s)=self.session.as_mut() else{return;};if s.phase!=Phase::Active || s.inflight.is_some() || s.enter_inflight.is_some(){return;}
        let Some(seq)=s.queue.pop_front() else{return;};
        let e=s.entries.get_mut(&seq).unwrap();if now.saturating_sub(e.accepted_at)>=STALE_MS {s.queue.push_front(seq);self.terminate(Phase::Paused,Reason::StaleState,now,true);return;}
        e.status="applying";s.inflight=Some(seq);
        let command=NativeCommand::Apply{request_id:s.request_id.clone(),target_epoch:s.epoch.clone().unwrap(),seq,prev_seq:e.state.prev_seq,text:e.state.text.clone(),selection:Selection{start:e.state.selection_start,end:e.state.selection_end,direction:e.state.selection_direction.clone()},composition:e.state.composition.clone()};self.emit(now,Some(seq));let _=self.send(command,now);
    }
}

#[cfg(test)]
mod enter_rate_tests {
    use super::*;
    #[test]
    fn realtime_enter_rate_999_1000_boundary() {
        assert_eq!(enter_rate_remaining(None,0),0);
        assert_eq!(enter_rate_remaining(Some(0),0),1000);
        assert_eq!(enter_rate_remaining(Some(0),999),1);
        assert_eq!(enter_rate_remaining(Some(0),1000),0);
        assert_eq!(enter_rate_remaining(Some(0),1001),0);
    }
    #[test]
    fn realtime_enter_rate_limited_snapshot_and_retry() {
        struct FixedClock;
        impl Clock for FixedClock {fn now_ms(&self)->u64 {999}}
        struct Link;
        impl NativeLink for Link {fn send(&self,_:NativeCommand)->std::io::Result<()> {Ok(())}}
        let state=RealtimeState::with_clock(Arc::new(FixedClock));state.attach_native(Arc::new(Link));
        let nonce="00000000000000000000000000000000";
        let request=state.request("owner","phone",json!({"client_nonce":nonce})).unwrap();let id=request["request_id"].as_str().unwrap();
        state.handle_native(NativeMessage::Grant{request_id:id.into()}).unwrap();let cap=state.claim("owner",json!({"request_id":id,"client_nonce":nonce})).unwrap()["capability"].as_str().unwrap().to_owned();
        state.handle_native(NativeMessage::Capabilities{virtual_enter:true}).unwrap();
        for phase in ["binding","active"] {
            state.handle_native(NativeMessage::TargetBound{request_id:id.into(),target_epoch:"epoch".into(),phase:Some(phase.into()),app_label:None,enter_allowed:true}).unwrap();
            if phase=="binding" {state.heartbeat("owner",&cap,json!({"target_epoch":"epoch","confirm_empty":true})).unwrap();}
        }
        // Normal completion ends an epoch, making a second fresh Enter unreachable.
        // Seed the cooldown to exercise the independent defensive rate gate itself.
        state.inner.lock().unwrap().session.as_mut().unwrap().enter_rate_at=Some(0);
        let body=json!({"v":1,"request_id":id,"target_epoch":"epoch","enter_id":nonce,"after_seq":0,"hold_ms":600});
        let e=state.enter("owner",&cap,body.clone()).unwrap_err();assert_eq!((e.status,e.code),(429,"rate_limited"));assert_eq!(e.body["retry_after_ms"],1);assert!(e.body["enter"].is_null());
        state.inner.lock().unwrap().session.as_mut().unwrap().enter_rate_at=None;
        assert_eq!(state.enter("owner",&cap,body).unwrap()["enter"]["status"],"requested");
    }
}
