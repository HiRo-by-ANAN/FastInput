using System.Diagnostics;
using System.Text.Json;

namespace ProbeLab;
public static class Paths
{
    public static string Root { get; } = FindRoot();
    static string FindRoot()
    {
        foreach (var start in new[] { Environment.CurrentDirectory, AppContext.BaseDirectory })
            for (var d = new DirectoryInfo(start); d != null; d = d.Parent)
            {
                if (File.Exists(Path.Combine(d.FullName, "ProbeLab.csproj"))) return d.FullName;
                if (File.Exists(Path.Combine(d.FullName, "tools", "ProbeLab", "ProbeLab.csproj"))) return Path.Combine(d.FullName, "tools", "ProbeLab");
            }
        throw new InvalidOperationException("找不到 tools/ProbeLab 專案目錄");
    }
    public static string Temp() { string p = Path.Combine(Path.GetTempPath(), "ProbeLab-"+Guid.NewGuid().ToString("N")); Directory.CreateDirectory(p); return p; }
}
public record Measurement(string Step, object Raw, bool? Pass, string Criterion, long Timestamp);
public sealed class Report
{
    public string Id { get; }
    public List<Measurement> Measurements { get; } = new();
    public Report(string id) { Id = id; }
    public void Add(string step, object raw, bool? pass = null, string criterion = "")
    { if(string.IsNullOrEmpty(criterion))criterion="原始觀察／步驟紀錄；未預設Windows行為";Measurements.Add(new(step, raw, pass, criterion, Stopwatch.GetTimestamp())); Console.WriteLine($"[{Id}] {step}: {(pass is null ? "觀察" : pass.Value ? "通過" : "失敗")}"); }
    public void Save()
    {
        Directory.CreateDirectory(Path.Combine(Paths.Root,"results"));
        string stem = Path.Combine(Paths.Root,"results",Id);
        File.WriteAllText(stem+".json",JsonSerializer.Serialize(new { experiment=Id, utc=DateTime.UtcNow, os=Environment.OSVersion.ToString(), runtime=Environment.Version.ToString(), stopwatchFrequency=Stopwatch.Frequency, status=Status(), measurements=Measurements },new JsonSerializerOptions { WriteIndented=true }));
        File.WriteAllText(stem+".md",$"# {Id}\n\n狀態：{Status()}；量測頻率：{Stopwatch.Frequency} ticks/s\n\n"+string.Join("\n\n",Measurements.Select(m=>$"## {m.Step}\n\n判準：{m.Criterion}\n\n結果：{(m.Pass is null ? "觀察／未判定" : m.Pass.Value ? "通過" : "失敗")}\n\n```json\n{JsonSerializer.Serialize(m.Raw,new JsonSerializerOptions {WriteIndented=true})}\n```")));
    }
    string Status() => Measurements.Any(m=>m.Pass==false) ? "失敗" : Measurements.Any(m=>m.Step.Contains("未執行") || m.Step.Contains("略過") || m.Criterion.StartsWith("略過")) ? "未執行／部分執行" : Measurements.Any(m=>m.Pass==true) ? "通過（另見各項觀察）" : "未判定／略過";
    public static void Summary()
    {
        var dir=Path.Combine(Paths.Root,"results"); Directory.CreateDirectory(dir);
        var sections=new List<string>();
        foreach(var id in new[]{"E1","E2","E3","E4","E5","E6","E7","E8","E12","E13","E14","U16"})
        {
            string f=Path.Combine(dir,id+".json");
            if(!File.Exists(f)) {sections.Add($"## {id}\n\n未執行。");continue;}
            using var d=JsonDocument.Parse(File.ReadAllText(f));
            var rows=d.RootElement.GetProperty("measurements").EnumerateArray().Select(m=>
            {
                var metrics=new List<string>();Metrics(m.GetProperty("Raw"),"",metrics);
                string status=m.GetProperty("Pass").ValueKind switch {JsonValueKind.True=>"通過",JsonValueKind.False=>"失敗",_=>"觀察／未判定"};
                return $"| {Cell(m.GetProperty("Step").GetString()!)} | {Cell(m.GetProperty("Criterion").GetString()!)} | {status} | {Cell(string.Join("；",metrics.Take(32)))} |";
            });
            sections.Add($"## {id}\n\n{d.RootElement.GetProperty("status").GetString()}；[完整原始量測]({id}.json)\n\n| 步驟 | 判準 | 結果 | 關鍵原始數值（前32項） |\n|---|---|---|---|\n"+string.Join("\n",rows));
        }
        File.WriteAllText(Path.Combine(dir,"SUMMARY.md"),"# ProbeLab 實驗彙整\n\n原始數值來自各實驗 JSON；未判定不等於通過。E5/E6/E14 與 E13/U16 的來源關係見 README。\n\n"+string.Join("\n\n",sections));
    }
    static string Cell(string s)=>s.Replace("|","\\|").Replace("\r","").Replace("\n"," ");
    static void Metrics(JsonElement e,string path,List<string> values)
    {
        if(values.Count>=32)return;
        switch(e.ValueKind)
        {
            case JsonValueKind.Object:
                foreach(var p in e.EnumerateObject())Metrics(p.Value,path.Length==0?p.Name:path+"."+p.Name,values);
                break;
            case JsonValueKind.Array:
                if(e.EnumerateArray().All(x=>x.ValueKind==JsonValueKind.Number))values.Add(path+"="+e.GetRawText().Replace("\r","").Replace("\n","").Replace(" ",""));
                else values.Add(path+".count="+e.GetArrayLength());
                break;
            case JsonValueKind.Number:case JsonValueKind.True:case JsonValueKind.False: values.Add(path+"="+e.GetRawText());break;
            case JsonValueKind.String:
                if(path.EndsWith("reason") || path.EndsWith("runtime") || path.EndsWith("interpretation"))values.Add(path+"="+e.GetString());
                break;
        }
    }
}
public static class Launcher
{
    public static Process Start(string exe,IEnumerable<string> args)
    {
        var psi=new ProcessStartInfo(exe) { UseShellExecute=false };
        foreach(var a in args) psi.ArgumentList.Add(a);
        Console.WriteLine("啟動："+Quote(exe)+" "+string.Join(" ",psi.ArgumentList.Select(Quote)));
        return Process.Start(psi) ?? throw new InvalidOperationException("啟動失敗");
    }
    // Edge 以 UseShellExecute=false 啟動時（繼承主控台／重導向控制代碼）會立即結束；
    // 改用 ShellExecute 啟動（與 PowerShell Start-Process 相同）後可正常存活，故瀏覽器改走此路徑。
    public static Process StartShell(string exe,IEnumerable<string> args)
    {
        var list=args.ToList();
        var psi=new ProcessStartInfo(exe){UseShellExecute=true,Arguments=string.Join(" ",list.Select(Quote))};
        Console.WriteLine("啟動："+Quote(exe)+" "+psi.Arguments);
        return Process.Start(psi) ?? throw new InvalidOperationException("啟動失敗");
    }
    static string Quote(string s)
    {
        var result=new System.Text.StringBuilder("\"");int slashes=0;
        foreach(char c in s)
        {
            if(c=='\\'){slashes++;continue;}
            if(c=='\"')result.Append('\\',slashes*2+1).Append(c);
            else result.Append('\\',slashes).Append(c);
            slashes=0;
        }
        return result.Append('\\',slashes*2).Append('"').ToString();
    }
    public static Process Self(IEnumerable<string> args)
    {
        var exe=Environment.ProcessPath!;
        return Path.GetFileNameWithoutExtension(exe).Equals("dotnet",StringComparison.OrdinalIgnoreCase)
            ? Start(exe,new[]{typeof(Launcher).Assembly.Location}.Concat(args)) : Start(exe,args);
    }
}
