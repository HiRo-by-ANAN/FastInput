using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json;
using FastInput.Windows.Realtime;
namespace FastInput.LiveSim;
internal static class Runner
{
    internal sealed record Result(string Scenario, bool Passed, string? Error, int? CorrectFinal, int? Flicker, int? WrongTargetWrites, int? DuplicateApplications, int? ExcessMiddleEdits, double[] LatencyMs, double[] StopMs, int? EnterEvents = null, int? DefaultButtonClicks = null, int? EnterAttempts = null, int? PostEnterApplications = null);
    public static async Task<int> RunAsync(string[] args)
    {
        string Option(string name, string fallback) { var i = Array.IndexOf(args, name); return i >= 0 && i + 1 < args.Length ? args[i + 1] : fallback; }
        var root = FindRoot(); var server = Path.GetFullPath(Option("--server", Environment.GetEnvironmentVariable("FASTINPUT_TEST_SERVER") ?? Path.Combine(root, "target-p10", "debug", "fastinput.exe")));
        var output = Path.GetFullPath(Option("--output", Path.Combine(root, "tools", "LiveSim", "reports"))); Directory.CreateDirectory(output);
        var work = Path.Combine(output, "work-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(work);
        var delay = int.Parse(Option("--delay-ms", "0")); if (delay < 0 || delay > 5000) throw new ArgumentException("delay must be 0..5000");
        var physicalClicks = args.Contains("--physical-clicks", StringComparer.Ordinal);
        var results = new List<Result>();
        try
        {
            if (args[0] is "all" or "blacklist") results.Add(Blacklist());
            if (args[0] is "all" or "enter")
                foreach (var name in new[] { "disabled", "enabled", "blacklist" }.Where(n => Option("--case", "all") is "all" || n == Option("--case", "all")))
                    results.Add(await EnterScenarios.RunAsync(server, work, name, physicalClicks));
            if (args[0] is "all" or "fixture")
            {
                foreach (var name in new[] { "middle", "incremental", "long1000", "reconnect", "disconnect", "foreground", "password", "multi", "rich" }.Where(n => Option("--case", "all") is "all" || n == Option("--case", "all")))
                    results.Add(await RunCaseAsync(server, work, "fixture-" + name, "fixture", name, delay, physicalClicks));
            }
            if (args[0] is "all" or "chrome")
                foreach (var field in new[] { "input", "textarea", "editable", "password" }.Where(n => Option("--case", "all") is "all" || n == Option("--case", "all"))) results.Add(await RunCaseAsync(server, work, "chrome-" + field, "chrome", field, delay, physicalClicks));
            if (results.Count == 0) throw new ArgumentException("unknown scenario group");
            var latency = results.SelectMany(r => r.LatencyMs).Order().ToArray(); var stops = results.SelectMany(r => r.StopMs).Order().ToArray();
            var report = new { generated_utc = DateTimeOffset.UtcNow, desktop_executed = results.Any(r => r.Scenario != "blacklist-fake-process-facts"), physical_clicks = physicalClicks, scenarios = results, metrics = new { final_text_accuracy = Ratio(results.Where(r => r.CorrectFinal.HasValue).Select(r => r.CorrectFinal!.Value)), flicker = Sum(results.Select(r => r.Flicker)), wrong_target_writes = Sum(results.Select(r => r.WrongTargetWrites)), duplicate_applications = Sum(results.Select(r => r.DuplicateApplications)), excess_middle_edits = Sum(results.Select(r => r.ExcessMiddleEdits)), latency_samples = latency.Length, latency_p50_ms = Percentile(latency, .5), latency_p95_ms = Percentile(latency, .95), stop_samples = stops.Length, stop_p95_ms = Percentile(stops, .95) }, limitations = new[] { "Desktop values are produced by this run only; null means unmeasured.", "Wrong target observation covers owned fixture decoy/password fields, not arbitrary desktop windows.", "Stop p95 measures host stdin stop command through authoritative HTTP revocation observation, followed by a 200 ms zero-new-operation observation; it is not an OS atomicity guarantee.", physicalClicks ? "The production RealTargetPicker received manually performed hardware clicks; autoapproval remains test-host-only." : "The test picker accepts only LiveSim injected test-marker clicks; production picker rejects injected clicks.", "Omnibox is optional and not automated; see README.", "UIA emoji offset mapping remains uncertain; no claim based on these BMP cases." } };
            var json = JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }); File.WriteAllText(Path.Combine(output, "latest.json"), json);
            var md = "# LiveSim 桌面報告\n\n|情境|結果|失敗原因|\n|---|---|---|\n" + string.Join("\n", results.Select(r => $"|{r.Scenario}|{(r.Passed ? "PASS" : "FAIL")}|{r.Error?.Replace("|", "/")}|")) + $"\n\n最終文字正確率：{report.metrics.final_text_accuracy:P2}；延遲樣本：{latency.Length}，p50={report.metrics.latency_p50_ms:F3} ms，p95={report.metrics.latency_p95_ms:F3} ms；停止樣本：{stops.Length}，p95={report.metrics.stop_p95_ms:F3} ms。\n\n閃爍={report.metrics.flicker}；錯目標={report.metrics.wrong_target_writes}；重複套用={report.metrics.duplicate_applications}；中間改字多餘編輯={report.metrics.excess_middle_edits}。\n\n錯目標僅觀察自有 decoy/password 欄位。未量測數值在 JSON 為 null。\n";
            var enterResults = results.Where(r => r.Scenario.StartsWith("enter-", StringComparison.Ordinal)).ToArray();
            if (enterResults.Length > 0)
                md += "\n|M4 情境|Enter 事件|預設按鈕|Enter adapter 呼叫|Enter 後新增文字套用|\n|---|---|---|---|---|\n"
                    + string.Join("\n", enterResults.Select(r => $"|{r.Scenario}|{r.EnterEvents?.ToString() ?? "未量測"}|{r.DefaultButtonClicks?.ToString() ?? "未量測"}|{r.EnterAttempts?.ToString() ?? "未量測"}|{r.PostEnterApplications?.ToString() ?? "未量測"}|"))
                    + "\n\nEnter applied 僅代表事件送出；fixture 事件與預設按鈕另行觀察，不保證任意應用已完成提交。\n";
            File.WriteAllText(Path.Combine(output, "latest.md"), md); Console.WriteLine(md); return results.All(r => r.Passed) ? 0 : 1;
        }
        finally
        {
            var fullWork = Path.GetFullPath(work); var fullOutput = Path.GetFullPath(output).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            if (!fullWork.StartsWith(fullOutput, StringComparison.OrdinalIgnoreCase) || Path.GetFileName(fullWork) != Path.GetFileName(work) || !Path.GetFileName(fullWork).StartsWith("work-", StringComparison.Ordinal)) throw new IOException("cleanup boundary invalid");
            if (Directory.Exists(fullWork)) Directory.Delete(fullWork, true);
        }
    }
    private static async Task<Result> RunCaseAsync(string server, string work, string scenario, string kind, string name, int delay, bool physicalClicks)
    {
        var directory = Path.Combine(work, scenario); Directory.CreateDirectory(directory); Process? host = null, fixture = null; BrowserFixture? browser = null; Phone? phone = null;
        var latencies = new List<double>(); var stops = new List<double>(); var edits = new ConcurrentQueue<JsonElement>();
        int? measuredCorrect = null, measuredFlicker = null, measuredWrong = null, measuredDuplicates = null, measuredExcess = null;
        try
        {
            if (!File.Exists(server)) throw new FileNotFoundException("Rust server missing: use --server or build offline target-p10");
            host = Program.Child(physicalClicks ? "--host-physical" : "--host", server, Path.Combine(directory, "server-data"));
            var helloLine = await host.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(15)) ?? throw new IOException("host ended before hello");
            using var hello = JsonDocument.Parse(helloLine); phone = new Phone(hello.RootElement.GetProperty("desktop_url").GetString()!);
            _ = Task.Run(async () => { try { while (await host.StandardOutput.ReadLineAsync() is { } line) { using var json = JsonDocument.Parse(line); edits.Enqueue(json.RootElement.Clone()); } } catch (Exception) { } });
            nint window, field = 0, decoy = 0; int x, y; var state = Path.Combine(directory, "fixture.json");
            var targetId = name is "multi" or "rich" or "password" ? name : "single";
            if (kind == "fixture")
            {
                fixture = Program.Child("--fixture", state); await Program.WaitAsync(() => File.Exists(state), "fixture ready");
                var snapshot = Read(state); window = (nint)snapshot.GetProperty("window").GetInt64(); var f = snapshot.GetProperty("fields").GetProperty(targetId); field = (nint)f.GetProperty("hwnd").GetInt64(); decoy = (nint)snapshot.GetProperty("fields").GetProperty("decoy").GetProperty("hwnd").GetInt64();
                var rect = f.GetProperty("rect"); x = rect.GetProperty("X").GetInt32() + 12; y = rect.GetProperty("Y").GetInt32() + 10;
            }
            else { browser = await BrowserFixture.StartAsync(directory); window = browser.Window; (x, y) = browser.FieldPoint(name); }
            await phone.BeginAsync();
            if (physicalClicks)
                Console.WriteLine($"[{scenario}] 已核准、等待正式 picker：請在 30 秒內用實體滑鼠點選 {(kind == "fixture" ? targetId : name)} 欄位（座標約 {x},{y}）。點選後請勿再操作鍵盤滑鼠，等待報告。");
            else Native.Click(window, field, x, y);
            if (name == "password")
            {
                await phone.WaitStatusAsync(j => j.GetProperty("phase").GetString() is "REVOKED" or "PAUSED", physicalClicks ? 30000 : 10000);
                await Task.Delay(100);
                string passwordValue, otherValue;
                if (kind == "fixture")
                {
                    var snapshot = Read(state);
                    passwordValue = snapshot.GetProperty("fields").GetProperty("password").GetProperty("text").GetString()!;
                    otherValue = snapshot.GetProperty("fields").GetProperty("decoy").GetProperty("text").GetString()!;
                }
                else
                {
                    var snapshot = await browser!.SnapshotAsync("password");
                    passwordValue = snapshot.GetProperty("text").GetString()!;
                    otherValue = snapshot.GetProperty("decoy").GetString()!;
                }
                measuredWrong = passwordValue.Length == 0 && otherValue == (kind == "fixture" ? "DECOY_UNCHANGED" : "") ? 0 : 1;
                if (!edits.IsEmpty || measuredWrong != 0) throw new IOException("password rejected but observed an adapter operation or field mutation");
                return new(scenario, true, null, null, null, measuredWrong, null, null, [], []);
            }
            await phone.BindAsync(physicalClicks ? 30000 : 10000);
            var words = name == "long1000" ? new[] { new string('x', 1000) } : name == "incremental" ? new[] { "G", "Go", "Google" } : new[] { "Goagle", "Google" };
            var seq = 0; foreach (var word in words) latencies.Add(await phone.SendAsync(++seq, word, delay));
            await Task.Delay(60);
            var priorEdits = edits.Count;
            var duplicate = await phone.PostAsync("/api/realtime/states", phone.State(seq, words[^1])); Phone.Ensure(duplicate); await Task.Delay(100);
            var duplicates = edits.Count - priorEdits;
            measuredDuplicates = duplicates;
            var gap = await phone.PostAsync("/api/realtime/states", phone.State(seq + 2, words[^1] + "gap")); if (gap.Status != 409) throw new IOException("out of order state not rejected");
            if (name == "reconnect") { await phone.ReconnectAsync(); Phone.Ensure(await phone.PostAsync("/api/realtime/states", phone.State(seq, words[^1]))); await Task.Delay(100); duplicates += edits.Count - priorEdits; }
            if (name == "disconnect") { await phone.TimeoutDisconnectAsync(); await phone.ReconnectAsync(); var late = await phone.PostAsync("/api/realtime/states", phone.State(seq + 1, "SHOULD_NOT_APPLY")); if (late.Status == 200) throw new IOException("disconnected epoch accepted"); }
            if (name == "foreground")
            {
                if (!Native.SetForegroundWindow((nint)hello.RootElement.GetProperty("window").GetInt64())) throw new IOException("foreground switch to owned test host failed");
                await phone.WaitStatusAsync(j => j.GetProperty("phase").GetString() is "REVOKED" or "UNKNOWN");
            }
            var beforeStop = edits.Count; var stopWatch = Stopwatch.StartNew(); await host.StandardInput.WriteLineAsync("stop"); await host.StandardInput.FlushAsync();
            await phone.WaitStatusAsync(j => j.GetProperty("phase").GetString() is "REVOKED" or "UNKNOWN"); if (name is not "disconnect" and not "foreground") stops.Add(stopWatch.Elapsed.TotalMilliseconds);
            var afterStop = await phone.PostAsync("/api/realtime/states", phone.State(seq + 1, "SHOULD_NOT_APPLY")); if (afterStop.Status == 200) throw new IOException("post-stop state accepted");
            await Task.Delay(200); if (edits.Count != beforeStop) throw new IOException("operation after stop");
            string actual, decoyText; string[] values;
            if (kind == "fixture")
            {
                var snapshot = Read(state); actual = snapshot.GetProperty("fields").GetProperty(targetId).GetProperty("text").GetString()!; decoyText = snapshot.GetProperty("fields").GetProperty("decoy").GetProperty("text").GetString()!;
                values = snapshot.GetProperty("events").EnumerateArray().Where(e => e.GetProperty("kind").GetString() == "text" && e.GetProperty("id").GetString() == targetId).Select(e => e.GetProperty("text").GetString()!).ToArray();
            }
            else { var snapshot = await browser!.SnapshotAsync(name); actual = snapshot.GetProperty("text").GetString()!; decoyText = snapshot.GetProperty("decoy").GetString()!; values = snapshot.GetProperty("events").EnumerateArray().Where(e => e.GetProperty("id").GetString() == name).Select(e => e.GetProperty("text").GetString()!).ToArray(); }
            var flicker = 0; for (var i = 1; i + 1 < values.Length; i++) if (values[i].Length == 0 && values[i - 1].Length > 0 && values.Skip(i + 1).Contains(values[i - 1])) flicker++;
            var wrong = decoyText == (kind == "fixture" ? "DECOY_UNCHANGED" : "") ? 0 : 1;
            var middle = edits.ToArray().Skip(1).FirstOrDefault();
            var excess = words.Length == 2 && words[0] == "Goagle" ? Math.Max(0, edits.Count - 2) + (middle.ValueKind == JsonValueKind.Object ? Math.Max(0, middle.GetProperty("deleted").GetInt32() - 1) + Math.Max(0, middle.GetProperty("inserted").GetInt32() - 1) : 1) : 0;
            var correct = actual == words[^1] ? 1 : 0;
            measuredCorrect = correct; measuredFlicker = flicker; measuredWrong = wrong; measuredDuplicates = duplicates; measuredExcess = excess;
            var passed = correct == 1 && flicker == 0 && wrong == 0 && duplicates == 0 && excess == 0;
            var error = passed ? null : $"verification correct={correct} flicker={flicker} wrong={wrong} duplicates={duplicates} excess={excess}";
            await phone.ReleaseAsync(); return new(scenario, passed, error, correct, flicker, wrong, duplicates, excess, latencies.ToArray(), stops.ToArray());
        }
        catch (Exception e) { return new(scenario, false, e.GetType().Name + ": " + e.Message, measuredCorrect, measuredFlicker, measuredWrong, measuredDuplicates, measuredExcess, latencies.ToArray(), stops.ToArray()); }
        finally { phone?.Dispose(); if (browser is not null) await browser.DisposeAsync(); if (fixture is not null) { await Program.StopAsync(fixture); fixture.Dispose(); } if (host is not null) { await Program.StopAsync(host); host.Dispose(); } }
    }
    private static Result Blacklist()
    {
        var target = new TargetCandidate("pure-policy", 1, 2, new LocalRect(0, 0, 10, 10), new LocalRect(0, 0, 100, 100));
        var names = new[] { "WindowsTerminal", "conhost", "cmd", "powershell", "pwsh", "wt", "OpenConsole" };
        var ok = names.All(name => SafetyPolicy.Check(target, new SafetySnapshot(1, 2, name, 1, 1, 8192, 8192)) is not null);
        return new("blacklist-fake-process-facts", ok, ok ? null : "blacklist accepted", null, null, 0, 0, null, [], []);
    }
    private static JsonElement Read(string path) { using var json = JsonDocument.Parse(File.ReadAllText(path)); return json.RootElement.Clone(); }
    private static int? Sum(IEnumerable<int?> data) { var values = data.Where(x => x.HasValue).Select(x => x!.Value).ToArray(); return values.Length > 0 ? values.Sum() : null; }
    private static double? Ratio(IEnumerable<int> data) { var values = data.ToArray(); return values.Length > 0 ? values.Average() : null; }
    private static double? Percentile(double[] sorted, double percentile) => sorted.Length == 0 ? null : sorted[(int)Math.Ceiling(sorted.Length * percentile) - 1];
    private static string FindRoot() { for (var d = new DirectoryInfo(AppContext.BaseDirectory); d is not null; d = d.Parent) if (File.Exists(Path.Combine(d.FullName, "Cargo.toml"))) return d.FullName; throw new IOException("workspace root missing"); }
}





