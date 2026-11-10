using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;

namespace ProbeLab;
public sealed class OwnedTarget
{
    readonly Process process; readonly Report report;
    readonly bool browser;
    public OwnedTarget(Process process,Report report,bool browser=false) {this.process=process;this.report=report;this.browser=browser;}
    public bool Guard(nint hwnd,string action)
    {
        Native.GetWindowThreadProcessId(hwnd,out var pid);
        var foreground=Native.GetForegroundWindow();Native.GetWindowThreadProcessId(foreground,out var fgpid);
        bool ok=!process.HasExited && Native.IsWindow(hwnd) && pid==process.Id && fgpid==process.Id && (!browser || NetworkSafety.IsOffline());
        if(!ok) report.Add("安全拒絕 "+action,new{hwnd=(long)hwnd,pid,foreground=(long)foreground,fgpid,ownedPid=process.Id},null,"目標與前景 PID 均須等於本工具直接啟動 PID");
        return ok;
    }
    public bool Activate(nint h)
    {
        Native.GetWindowThreadProcessId(h,out var pid);
        if(process.HasExited || pid!=process.Id) return false;
        bool result=Native.SetForegroundWindow(h);Thread.Sleep(120);
        bool allowed=Guard(h,"foreground");report.Add("SetForegroundWindow",new{result,allowed,hwnd=(long)h},allowed,"前景PID須為自啟fixture；失敗時依賴此焦點的步驟不執行");return allowed;
    }
    public uint Input(nint hwnd,Native.INPUT[] inputs,string label)
    {
        if(!Guard(hwnd,label)) return 0;
        uint result=Native.SendInput((uint)inputs.Length,inputs,Marshal.SizeOf<Native.INPUT>());
        report.Add(label,new{requested=inputs.Length,returned=result,error=Marshal.GetLastWin32Error(),marker=Native.Marker},result==inputs.Length,"SendInput 回傳等於送入事件數；實際落點另以fixture核對");return result;
    }
    public bool Message(nint hwnd,uint msg,nint wp,nint lp,uint timeout=500)
    {
        if(!Guard(hwnd,$"message 0x{msg:X}")) return false;
        var start=Stopwatch.GetTimestamp();nint ok=Native.SendMessageTimeout(hwnd,msg,wp,lp,2,timeout,out var result);
        report.Add($"SendMessageTimeout 0x{msg:X}",new{hwnd=(long)hwnd,wp=(long)wp,lp=(long)lp,timeout,completed=ok!=0,result=(ulong)result,elapsedMs=Stopwatch.GetElapsedTime(start).TotalMilliseconds,error=Marshal.GetLastWin32Error()});return ok!=0;
    }
    // A timed-out cross-process message may still consume its buffer: retain it until target exit.
    readonly List<nint> buffers=new();
    public bool Text(nint hwnd,uint msg,string text,uint timeout=500)
    { nint p=Marshal.StringToHGlobalUni(text);buffers.Add(p);return Message(hwnd,msg,msg==0xC2?1:0,p,timeout); }
    public void ReleaseAfterExit() { if(!process.HasExited)return;foreach(var p in buffers)Marshal.FreeHGlobal(p);buffers.Clear(); }
}
public sealed class FixtureSession : IDisposable
{
    public Process Process {get;}
    public OwnedTarget Target {get;}
    public string DirectoryPath {get;}
    public string LogPath => Path.Combine(DirectoryPath,"events.jsonl");
    readonly string state,commands; readonly Report report; long seq;
    FixtureSession(Report r)
    {
        report=r;DirectoryPath=Paths.Temp();state=Path.Combine(DirectoryPath,"state.json");commands=Path.Combine(DirectoryPath,"commands.jsonl");
        Process=Launcher.Self(new[]{"fixture-winforms","--log",LogPath,"--commands",commands,"--state",state});
        Target=new OwnedTarget(Process,r);
        try { Wait(()=>File.Exists(state),5000);r.Add("fixture啟動",new{Process.Id,DirectoryPath,log=LogPath}); }
        catch {if(!Process.HasExited){Process.Kill();Process.WaitForExit(2000);}Process.Dispose();throw;}
    }
    public static FixtureSession Start(Report r)=>new(r);
    public JsonElement Snapshot()
    {
        for(int i=0;i<20;i++)try{using var stream=new FileStream(state,FileMode.Open,FileAccess.Read,FileShare.ReadWrite|FileShare.Delete);using var d=JsonDocument.Parse(stream);return d.RootElement.Clone();}catch(IOException){Thread.Sleep(20);}catch(JsonException){Thread.Sleep(20);}
        throw new IOException("fixture state 讀取失敗");
    }
    public nint Main => (nint)Snapshot().GetProperty("main").GetInt64();
    public nint Decoy => (nint)Snapshot().GetProperty("decoy").GetInt64();
    public nint Handle(string id)=>(nint)Snapshot().GetProperty("controls").GetProperty(id).GetProperty("hwnd").GetInt64();
    public JsonElement Control(string id)=>Snapshot().GetProperty("controls").GetProperty(id).Clone();
    public void Send(object command)
    {
        var d=JsonSerializer.Deserialize<Dictionary<string,JsonElement>>(JsonSerializer.Serialize(command))!;
        string? op=d["op"].GetString();
        if(op is "text" or "select")
        {
            string id=d.TryGetValue("id",out var idValue)?idValue.GetString()!:"single";
            if(!Target.Guard(Handle(id),"fixture command "+op))throw new InvalidOperationException("安全拒絕fixture控制指令。");
        }
        d["seq"]=JsonSerializer.SerializeToElement(++seq);
        File.AppendAllText(commands,JsonSerializer.Serialize(d)+"\n");
        Wait(()=>Snapshot().GetProperty("lastCommandSeq").GetInt64()>=seq,5000);
        var s=Snapshot();
        if(s.TryGetProperty("lastCommandError",out var error) && error.ValueKind!=JsonValueKind.Null)
            throw new InvalidOperationException("Fixture command失敗："+error.GetRawText());
    }
    public bool Focus(string id)
    {if(!Target.Activate(id=="decoy"?Decoy:Main))return false;Send(new{op="focus",id});Thread.Sleep(100);bool focused=Control(id).GetProperty("focused").GetBoolean();report.Add("fixture欄位焦點 "+id,new{focused},focused,"目標欄位Focused必須為true");return Target.Guard(Handle(id),"focus "+id) && focused;}
    public void Capture(string step,bool? pass=null,string criterion="")
    { Send(new{op="snapshot"});report.Add(step,new{state=Snapshot(),events=ReadEvents()},pass,criterion); }
    public JsonElement[] ReadEvents()
    {string text;
     // fixture 以 FileShare.ReadWrite 持續寫入；File.ReadAllText 預設 FileShare.Read 會與之衝突，需明確允許共用。
     using(var fs=new FileStream(LogPath,FileMode.Open,FileAccess.Read,FileShare.ReadWrite|FileShare.Delete))using(var sr=new StreamReader(fs,new System.Text.UTF8Encoding(false)))text=sr.ReadToEnd();
     int end=text.LastIndexOf('\n');return end<0?Array.Empty<JsonElement>():Model.TextModel.ParseLines(text[..(end+1)]).ToArray();}
    public static void Wait(Func<bool> check,int ms)
    {var sw=Stopwatch.StartNew();while(sw.ElapsedMilliseconds<ms){if(check())return;Thread.Sleep(25);}throw new TimeoutException("等候fixture/控制命令逾時");}
    public void Dispose()
    {
        try {if(!Process.HasExited){Send(new{op="close"});Process.WaitForExit(2000);}}catch(Exception ex){report.Add("fixture清理",new{ex.Message});}
        finally {if(!Process.HasExited){Process.Kill();Process.WaitForExit(2000);}}
        try{report.Add("fixture完整JSONL",new{log=LogPath,events=ReadEvents()});Target.ReleaseAfterExit();}catch(Exception ex){report.Add("log讀取錯誤",new{ex.Message});}
        Process.Dispose();
    }
}
public static class NetworkSafety
{
    // PROBELAB_ALLOW_ONLINE=1 時放寬「必須實體離線」。瀏覽器仍使用全新 profile、只載本機檔案、
    // 代理指向不轉送的本機位址、CDP 只綁 127.0.0.1；僅在使用者明確同意且不能斷網（遠端操作中）時使用。
    public static bool IsOffline() => Environment.GetEnvironmentVariable("PROBELAB_ALLOW_ONLINE") == "1" || !System.Net.NetworkInformation.NetworkInterface.GetAllNetworkInterfaces().Any(n=>n.NetworkInterfaceType!=System.Net.NetworkInformation.NetworkInterfaceType.Loopback && n.OperationalStatus==System.Net.NetworkInformation.OperationalStatus.Up);
    public static bool RequireOffline(Report r)
    {
        var adapters=System.Net.NetworkInformation.NetworkInterface.GetAllNetworkInterfaces().Select(n=>new{type=n.NetworkInterfaceType.ToString(),status=n.OperationalStatus.ToString()}).ToArray();
        bool offline=IsOffline();
        r.Add("瀏覽器離線前置條件",new{offline,adapters},null,offline?"無啟用中的非loopback介面，僅使用127.0.0.1 CDP":"略過：請停用Wi-Fi／拔除網路，包含啟用中的虛擬介面；工具不修改網路設定");return offline;
    }
}
