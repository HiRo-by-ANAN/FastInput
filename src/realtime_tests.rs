//! Deterministic model tests and production TCP/stdio wire tests for the M1 core.
use crate::{native_bridge::{self, FrameDecoder, NativeCommand, NativeLink, NativeMessage}, realtime::{Clock, RealtimeState}, state::App};
use serde_json::{json, Value};
use std::{fs, io::{self, BufRead, BufReader, Read, Write}, net::{IpAddr, Ipv4Addr, TcpListener, TcpStream}, path::PathBuf, process::{Command, Stdio}, sync::{Arc, Mutex, atomic::{AtomicU64, Ordering}}, thread::{self, JoinHandle}, time::{Duration, Instant}};

const NONCE: &str = "0123456789abcdef0123456789abcdef";
const OWNER: &str = "credential-a";
const EPOCH: &str = "0123456789abcdef0123456789abcdef";
#[derive(Default)] struct TestClock(AtomicU64);
impl Clock for TestClock { fn now_ms(&self) -> u64 { self.0.load(Ordering::SeqCst) } }
impl TestClock { fn advance(&self, ms:u64) { self.0.fetch_add(ms,Ordering::SeqCst); } }
#[derive(Default)] struct FakeNative(Mutex<Vec<Value>>);
impl NativeLink for FakeNative { fn send(&self, command:NativeCommand)->io::Result<()> { self.0.lock().unwrap().push(serde_json::to_value(command).unwrap()); Ok(()) } }
impl FakeNative { fn applies(&self)->Vec<Value> { self.0.lock().unwrap().iter().filter(|v|v["type"]=="apply").cloned().collect() } }

fn native(value:Value)->NativeMessage { serde_json::from_value(value).unwrap() }
fn text_state(seq:u64,text:&str)->Value { json!({"seq":seq,"prev_seq":seq-1,"text":text,"selection_start":text.encode_utf16().count(),"selection_end":text.encode_utf16().count(),"selection_direction":"none","composition":"committed"}) }
struct Model { state:RealtimeState, clock:Arc<TestClock>, link:Arc<FakeNative>, id:String, cap:String }
impl Model {
    fn new()->Self {
        let clock=Arc::new(TestClock::default()); let state=RealtimeState::with_clock(clock.clone()); let link=Arc::new(FakeNative::default()); state.attach_native(link.clone());
        let request=state.request(OWNER,"測試手機",json!({"client_nonce":NONCE})).unwrap();
        let id=request["request_id"].as_str().unwrap().to_owned();
        state.handle_native(native(json!({"type":"grant","request_id":id}))).unwrap();
        let claim=state.claim(OWNER,json!({"request_id":id,"client_nonce":NONCE})).unwrap();
        let cap=claim["capability"].as_str().unwrap().to_owned();
        state.handle_native(native(json!({"type":"target_bound","request_id":id,"target_epoch":EPOCH}))).unwrap();
        state.heartbeat(OWNER,&cap,json!({"target_epoch":EPOCH,"confirm_empty":true})).unwrap();
        state.handle_native(native(json!({"type":"target_bound","request_id":id,"target_epoch":EPOCH,"phase":"active"}))).unwrap();
        Self{state,clock,link,id,cap}
    }
    fn submit(&self, states:Vec<Value>)->Result<Value,crate::realtime::RealtimeError> { self.state.states(OWNER,&self.cap,json!({"request_id":self.id,"target_epoch":EPOCH,"states":states})) }
    fn result(&self, seq:u64, status:&str) { self.state.handle_native(native(json!({"type":"result","request_id":self.id,"target_epoch":EPOCH,"seq":seq,"status":status,"proof":if status=="applied" {Some("verified_readback")} else {None},"reason":if status=="applied" {None} else {Some("content_diverged")}}))).unwrap(); }
    fn status(&self)->Value { self.state.status(OWNER).unwrap() }
    fn keepalive(&self) { self.state.handle_native(native(json!({"type":"heartbeat"}))).unwrap(); self.state.heartbeat(OWNER,&self.cap,json!({"target_epoch":EPOCH})).unwrap(); }
}

#[test] fn realtime_contiguous_batch_dedup_and_single_delivery() {
    let m=Model::new(); let batch=vec![text_state(1,"G"),text_state(2,"Go"),text_state(3,"Google")];
    let accepted=m.submit(batch.clone()).unwrap(); assert_eq!(accepted["accepted_seq"],3); assert_eq!(accepted["applied_seq"],0);
    assert_eq!(m.link.applies().len(),1); assert_eq!(m.link.applies()[0]["seq"],1);
    m.submit(batch).unwrap(); assert_eq!(m.link.applies().len(),1);
    m.result(1,"applied"); assert_eq!(m.status()["applied_seq"],1); assert_eq!(m.link.applies()[1]["seq"],2);
    m.result(2,"applied"); m.result(3,"applied"); assert_eq!(m.status()["applied_seq"],3);
    m.submit(vec![text_state(3,"Google")]).unwrap(); assert_eq!(m.link.applies().len(),3);
}
#[test] fn realtime_gap_expected_seq_and_conflicting_duplicate_pause() {
    let m=Model::new(); let gap=m.submit(vec![text_state(2,"b")]).unwrap_err(); assert_eq!(gap.status,409); assert_eq!(gap.body["expected_seq"],1);
    m.submit(vec![text_state(1,"a")]).unwrap(); let conflict=m.submit(vec![text_state(1,"different")]).unwrap_err(); assert_eq!(conflict.status,409); assert_eq!(m.status()["phase"],"PAUSED"); assert_eq!(m.link.applies().len(),1);
}
#[test] fn realtime_batch_atomicity_epoch_and_prev_seq() {
    let m=Model::new(); let err=m.state.states(OWNER,&m.cap,json!({"request_id":m.id,"target_epoch":"wrong","states":[text_state(1,"a")]})).unwrap_err(); assert_eq!(err.status,409);
    let mut invalid=text_state(2,"ab"); invalid["prev_seq"]=json!(0);
    assert!(m.submit(vec![text_state(1,"a"),invalid]).is_err()); assert_eq!(m.status()["accepted_seq"],0);
    assert!(m.submit((1..=9).map(|n|text_state(n,"x")).collect()).is_err()); assert_eq!(m.status()["accepted_seq"],0);
}
#[test] fn realtime_utf16_selection_and_text_boundaries() {
    for (text,start,end,valid) in [("A😀B",1,3,true),("A😀B",2,2,false),("A😀B",3,4,true),("A😀B",4,5,false),("A😀B",3,1,false),("",0,0,true)] {
        let m=Model::new(); let mut s=text_state(1,text); s["selection_start"]=json!(start);s["selection_end"]=json!(end); assert_eq!(m.submit(vec![s]).is_ok(),valid,"{text:?} [{start},{end}]");
    }
    let m=Model::new(); assert!(m.submit(vec![text_state(1,&"a".repeat(4096))]).is_ok());
    let m=Model::new(); assert!(m.submit(vec![text_state(1,&"a".repeat(4097))]).is_err());
    let m=Model::new(); assert!(m.submit(vec![text_state(1,&"😀".repeat(2048))]).is_ok());
    let m=Model::new(); assert!(m.submit(vec![text_state(1,&"😀".repeat(2049))]).is_err());
    let m=Model::new(); let mut s=text_state(1,"x");s["composition"]=json!("composing");assert!(m.submit(vec![s]).is_err());
    println!("UTF-16 verified: 4096 accepted / 4097 rejected; emoji 2048 accepted / 2049 rejected; surrogate midpoint rejected");
}
#[test] fn realtime_controls_bidi_and_isolated_surrogates_rejected() {
    for cp in (0..=31).chain(127..=159).chain([0x2028,0x2029,0x061c,0x200e,0x200f,0x202a,0x202b,0x202c,0x202d,0x202e,0x2066,0x2067,0x2068,0x2069]) {
        let m=Model::new();let text=char::from_u32(cp).unwrap().to_string(); assert!(m.submit(vec![text_state(1,&text)]).is_err(),"control U+{cp:04X}");
    }
    assert!(serde_json::from_str::<Value>(r#"{"text":"\ud800"}"#).is_err()); assert!(serde_json::from_str::<Value>(r#"{"text":"\udfff"}"#).is_err());
}
#[test] fn realtime_queue_capacity_and_stale_pause() {
    let m=Model::new(); for n in 0..4 { m.submit((1+n*8..=8+n*8).map(|s|text_state(s,"x")).collect()).unwrap(); }
    assert_eq!(m.status()["accepted_seq"],32); assert!(m.submit(vec![text_state(33,"x")]).is_err());
    let m=Model::new();m.submit(vec![text_state(1,"a"),text_state(2,"ab")]).unwrap();m.clock.advance(1499);m.state.tick();assert_eq!(m.status()["phase"],"ACTIVE");m.clock.advance(1);m.state.tick();assert_eq!(m.status()["phase"],"PAUSED");assert_eq!(m.status()["reason"],"stale_state");assert_eq!(m.link.applies().len(),1);
}
#[test] fn realtime_rate_burst_sixty_and_thirty_per_second() {
    let m=Model::new(); for seq in 1..=60 { m.submit(vec![text_state(seq,"x")]).unwrap();m.result(seq,"applied"); }
    let err=m.submit(vec![text_state(61,"x")]).unwrap_err();assert_eq!(err.status,429);assert!(err.body["retry_after_ms"].as_u64().unwrap()>0);
    m.clock.advance(1000);m.keepalive(); for seq in 61..=90 { m.submit(vec![text_state(seq,"x")]).unwrap();m.result(seq,"applied"); }
    assert_eq!(m.submit(vec![text_state(91,"x")]).unwrap_err().status,429);
    m.clock.advance(33);assert_eq!(m.submit(vec![text_state(91,"x")]).unwrap_err().status,429);m.clock.advance(1);assert!(m.submit(vec![text_state(91,"x")]).is_ok());
    println!("Rate verified: burst 60 accepted, #61=429; +1000ms refills exactly 30, #91=429");
}
#[test] fn realtime_absolute_idle_and_phone_heartbeat_leases() {
    let hb=|m:&Model|{m.state.handle_native(native(json!({"type":"heartbeat"}))).unwrap();};let m=Model::new();for _ in 0..7 { m.clock.advance(1000);hb(&m); }m.clock.advance(999);hb(&m);m.state.tick();assert_ne!(m.status()["reason"],"heartbeat_timeout");m.clock.advance(1);m.state.tick();assert_eq!(m.status()["reason"],"heartbeat_timeout");assert_ne!(m.status()["phase"],"ACTIVE");
    let m=Model::new(); for _ in 0..59 { m.clock.advance(1000);m.keepalive(); } assert_eq!(m.status()["phase"],"ACTIVE");m.clock.advance(1000);m.state.tick();assert_eq!(m.status()["reason"],"idle_timeout");assert_ne!(m.status()["phase"],"ACTIVE");
    let m=Model::new();for n in 1..=299 { m.clock.advance(1000);m.keepalive();if n%20==0 { let seq=n/20;m.submit(vec![text_state(seq,&seq.to_string())]).unwrap();m.result(seq,"applied"); } }m.clock.advance(1000);m.state.tick();assert_eq!(m.status()["reason"],"expired");assert_ne!(m.status()["phase"],"ACTIVE");
    let m=Model::new();m.clock.advance(2999);m.state.heartbeat(OWNER,&m.cap,json!({"target_epoch":EPOCH})).unwrap();m.clock.advance(1);m.state.tick();assert_eq!(m.status()["reason"],"native_timeout");
    println!("Leases verified with fake clock: phone heartbeat=8000ms; idle=60000ms; absolute=300000ms (heartbeat does not extend)");
}
#[test] fn realtime_selection_only_does_not_extend_text_idle_lease() {
    let m=Model::new();m.submit(vec![text_state(1,"a")]).unwrap();m.result(1,"applied");
    for n in 1..=59 {m.clock.advance(1000);m.keepalive();if n%20==0 {let seq=n/20+1;let mut state=text_state(seq,"a");state["selection_start"]=json!(if n==20{0}else{1});state["selection_end"]=state["selection_start"].clone();m.submit(vec![state]).unwrap();m.result(seq,"applied");}}
    assert_eq!(m.status()["phase"],"ACTIVE");m.clock.advance(1000);m.state.tick();assert_eq!(m.status()["reason"],"idle_timeout");println!("Selection-only states at20s/40s and heartbeats every1s do not extend idle60000ms");
}
#[test] fn realtime_diagnostic_fields_do_not_change_dedup_identity() {
    let m=Model::new();let mut first=text_state(1,"a");first["input_kind"]=json!("insertText");first["client_elapsed_ms"]=json!(1);m.submit(vec![first.clone()]).unwrap();first["client_elapsed_ms"]=json!(999);m.submit(vec![first]).unwrap();assert_eq!(m.link.applies().len(),1);
}
#[test] fn realtime_capability_exact_credential_revocation_and_private_results() {
    let m=Model::new();assert_eq!(m.cap.len(),32);
    for cap in ["".to_owned(),"0".repeat(32),format!("{}x",m.cap),format!("x{}",&m.cap[1..])] { assert!(m.state.heartbeat(OWNER,&cap,json!({"target_epoch":EPOCH})).is_err()); }
    assert!(m.state.heartbeat("credential-b",&m.cap,json!({"target_epoch":EPOCH})).is_err());
    m.submit(vec![text_state(1,"PRIVATE_SENTINEL_6677")]).unwrap();m.result(1,"applied");let encoded=m.status().to_string();assert!(!encoded.contains("PRIVATE_SENTINEL_6677"));assert!(!encoded.contains(&m.cap));assert!(!encoded.contains("client_nonce"));
    m.state.credential_revoked("credential-b");assert_eq!(m.status()["phase"],"ACTIVE");m.state.credential_revoked(OWNER);assert_ne!(m.status()["phase"],"ACTIVE");
}
#[test] fn realtime_unknown_results_and_native_disconnect_never_reapply() {
    let m=Model::new();m.submit(vec![text_state(1,"a"),text_state(2,"ab")]).unwrap();m.result(1,"unknown");assert_eq!(m.status()["phase"],"UNKNOWN");assert_eq!(m.link.applies().len(),1);
    let m=Model::new();m.submit(vec![text_state(1,"a"),text_state(2,"ab")]).unwrap();m.state.native_disconnected();assert_ne!(m.status()["phase"],"ACTIVE");assert!(m.submit(vec![text_state(1,"a")]).is_err());assert_eq!(m.link.applies().len(),1);
}
#[test] fn realtime_request_claim_nonce_scope_and_grant_deadline() {
    let clock=Arc::new(TestClock::default());let state=RealtimeState::with_clock(clock.clone());let link=Arc::new(FakeNative::default());state.attach_native(link);
    let first=state.request(OWNER,"phone",json!({"client_nonce":NONCE})).unwrap();let id=first["request_id"].as_str().unwrap();assert_eq!(first["check_code"].as_str().unwrap().len(),2);
    let duplicate=state.request(OWNER,"phone",json!({"client_nonce":NONCE})).unwrap();assert_eq!(duplicate["request_id"],id);
    assert_eq!(state.claim(OWNER,json!({"request_id":id,"client_nonce":NONCE})).unwrap_err().code,"approval_pending");
    state.handle_native(native(json!({"type":"grant","request_id":id}))).unwrap();
    for (owner,nonce) in [("other",NONCE),(OWNER,"ffffffffffffffffffffffffffffffff")] {assert_eq!(state.claim(owner,json!({"request_id":id,"client_nonce":nonce})).unwrap_err().status,403);}
    assert!(state.handle_native(native(json!({"type":"grant","request_id":id}))).is_err());
    for _ in 0..30 {clock.advance(1000);state.handle_native(native(json!({"type":"heartbeat"}))).unwrap();}
    assert!(state.claim(OWNER,json!({"request_id":id,"client_nonce":NONCE})).is_err());
}
#[test] fn realtime_epoch_limit_boundary_and_capability_all_positions() {
    use crate::realtime::{TextState,validate_text,constant_time_capability_equal};
    assert!(validate_text(&serde_json::from_value::<TextState>(text_state(10_000,"x")).unwrap()).is_ok());
    assert!(validate_text(&serde_json::from_value::<TextState>(text_state(10_001,"x")).unwrap()).is_err());
    let good="0123456789abcdef0123456789abcdef";assert!(constant_time_capability_equal(good,good));
    for n in 0..32 {let mut altered=good.as_bytes().to_vec();altered[n]=b'x';assert!(!constant_time_capability_equal(good,std::str::from_utf8(&altered).unwrap()));}
    for n in [0,1,31,33,64] {assert!(!constant_time_capability_equal(good,&"0".repeat(n)));}
    println!("Epoch validation: seq10000 accepted / seq10001 rejected; capability comparison rejects all 32 mismatch positions and lengths 0/1/31/33/64 (machine-code constant timing not proven)");
}
#[test] fn realtime_event_ring_reset_and_result_proof_is_required() {
    let m=Model::new();m.submit(vec![text_state(1,"private ring text")]).unwrap();
    for proof in [None,Some("unverified")] {let msg=json!({"type":"result","request_id":m.id,"target_epoch":EPOCH,"seq":1,"status":"applied","proof":proof});assert!(m.state.handle_native(native(msg)).is_err());assert_eq!(m.status()["applied_seq"],0);}
    m.result(1,"applied");for seq in 2..=45 {m.submit(vec![text_state(seq,"x")]).unwrap();m.result(seq,"applied");}
    let events=m.state.events(OWNER,0);assert_eq!(events["reset_required"],true);assert!(events["events"].as_array().unwrap().is_empty());assert!(!events.to_string().contains("private ring text"));
    let cursor=m.status()["event_id"].as_u64().unwrap();assert!(m.state.events("other",cursor-2)["events"].as_array().unwrap().is_empty());
    println!("Event ring: 45 accepted/applied states overflow 128 events -> reset_required=true; no text/capability in replay");
}
#[test] fn realtime_late_result_and_reused_epoch_do_not_restore_authority() {
    let m=Model::new();m.submit(vec![text_state(1,"x")]).unwrap();m.state.release(OWNER,&m.cap,json!({"request_id":m.id})).unwrap();
    assert!(m.state.handle_native(native(json!({"type":"result","request_id":m.id,"target_epoch":EPOCH,"seq":1,"status":"applied","proof":"verified_readback"}))).is_err());assert_eq!(m.status()["applied_seq"],0);assert_eq!(m.status()["phase"],"REVOKED");
    let m=Model::new();assert!(m.state.handle_native(native(json!({"type":"target_bound","request_id":m.id,"target_epoch":EPOCH}))).is_err());
}

#[test] fn realtime_frames_complete_bytewise_and_multiple() {
    let mut bytes=Vec::new();native_bridge::write_frame(&mut bytes,&json!({"type":"heartbeat"})).unwrap();
    let mut parser=FrameDecoder::new();assert_eq!(parser.feed(&bytes).unwrap().len(),1);
    let mut parser=FrameDecoder::new();assert!(parser.feed(&bytes[..bytes.len()/2]).unwrap().is_empty());assert_eq!(parser.feed(&bytes[bytes.len()/2..]).unwrap().len(),1);
    let mut parser=FrameDecoder::new();let mut count=0;for byte in &bytes { count+=parser.feed(&[*byte]).unwrap().len(); }assert_eq!(count,1);
    let mut multiple=bytes.clone();multiple.extend_from_slice(&bytes);multiple.extend_from_slice(&bytes);assert_eq!(FrameDecoder::new().feed(&multiple).unwrap().len(),3);
    println!("Frame bytewise verified: {} separate one-byte feeds -> exactly 1 frame; concatenated frames -> 3",bytes.len());
}
#[test] fn realtime_frames_reject_zero_oversize_utf8_json_type_and_eof() {
    for body in [vec![],vec![0xff],b"not json".to_vec(),br#"{"type":"unknown"}"#.to_vec(),br#"{"type":"heartbeat","hwnd":123}"#.to_vec()] { let mut bytes=(body.len() as u32).to_le_bytes().to_vec();bytes.extend(body);assert!(FrameDecoder::new().feed(&bytes).is_err()); }
    assert!(FrameDecoder::new().feed(&65537u32.to_le_bytes()).is_err());
    assert!(FrameDecoder::new().finish().is_err());let mut parser=FrameDecoder::new();parser.feed(&[10,0]).unwrap();assert!(parser.finish().is_err());
    let mut parser=FrameDecoder::new();parser.feed(&[5,0,0,0,b'{']).unwrap();assert!(parser.finish().is_err());
    println!("Frame failures verified: 0/65537 lengths, UTF-8, JSON, unknown type/fields, EOF (boundary/header/body)");
}
#[test] fn realtime_frame_exact_64k_limit_and_failure_sticks() {
    let mut body=br#"{"type":"heartbeat"}"#.to_vec();body.resize(65536,b' ');let mut bytes=65536u32.to_le_bytes().to_vec();bytes.extend(body);assert_eq!(FrameDecoder::new().feed(&bytes).unwrap().len(),1);
    let mut parser=FrameDecoder::new();assert!(parser.feed(&0u32.to_le_bytes()).is_err());assert!(parser.feed(&bytes).is_err());
    assert!(native_bridge::write_frame(&mut Vec::new(),&"x".repeat(65536)).is_err());
    println!("Frame exact limit verified: payload65536 accepted, declared65537 rejected; parser stays failed after error");
}

struct Server { app:Arc<App>,port:u16,dir:PathBuf,accept:Option<JoinHandle<()>> }
impl Server {
    fn new()->Self { let listener=TcpListener::bind((Ipv4Addr::LOCALHOST,0)).unwrap();let port=listener.local_addr().unwrap().port();let dir=std::env::temp_dir().join(format!("fastinput-rt-{}-{port}",std::process::id()));fs::create_dir_all(&dir).unwrap();let app=Arc::new(App::new(dir.join("state.json"),Ipv4Addr::LOCALHOST,port).unwrap());App::start_realtime(&app);let worker=app.clone();let accept=Some(thread::spawn(move||crate::accept_connections(listener,worker)));Self{app,port,dir,accept} }
    fn connect(&self)->TcpStream { let s=TcpStream::connect((Ipv4Addr::LOCALHOST,self.port)).unwrap();s.set_read_timeout(Some(Duration::from_secs(3))).unwrap();s.set_write_timeout(Some(Duration::from_secs(3))).unwrap();s }
    fn headers(&self,method:&str,path:&str,token:Option<&str>,extra:&str)->String { format!("{method} {path} HTTP/1.1\r\nHost: 127.0.0.1:{}\r\n{}{}\r\n",self.port,token.map(|t|format!("Cookie: fi_session={t}\r\n")).unwrap_or_default(),extra) }
    fn post_headers(&self,path:&str,token:Option<&str>,cap:Option<&str>,len:usize)->String { self.headers("POST",path,token,&format!("Origin: http://127.0.0.1:{}\r\nContent-Type: application/json\r\nContent-Length: {len}\r\n{}",self.port,cap.map(|s|format!("X-FI-Realtime: {s}\r\n")).unwrap_or_default())) }
    fn raw(&self,headers:String,body:&[u8])->Response { let mut s=self.connect();s.write_all(headers.as_bytes()).unwrap();s.write_all(body).unwrap();let mut bytes=Vec::new();if let Err(e)=s.read_to_end(&mut bytes) { assert_eq!(e.kind(),io::ErrorKind::ConnectionReset); }Response::parse(&bytes) }
    fn post(&self,path:&str,token:&str,cap:Option<&str>,body:Value)->Response { let b=serde_json::to_vec(&body).unwrap();self.raw(self.post_headers(path,Some(token),cap,b.len()),&b) }
    fn get(&self,path:&str,token:&str)->Response { self.raw(self.headers("GET",path,Some(token),""),&[]) }
    fn phone(&self)->String { let desktop=self.app.desktop_token();let state=self.app.state(Some(&desktop),loopback());self.app.accept_pin(state["pairing"]["pin"].as_str().unwrap(),loopback()).unwrap() }
    fn active(&self,phone:&str)->(String,String,Arc<FakeNative>) {let link=Arc::new(FakeNative::default());self.app.realtime.attach_native(link.clone());let r=self.post("/api/realtime/request",phone,None,json!({"client_nonce":NONCE}));assert_eq!(r.status,200,"{}",r.json());let id=r.json()["request_id"].as_str().unwrap().to_owned();self.app.realtime.handle_native(native(json!({"type":"grant","request_id":id}))).unwrap();let claim=self.post("/api/realtime/claim",phone,None,json!({"request_id":id,"client_nonce":NONCE}));assert_eq!(claim.status,200,"{}",claim.json());let cap=claim.json()["capability"].as_str().unwrap().to_owned();self.app.realtime.handle_native(native(json!({"type":"target_bound","request_id":id,"target_epoch":EPOCH}))).unwrap();assert_eq!(self.post("/api/realtime/heartbeat",phone,Some(&cap),json!({"target_epoch":EPOCH,"confirm_empty":true})).status,200);self.app.realtime.handle_native(native(json!({"type":"target_bound","request_id":id,"target_epoch":EPOCH,"phase":"active"}))).unwrap();(id,cap,link)}
    fn states(&self,phone:&str,cap:&str,id:&str,states:Vec<Value>)->Response {self.post("/api/realtime/states",phone,Some(cap),json!({"request_id":id,"target_epoch":EPOCH,"states":states}))}
    fn parts(&self)->usize { fs::read_dir(self.dir.join("files")).unwrap().filter_map(Result::ok).filter(|e|e.path().extension().is_some_and(|s|s=="part")).count() }
}
fn loopback()->IpAddr { IpAddr::V4(Ipv4Addr::LOCALHOST) }
impl Drop for Server {fn drop(&mut self){self.app.request_shutdown(Some(&self.app.desktop_token()),loopback());self.accept.take().unwrap().join().unwrap();let start=Instant::now();while self.parts()!=0&&start.elapsed()<Duration::from_secs(3){thread::sleep(Duration::from_millis(10));}fs::remove_dir_all(&self.dir).unwrap();}}
struct Response{status:u16,headers:String,body:Vec<u8>}
impl Response {fn parse(bytes:&[u8])->Self{let end=bytes.windows(4).position(|s|s==b"\r\n\r\n").expect("complete HTTP headers");let headers=std::str::from_utf8(&bytes[..end]).unwrap().to_owned();let status=headers.split_whitespace().nth(1).unwrap().parse().unwrap();Self{status,headers,body:bytes[end+4..].to_vec()}}fn json(&self)->Value{serde_json::from_slice(&self.body).unwrap()} }

#[test] fn realtime_http_rejects_before_reading_body() {
    let server=Server::new();let phone=server.phone();
    for path in ["request","claim","states","heartbeat","release","enter"] {let response=server.raw(server.post_headers(&format!("/api/realtime/{path}"),None,None,1024),&[]);assert_eq!(response.status,401);}
    let h=server.post_headers("/api/realtime/request",Some(&phone),None,1024);assert_eq!(server.raw(h.replace(&format!("Origin: http://127.0.0.1:{}",server.port),"Origin: http://evil.test"),&[]).status,403);
    assert_eq!(server.raw(h.replace("application/json","text/plain"),&[]).status,403);
    let (id,cap,_)=server.active(&phone);let missing=server.raw(server.post_headers("/api/realtime/states",Some(&phone),None,1024),&[]);assert_eq!(missing.status,403,"{}",missing.json());
    assert_eq!(server.raw(server.post_headers("/api/realtime/states",Some(&phone),Some("wrong"),1024),&[]).status,403);
    assert_eq!(server.raw(server.post_headers("/api/realtime/states",Some(&phone),Some(&cap),65537),&[]).status,413);
    assert_eq!(server.get("/api/realtime/status",&phone).json()["request_id"],id);
}
#[test] fn realtime_http_flow_retransmission_gap_release_and_sse() {
    let server=Server::new();let phone=server.phone();let (id,cap,link)=server.active(&phone);
    let mut event_socket=server.connect();event_socket.write_all(server.headers("GET","/api/events",Some(&phone),"").as_bytes()).unwrap();let mut reader=BufReader::new(event_socket);
    let mut initial=String::new();loop{let mut line=String::new();reader.read_line(&mut line).unwrap();initial.push_str(&line);if let Some(data)=line.strip_prefix("data:"){let snapshot:Value=serde_json::from_str(data.trim()).unwrap();assert!(snapshot.get("text").is_some());assert!(snapshot.get("revision").is_some());assert!(snapshot.get("generation").is_some());break;}}assert!(initial.contains("data:"));
    let first=server.states(&phone,&cap,&id,vec![text_state(1,"G"),text_state(2,"Go")]);assert_eq!(first.status,200,"{}",first.json());assert_eq!(first.json()["accepted_seq"],2);assert_eq!(link.applies().len(),1);
    assert_eq!(server.states(&phone,&cap,&id,vec![text_state(1,"G")]).status,200);assert_eq!(link.applies().len(),1);
    let gap=server.states(&phone,&cap,&id,vec![text_state(4,"Google")]);assert_eq!(gap.status,409);assert_eq!(gap.json()["expected_seq"],3);
    server.app.realtime.handle_native(native(json!({"type":"result","request_id":id,"target_epoch":EPOCH,"seq":1,"status":"applied","proof":"verified_readback"}))).unwrap();
    let mut event=String::new();let mut named=false;loop {let mut line=String::new();reader.read_line(&mut line).unwrap();assert!(!line.is_empty());event.push_str(&line);if line.starts_with("event:"){named=line.trim()=="event: realtime";}if named {if let Some(data)=line.strip_prefix("data:"){let value:Value=serde_json::from_str(data.trim()).unwrap();if value["applied_seq"]==1 {assert!(value["event_id"].as_u64().is_some());break;}}}}assert!(!event.contains("capability"));
    assert_eq!(server.post("/api/realtime/release",&phone,Some(&cap),json!({"request_id":id})).status,200);assert_ne!(server.get("/api/realtime/status",&phone).json()["phase"],"ACTIVE");
    drop(reader);
}
#[test] fn realtime_http_same_device_different_tokens_and_pairing_revoke() {
    let server=Server::new();let first=server.phone();let second=server.phone();assert_ne!(first,second);let (_id,cap,_)=server.active(&first);
    let first_credential=server.app.credential(Some(&first),loopback()).unwrap().0;let second_credential=server.app.credential(Some(&second),loopback()).unwrap().0;assert_ne!(first_credential,second_credential);assert_ne!(first_credential,first);assert_eq!(first_credential.len(),64);
    assert_eq!(server.raw(server.post_headers("/api/realtime/states",Some(&second),Some(&cap),1024),&[]).status,403);
    let desktop=server.app.desktop_token();let device=server.app.state(Some(&desktop),loopback())["devices"][0]["id"].as_str().unwrap().to_owned();assert!(server.app.revoke(Some(&desktop),loopback(),&device).unwrap());
    assert_eq!(server.raw(server.post_headers("/api/realtime/states",Some(&first),Some(&cap),1024),&[]).status,401);
    assert_ne!(server.app.realtime.status(&first_credential).unwrap()["phase"],"ACTIVE");
}
#[test] fn realtime_http_native_disconnect_revokes_inflight_and_queued() {
    let server=Server::new();let phone=server.phone();let (id,cap,link)=server.active(&phone);assert_eq!(server.states(&phone,&cap,&id,vec![text_state(1,"a"),text_state(2,"ab")]).status,200);server.app.realtime.native_disconnected();
    let status=server.get("/api/realtime/status",&phone).json();assert_eq!(status["phase"],"REVOKED");assert_eq!(status["applied_seq"],0);assert_eq!(link.applies().len(),1);assert_ne!(server.raw(server.post_headers("/api/realtime/states",Some(&phone),Some(&cap),1024),&[]).status,200);
}
#[test] fn realtime_http_slow_body_cannot_outlive_pairing_revocation() {
    let server=Server::new();let phone=server.phone();let link=Arc::new(FakeNative::default());server.app.realtime.attach_native(link.clone());let body=serde_json::to_vec(&json!({"client_nonce":NONCE})).unwrap();let mut socket=server.connect();socket.write_all(server.post_headers("/api/realtime/request",Some(&phone),None,body.len()).as_bytes()).unwrap();let half=body.len()/2;socket.write_all(&body[..half]).unwrap();socket.set_read_timeout(Some(Duration::from_millis(100))).unwrap();let mut byte=[0];assert!(matches!(socket.read(&mut byte).unwrap_err().kind(),io::ErrorKind::WouldBlock|io::ErrorKind::TimedOut));
    let desktop=server.app.desktop_token();let id=server.app.state(Some(&desktop),loopback())["devices"][0]["id"].as_str().unwrap().to_owned();server.app.revoke(Some(&desktop),loopback(),&id).unwrap();socket.write_all(&body[half..]).unwrap();socket.set_read_timeout(Some(Duration::from_secs(3))).unwrap();let mut bytes=Vec::new();socket.read_to_end(&mut bytes).unwrap();assert_eq!(Response::parse(&bytes).status,401);assert!(link.0.lock().unwrap().is_empty());println!("Slow body TOCTOU: authorized headers + half body, revoke credential, finish body ->401; native commands=0");
}
#[test] fn realtime_trimmed_old_token_revokes_only_its_authority() {
    let server=Server::new();let first=server.phone();let credential=server.app.credential(Some(&first),loopback()).unwrap().0;let (_id,_cap,_link)=server.active(&first);let survivor=server.phone();for _ in 0..63 {server.phone();}
    assert!(server.app.credential(Some(&first),loopback()).is_none());assert!(server.app.credential(Some(&survivor),loopback()).is_some());let status=server.app.realtime.status(&credential).unwrap();assert_eq!(status["phase"],"REVOKED");assert_eq!(status["reason"],"credential_revoked");println!("Credential history: 65th pairing retires first token, retains second token, immediately revokes first token's realtime lease");
}
#[test] fn realtime_sse_and_status_do_not_expose_another_credential() {
    let server=Server::new();let owner=server.phone();let other=server.phone();let (id,cap,_)=server.active(&owner);let status=server.get("/api/realtime/status",&other).json();assert_eq!(status["phase"],"DISABLED");assert!(status.get("request_id").is_none());let mut socket=server.connect();socket.write_all(server.headers("GET","/api/events",Some(&other),"").as_bytes()).unwrap();socket.set_read_timeout(Some(Duration::from_millis(150))).unwrap();let mut reader=BufReader::new(socket);loop{let mut line=String::new();reader.read_line(&mut line).unwrap();if line.starts_with("data:"){break;}}
    assert_eq!(server.states(&owner,&cap,&id,vec![text_state(1,"secretOwnerText")]).status,200);let mut received=String::new();loop{let mut line=String::new();match reader.read_line(&mut line){Ok(0)=>break,Ok(_)=>received.push_str(&line),Err(e)if matches!(e.kind(),io::ErrorKind::WouldBlock|io::ErrorKind::TimedOut)=>break,Err(e)=>panic!("SSE read: {e}")}}
    assert!(!received.contains("event: realtime"));assert!(!received.contains(&id));assert!(!received.contains("secretOwnerText"));assert!(!received.contains(&cap));
}
#[test] fn realtime_four_uploads_keep_state_and_text_responsive() {
    const SIZE:u64=32*1024*1024;let server=Server::new();let phone=server.phone();let (id,cap,_)=server.active(&phone);let mut uploads=Vec::new();let streaming=Arc::new(std::sync::atomic::AtomicBool::new(false));let byte_count=Arc::new(AtomicU64::new(4096));
    for n in 0..4 {let mut socket=server.connect();socket.write_all(server.headers("POST",&format!("/api/files?name=parallel{n}.bin"),Some(&phone),&format!("Origin: http://127.0.0.1:{}\r\nContent-Type: application/octet-stream\r\nContent-Length: {SIZE}\r\n",server.port)).as_bytes()).unwrap();socket.write_all(&[0u8;1024]).unwrap();let streaming=streaming.clone();let counter=byte_count.clone();uploads.push(thread::spawn(move||{while !streaming.load(Ordering::SeqCst){thread::sleep(Duration::from_millis(1));}let chunk=[n as u8;65536];let mut remaining=SIZE-1024;while remaining>0{let count=remaining.min(chunk.len() as u64) as usize;socket.write_all(&chunk[..count]).unwrap();counter.fetch_add(count as u64,Ordering::SeqCst);remaining-=count as u64;thread::sleep(Duration::from_millis(8));}let mut bytes=Vec::new();socket.read_to_end(&mut bytes).unwrap();let response=Response::parse(&bytes);assert_eq!(response.status,200);assert_eq!(response.json()["file"]["size"],SIZE);}));}
    let start=Instant::now();while server.parts()!=4 {assert!(start.elapsed()<Duration::from_secs(3));thread::sleep(Duration::from_millis(5));}
    streaming.store(true,Ordering::SeqCst);let initial_bytes=byte_count.load(Ordering::SeqCst);
    let mut rt_ms=Vec::new();let mut text_ms=Vec::new();let desktop=server.app.desktop_token();let mut shared=server.get("/api/state",&desktop).json();
    for seq in 1..=20 {let start=Instant::now();let response=server.states(&phone,&cap,&id,vec![text_state(seq,&format!("s{seq}"))]);assert_eq!(response.status,200,"{}",response.json());server.app.realtime.handle_native(native(json!({"type":"result","request_id":id,"target_epoch":EPOCH,"seq":seq,"status":"applied","proof":"verified_readback"}))).unwrap();rt_ms.push(start.elapsed().as_secs_f64()*1000.0);
        let start=Instant::now();let response=server.post("/api/text",&phone,None,json!({"text":format!("note {seq}"),"revision":shared["revision"],"generation":shared["generation"]}));assert_eq!(response.status,200);shared=response.json();text_ms.push(start.elapsed().as_secs_f64()*1000.0);
        assert_eq!(server.get("/api/realtime/status",&phone).status,200);assert_eq!(server.parts(),4);assert!(byte_count.load(Ordering::SeqCst)>initial_bytes,"uploads must be streaming during measurements");
        assert_eq!(server.post("/api/realtime/heartbeat",&phone,Some(&cap),json!({"target_epoch":EPOCH})).status,200);
    }
    rt_ms.sort_by(f64::total_cmp);text_ms.sort_by(f64::total_cmp);println!("FOUR_UPLOAD_LATENCY samples=20 streaming4x32MiB bytes_during_measurement={} realtime HTTP->fake readback p50={:.3}ms p95={:.3}ms max={:.3}ms; /api/text p50={:.3}ms p95={:.3}ms max={:.3}ms",byte_count.load(Ordering::SeqCst)-initial_bytes,rt_ms[9],rt_ms[18],rt_ms[19],text_ms[9],text_ms[18],text_ms[19]);assert!(rt_ms[19]<1000.0);assert!(text_ms[19]<1000.0);
    for upload in uploads {upload.join().unwrap();}assert_eq!(byte_count.load(Ordering::SeqCst),4*SIZE);assert_eq!(server.get("/api/files",&phone).json()["files"].as_array().unwrap().len(),4);
}

const ENTER_EPOCH:&str="enter-epoch";
const ENTER_ID:&str="abcdef0123456789abcdef0123456789";
fn enable_enter(state:&RealtimeState,id:&str,cap:&str,owner:&str) {
    state.handle_native(native(json!({"type":"capabilities","virtual_enter":true}))).unwrap();
    state.handle_native(native(json!({"type":"target_bound","request_id":id,"target_epoch":ENTER_EPOCH,"app_label":"記事本 Editor_1.0","enter_allowed":true}))).unwrap();
    state.heartbeat(owner,cap,json!({"target_epoch":ENTER_EPOCH,"confirm_empty":true})).unwrap();
    state.handle_native(native(json!({"type":"target_bound","request_id":id,"target_epoch":ENTER_EPOCH,"phase":"active","app_label":"記事本 Editor_1.0","enter_allowed":true}))).unwrap();
}
fn enter_body(id:&str)->Value {json!({"v":1,"request_id":id,"target_epoch":ENTER_EPOCH,"enter_id":ENTER_ID,"after_seq":0,"hold_ms":600})}
fn enter_commands(link:&FakeNative)->Vec<Value> {link.0.lock().unwrap().iter().filter(|v|v["type"]=="press_enter").cloned().collect()}
fn enter_result(m:&Model,id:&str,status:&str)->Result<(),crate::realtime::RealtimeError> {
    m.state.handle_native(native(json!({"type":"enter_result","request_id":m.id,"target_epoch":ENTER_EPOCH,"enter_id":id,"status":status,"reason":null})))
}
#[test] fn realtime_enter_defaults_disabled_and_capabilities_are_not_target_permission() {
    let m=Model::new();assert_eq!(m.status()["enter_available"],false);assert!(m.status()["enter"].is_null());
    let mut body=enter_body(&m.id);body["target_epoch"]=json!(EPOCH);
    assert_eq!(m.state.enter(OWNER,&m.cap,body.clone()).unwrap_err().code,"enter_disabled");
    m.state.handle_native(native(json!({"type":"capabilities","virtual_enter":true}))).unwrap();
    assert_eq!(m.status()["enter_available"],false);
    assert_eq!(m.state.enter(OWNER,&m.cap,body).unwrap_err().code,"enter_disabled");assert!(enter_commands(&m.link).is_empty());
    enable_enter(&m.state,&m.id,&m.cap,OWNER);assert_eq!(m.status()["enter_available"],true);
    m.state.handle_native(native(json!({"type":"capabilities","virtual_enter":false}))).unwrap();
    assert_eq!(m.state.enter(OWNER,&m.cap,enter_body(&m.id)).unwrap_err().code,"enter_disabled");
}
#[test] fn realtime_enter_hold_599_600_and_strict_fields() {
    let m=Model::new();enable_enter(&m.state,&m.id,&m.cap,OWNER);
    for field in ["v","request_id","target_epoch","enter_id","after_seq","hold_ms"] {let mut b=enter_body(&m.id);b.as_object_mut().unwrap().remove(field);assert_eq!(m.state.enter(OWNER,&m.cap,b).unwrap_err().status,400);}
    for (field,value) in [("key",json!(13)),("hold_ms",json!(-1)),("enter_id",json!("bad")),("v",json!(2))] {let mut b=enter_body(&m.id);b[field]=value;assert!(m.state.enter(OWNER,&m.cap,b).is_err());}
    let mut b=enter_body(&m.id);b["hold_ms"]=json!(599);let e=m.state.enter(OWNER,&m.cap,b).unwrap_err();assert_eq!((e.status,e.code),(422,"invalid_hold"));
    assert!(enter_commands(&m.link).is_empty());let result=m.state.enter(OWNER,&m.cap,enter_body(&m.id)).unwrap();assert_eq!(result["enter"]["status"],"requested");assert_eq!(enter_commands(&m.link).len(),1);
    let command=&enter_commands(&m.link)[0];assert_eq!(command.as_object().unwrap().len(),5);assert_eq!(command["after_seq"],0);
}
#[test] fn realtime_enter_text_pending_checks_precede_hold_and_only_one_inflight() {
    let m=Model::new();enable_enter(&m.state,&m.id,&m.cap,OWNER);
    let text=json!({"request_id":m.id,"target_epoch":ENTER_EPOCH,"states":[text_state(1,"secret") ]});
    m.state.states(OWNER,&m.cap,text.clone()).unwrap();
    let mut b=enter_body(&m.id);b["hold_ms"]=json!(599);assert_eq!(m.state.enter(OWNER,&m.cap,b.clone()).unwrap_err().code,"text_pending");
    m.state.handle_native(native(json!({"type":"result","request_id":m.id,"target_epoch":ENTER_EPOCH,"seq":1,"status":"applied","proof":"verified_readback"}))).unwrap();
    assert_eq!(m.state.enter(OWNER,&m.cap,b.clone()).unwrap_err().code,"text_pending");
    b["after_seq"]=json!(1);b["hold_ms"]=json!(600);m.state.enter(OWNER,&m.cap,b.clone()).unwrap();
    b["enter_id"]=json!("11111111111111111111111111111111");assert_eq!(m.state.enter(OWNER,&m.cap,b).unwrap_err().code,"busy");
    assert_eq!(m.state.states(OWNER,&m.cap,text).unwrap_err().code,"enter_busy");assert_eq!(enter_commands(&m.link).len(),1);assert_eq!(m.link.applies().len(),1);
}
#[test] fn realtime_enter_idempotency_conflict_and_all_results_pause_without_replay() {
    for status in ["applied","rejected","unknown"] {
        let m=Model::new();enable_enter(&m.state,&m.id,&m.cap,OWNER);let body=enter_body(&m.id);
        m.state.enter(OWNER,&m.cap,body.clone()).unwrap();m.state.enter(OWNER,&m.cap,body.clone()).unwrap();
        let mut conflict=body.clone();conflict["hold_ms"]=json!(601);assert_eq!(m.state.enter(OWNER,&m.cap,conflict.clone()).unwrap_err().code,"sequence_conflict");
        assert_eq!(enter_result(&m,"00000000000000000000000000000000",status).unwrap_err().code,"stale_result");assert_eq!(m.status()["enter"]["status"],"requested");
        enter_result(&m,ENTER_ID,status).unwrap();let snapshot=m.status();assert_eq!(snapshot["phase"],"PAUSED");assert_eq!(snapshot["reason"],"enter_sent");assert_eq!(snapshot["enter"]["status"],status);assert_eq!(snapshot["enter_available"],false);
        assert_eq!(m.state.enter(OWNER,&m.cap,body).unwrap()["enter"]["status"],status);
        assert_eq!(m.state.enter(OWNER,&m.cap,conflict).unwrap_err().code,"sequence_conflict");
        assert_eq!(enter_result(&m,ENTER_ID,status).unwrap_err().code,"stale_result");assert!(m.submit(vec![text_state(1,"later")]).is_err());assert_eq!(enter_commands(&m.link).len(),1);assert!(m.link.applies().is_empty());
    }
}
#[test] fn realtime_enter_timeout_disable_and_revocation_close_command_gate() {
    for action in ["timeout","disable","revoke"] {
        let m=Model::new();enable_enter(&m.state,&m.id,&m.cap,OWNER);m.state.enter(OWNER,&m.cap,enter_body(&m.id)).unwrap();
        let command:NativeCommand=NativeCommand::PressEnter{request_id:m.id.clone(),target_epoch:ENTER_EPOCH.into(),enter_id:ENTER_ID.into(),after_seq:0};assert!(m.state.native_command_allowed(&command));
        match action {"timeout"=>{m.clock.advance(1499);assert_eq!(m.status()["enter"]["status"],"requested");m.clock.advance(1);m.state.tick();},"disable"=>{m.state.handle_native(native(json!({"type":"capabilities","virtual_enter":false}))).unwrap();},_=>m.state.credential_revoked(OWNER)}
        assert!(!m.state.native_command_allowed(&command));assert_eq!(m.status()["enter"]["status"],"unknown");assert_eq!(m.status()["phase"],if action=="revoke"{"REVOKED"}else{"PAUSED"});
        assert!(enter_result(&m,ENTER_ID,"applied").is_err());assert_eq!(enter_commands(&m.link).len(),1);
    }
}
#[test] fn realtime_enter_snapshots_and_events_do_not_leak_other_credentials() {
    let m=Model::new();enable_enter(&m.state,&m.id,&m.cap,OWNER);m.state.enter(OWNER,&m.cap,enter_body(&m.id)).unwrap();
    let other=m.state.status("credential-b").unwrap();assert_eq!(other["enter_available"],false);assert!(other["target_app"].is_null());assert!(other["enter"].is_null());assert!(!other.to_string().contains(ENTER_ID));
    assert!(m.state.events("credential-b",0)["events"].as_array().unwrap().is_empty());
    assert_eq!(m.status()["target_app"],"記事本 Editor_1.0");assert_eq!(m.status()["enter"]["enter_id"],ENTER_ID);
    assert_eq!(m.state.enter("credential-b",&m.cap,enter_body(&m.id)).unwrap_err().status,403);
    let all=m.state.events(OWNER,0).to_string();assert!(!all.contains(&m.cap));assert!(!all.contains("hold_ms"));
}
#[test] fn realtime_enter_native_frames_reject_malicious_labels_and_fields() {
    for label in ["<script>","C:\\app","https://x","title\nsecret","\u{202e}app","😀","é",&"a".repeat(33)] {
        let b=json!({"type":"target_bound","request_id":"r","target_epoch":"e","app_label":label,"enter_allowed":true});
        let mut bytes=Vec::new();native_bridge::write_frame(&mut bytes,&b).unwrap();let mut decoder=FrameDecoder::new();assert!(decoder.feed(&bytes).is_err(),"{label:?}");assert!(decoder.feed(&[1]).is_err());
    }
    for label in ["A-b_c.9","記事本","メモ帳","한글",&"中".repeat(32)] {assert!(serde_json::from_value::<NativeMessage>(json!({"type":"target_bound","request_id":"r","target_epoch":"e","app_label":label})).is_ok());}
    for b in [json!({"type":"capabilities","virtual_enter":"true"}),json!({"type":"capabilities","virtual_enter":true,"key":13}),json!({"type":"target_bound","request_id":"r","target_epoch":"e","enter_allowed":null}),json!({"type":"enter_result","request_id":"r","target_epoch":"e","enter_id":ENTER_ID,"status":"applied","proof":"verified_readback"})] {assert!(serde_json::from_value::<NativeMessage>(b).is_err());}
    let m=Model::new();assert_eq!(m.state.handle_native(NativeMessage::TargetBound{request_id:m.id.clone(),target_epoch:"bad-label".into(),phase:None,app_label:Some("<bad>".into()),enter_allowed:true}).unwrap_err().status,400);assert_eq!(m.status()["phase"],"REVOKED");
}
#[test] fn realtime_enter_http_security_before_body_and_slow_revocation_race() {
    let server=Server::new();let phone=server.phone();let (id,cap,link)=server.active(&phone);let credential=server.app.credential(Some(&phone),loopback()).unwrap().0;
    enable_enter(&server.app.realtime,&id,&cap,&credential);
    let headers=server.post_headers("/api/realtime/enter",Some(&phone),Some(&cap),2048);
    for bad in [headers.replace("application/json","text/plain"),headers.replace(&format!("Origin: http://127.0.0.1:{}",server.port),"Origin: http://evil.test"),headers.replace(&format!("Host: 127.0.0.1:{}",server.port),"Host: evil.test"),headers.replace(&cap,"wrong")] {assert_eq!(server.raw(bad,&[]).status,403);}
    assert_eq!(server.raw(server.post_headers("/api/realtime/enter",Some(&phone),None,2048),&[]).status,403);
    assert_eq!(server.raw(server.post_headers("/api/realtime/enter",Some(&phone),Some(&cap),65537),&[]).status,413);
    let bytes=serde_json::to_vec(&enter_body(&id)).unwrap();let mut socket=server.connect();socket.write_all(server.post_headers("/api/realtime/enter",Some(&phone),Some(&cap),bytes.len()).as_bytes()).unwrap();let half=bytes.len()/2;socket.write_all(&bytes[..half]).unwrap();socket.set_read_timeout(Some(Duration::from_millis(100))).unwrap();assert!(socket.read(&mut [0]).is_err());
    let desktop=server.app.desktop_token();let device=server.app.state(Some(&desktop),loopback())["devices"][0]["id"].as_str().unwrap().to_owned();server.app.revoke(Some(&desktop),loopback(),&device).unwrap();socket.write_all(&bytes[half..]).unwrap();socket.set_read_timeout(Some(Duration::from_secs(3))).unwrap();let mut received=Vec::new();socket.read_to_end(&mut received).unwrap();assert_eq!(Response::parse(&received).status,401);assert!(enter_commands(&link).is_empty());
}
#[test] fn realtime_enter_http_idempotent_flow_and_pause() {
    let server=Server::new();let phone=server.phone();let (id,cap,link)=server.active(&phone);let credential=server.app.credential(Some(&phone),loopback()).unwrap().0;enable_enter(&server.app.realtime,&id,&cap,&credential);
    let body=enter_body(&id);for _ in 0..2 {let response=server.post("/api/realtime/enter",&phone,Some(&cap),body.clone());assert_eq!(response.status,200,"{}",response.json());assert_eq!(response.json()["enter"]["status"],"requested");}
    server.app.realtime.handle_native(native(json!({"type":"enter_result","request_id":id,"target_epoch":ENTER_EPOCH,"enter_id":ENTER_ID,"status":"applied","reason":null}))).unwrap();
    let response=server.post("/api/realtime/enter",&phone,Some(&cap),body);assert_eq!(response.status,200);assert_eq!(response.json()["phase"],"PAUSED");assert_eq!(response.json()["enter"]["status"],"applied");assert_eq!(enter_commands(&link).len(),1);
}

fn executable()->PathBuf {
    let name=if cfg!(windows){"fastinput.exe"}else{"fastinput"};
    let path=std::env::current_exe().unwrap().parent().unwrap().parent().unwrap().join(name);
    if path.exists(){return path;}
    // 修正：測試程式旁沒有已建置的主程式時（例如官方腳本的 `cargo test --release --target …`），
    // 改建到獨立的暫存目錄（避免與外層 cargo 的 build lock 衝突），沿用外層的 CARGO 與工具鏈環境變數。
    let dir=std::env::temp_dir().join(format!("fastinput-realtime-bin-{}",std::process::id()));
    let cargo=std::env::var_os("CARGO").unwrap_or_else(||"cargo".into());
    let status=Command::new(cargo).args(["build","--offline","--manifest-path",concat!(env!("CARGO_MANIFEST_DIR"),"/Cargo.toml"),"--target-dir"]).arg(&dir).status().unwrap();
    assert!(status.success());
    dir.join("debug").join(name)
}
fn child_frame(reader:&mut impl Read)->Value {let mut size=[0;4];reader.read_exact(&mut size).unwrap();let size=u32::from_le_bytes(size) as usize;assert!((1..=65536).contains(&size));let mut bytes=vec![0;size];reader.read_exact(&mut bytes).unwrap();serde_json::from_slice(&bytes).unwrap()}
fn child_http(port:u16,method:&str,path:&str,token:&str,body:Value)->Response {let mut socket=TcpStream::connect((Ipv4Addr::LOCALHOST,port)).unwrap();socket.set_read_timeout(Some(Duration::from_secs(3))).unwrap();let body=serde_json::to_vec(&body).unwrap();write!(socket,"{method} {path} HTTP/1.1\r\nHost: 127.0.0.1:{port}\r\nCookie: fi_session={token}\r\nOrigin: http://127.0.0.1:{port}\r\nContent-Type: application/json\r\nContent-Length: {}\r\n\r\n",body.len()).unwrap();socket.write_all(&body).unwrap();let mut bytes=Vec::new();socket.read_to_end(&mut bytes).unwrap();Response::parse(&bytes)}
#[test] fn realtime_native_stdio_child_hello_eof_and_legacy_output() {
    let exe=executable();let listener=TcpListener::bind((Ipv4Addr::LOCALHOST,0)).unwrap();let port=listener.local_addr().unwrap().port();drop(listener);let dir=std::env::temp_dir().join(format!("fastinput-child-{}-{port}",std::process::id()));
    let mut child=Command::new(&exe).args(["--native-stdio","--no-browser","--lan-ip","127.0.0.1","--port",&port.to_string()]).env("FASTINPUT_DATA_DIR",&dir).stdin(Stdio::piped()).stdout(Stdio::piped()).stderr(Stdio::null()).spawn().unwrap();let hello=child_frame(child.stdout.as_mut().unwrap());assert_eq!(hello["type"],"hello");assert!(hello["server_boot_id"].as_str().is_some());assert!(hello["desktop_url"].as_str().is_some_and(|u|u.contains("/desktop?key=")));
    let desktop=hello["desktop_url"].as_str().unwrap().split("key=").nth(1).unwrap();let initial=child_http(port,"GET","/api/state",desktop,json!({}));let pin=initial.json()["pairing"]["pin"].as_str().unwrap().to_owned();let paired=child_http(port,"POST","/api/pairing/pin",desktop,json!({"pin":pin}));assert_eq!(paired.status,200);let phone=paired.headers.lines().find_map(|line|line.strip_prefix("Set-Cookie: fi_session=")).unwrap().split(';').next().unwrap();
    let request=child_http(port,"POST","/api/realtime/request",phone,json!({"client_nonce":NONCE}));assert_eq!(request.status,200);let id=request.json()["request_id"].as_str().unwrap().to_owned();let notice=child_frame(child.stdout.as_mut().unwrap());assert_eq!(notice["type"],"request");assert_eq!(notice["request_id"],id);native_bridge::write_frame(child.stdin.as_mut().unwrap(),&json!({"type":"grant","request_id":id})).unwrap();
    let start=Instant::now();loop {let claim=child_http(port,"POST","/api/realtime/claim",phone,json!({"request_id":id,"client_nonce":NONCE}));if claim.status==200 {assert_eq!(claim.json()["phase"],"ARMED");assert_eq!(claim.json()["capability"].as_str().unwrap().len(),32);break;}assert_eq!(claim.status,409);assert!(start.elapsed()<Duration::from_secs(1));thread::sleep(Duration::from_millis(10));}
    drop(child.stdin.take());let start=Instant::now();loop {if let Some(status)=child.try_wait().unwrap(){assert!(status.success());break;}assert!(start.elapsed()<Duration::from_secs(3),"stdio EOF must terminate backend");thread::sleep(Duration::from_millis(10));}println!("STDIO child: first frame hello; request/grant/claim reached ARMED; stdin EOF -> process exit in {:.3}ms",start.elapsed().as_secs_f64()*1000.0);
    let mut child=Command::new(&exe).args(["--no-browser","--lan-ip","127.0.0.1","--port",&port.to_string()]).env("FASTINPUT_DATA_DIR",&dir).stdin(Stdio::null()).stdout(Stdio::piped()).stderr(Stdio::null()).spawn().unwrap();let mut reader=BufReader::new(child.stdout.take().unwrap());let mut line=String::new();reader.read_line(&mut line).unwrap();assert_eq!(line.trim(),"FastInput is running.");line.clear();reader.read_line(&mut line).unwrap();assert!(line.starts_with("Computer: http://127.0.0.1:"));line.clear();reader.read_line(&mut line).unwrap();assert_eq!(line.trim(),"Phone: scan the QR code shown on the computer (same LAN).");child.kill().unwrap();child.wait().unwrap();fs::remove_dir_all(dir).unwrap();
}
