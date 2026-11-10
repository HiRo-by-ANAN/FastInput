using System.Windows.Forms;

namespace ProbeLab;
public static class Program
{
    [STAThread] public static int Main(string[] args)
    {
        if(args.Length==2 && args[0]=="--probe-notepad") args=new[]{"probe-notepad","--file",args[1]};
        if(args.Length==0 || args[0] is "help" or "--help") { Console.WriteLine("ProbeLab <fixture-winforms|e1-edit-replace|e4-unicode|e5-sendinput|probe-notepad|e3-browser|e12-overlay|e13-hooks|report> [選項]\n詳見 tools/ProbeLab/README.md。桌面實驗請在真實桌面執行，執行期間不要碰鍵盤滑鼠。");return 0; }
        string? Opt(string name) {int i=Array.IndexOf(args,name);return i>=0 && i+1<args.Length?args[i+1]:null;}
        string Required(string name)=>Opt(name)??throw new ArgumentException("缺少 "+name);
        Report? r=null;
        try
        {
            var permitted=args[0] switch
            {
                "fixture-winforms"=>new[]{"--log","--commands","--state"},
                "e4-unicode"=>new[]{"--model-only"},"e5-sendinput"=>new[]{"--trials"},
                "probe-notepad"=>new[]{"--file"},
                "e1-edit-replace" or "e3-browser" or "e12-overlay" or "e13-hooks" or "report"=>Array.Empty<string>(),
                _=>throw new ArgumentException("未知子命令")
            };
            var used=new HashSet<string>();
            for(int i=1;i<args.Length;i++)
            {
                string key=args[i];
                if(!permitted.Contains(key) || !used.Add(key))throw new ArgumentException("未知或重複選項："+key);
                if(key!="--model-only" && (++i>=args.Length || args[i].StartsWith("--")))throw new ArgumentException("選項缺少值："+key);
            }
            if(args[0] != "report" && !args.Contains("--model-only")) Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
            if(args[0]=="fixture-winforms")
            {
                string log=Path.GetFullPath(Required("--log")),dir=Path.GetDirectoryName(log)!;
                Fixture.Run(log,Opt("--commands")??Path.Combine(dir,"commands.jsonl"),Opt("--state")??Path.Combine(dir,"state.json"));return 0;
            }
            if(args[0]=="report") {Report.Summary();Console.WriteLine("已寫 results/SUMMARY.md");return 0;}
            r=new Report(args[0] switch {"e1-edit-replace"=>"E1","e4-unicode"=>"E4","e5-sendinput"=>"E5","probe-notepad"=>"E2","e3-browser"=>"E3","e12-overlay"=>"E12","e13-hooks"=>"E13",_=>throw new ArgumentException("未知子命令")});
            r.Add("執行參數",new{args,process=Environment.ProcessId,modelOnly=args.Contains("--model-only")},null,"只使用自啟fixture；UIA僅讀metadata與長度；不提權、不寫登錄、不安裝");
            switch(args[0])
            {
                case "e1-edit-replace": EditProbes.Replace(r);break;
                case "e4-unicode": EditProbes.Unicode(r,args.Contains("--model-only"));break;
                case "e5-sendinput":int n=int.Parse(Opt("--trials")??"10");if(n<1 || n>100)throw new ArgumentOutOfRangeException("--trials", "1..100");EditProbes.SendInput(r,n);break;
                case "probe-notepad": UiaProbe.Notepad(r,Required("--file"));break;
                case "e3-browser":BrowserProbe.Run(r);break;
                case "e12-overlay": DesktopProbes.Overlay(r);break;
                case "e13-hooks": DesktopProbes.Hooks(r);break;
            }
            return r.Measurements.Any(m=>m.Pass==false)?1:0;
        }
        catch(Exception ex) { Console.Error.WriteLine(ex);r?.Add("未完成／例外",new{error=ex.ToString()},false,"執行不得有未處理錯誤；此處失敗不代表Windows功能不支援");return 2; }
        finally
        {
            if(r!=null)
            {
                r.Save();
                foreach(var alias in r.Id switch {"E1"=>new[]{"E7","E8"},"E5"=>new[]{"E6","E14"},"E13"=>new[]{"U16"},_=>Array.Empty<string>()})
                {var a=new Report(alias);a.Add("資料來源",new{source=r.Id},null,"同一次執行，相關原始量測見下列步驟");a.Measurements.AddRange(r.Measurements);a.Save();}
            }
        }
    }
}
