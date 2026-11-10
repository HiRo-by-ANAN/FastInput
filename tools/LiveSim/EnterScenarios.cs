using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json;

namespace FastInput.LiveSim;

/// <summary>Explicit desktop validation only. Building this file performs no desktop interaction.</summary>
internal static class EnterScenarios
{
    public static async Task<Runner.Result> RunAsync(string server, string work, string name, bool physicalClicks)
    {
        var scenario = "enter-" + name;
        var directory = Path.Combine(work, scenario); Directory.CreateDirectory(directory);
        Process? host = null, fixture = null; Phone? phone = null;
        var operations = new ConcurrentQueue<JsonElement>();
        int? enterEvents = null, defaultClicks = null, attempts = null, afterEnterEdits = null;
        try
        {
            if (!File.Exists(server)) throw new FileNotFoundException("Rust server missing: build offline target-p10 or use --server");
            host = Program.Child(physicalClicks ? "--host-physical" : "--host", server, Path.Combine(directory, "server-data"));
            var helloLine = await host.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(15)) ?? throw new IOException("host ended before hello");
            using var hello = JsonDocument.Parse(helloLine);
            phone = new Phone(hello.RootElement.GetProperty("desktop_url").GetString()!);
            var captureHost = host;
            _ = Task.Run(async () =>
            {
                try { while (await captureHost.StandardOutput.ReadLineAsync() is { } line) { using var json = JsonDocument.Parse(line); operations.Enqueue(json.RootElement.Clone()); } }
                catch (Exception) { }
            });
            // Only the enabled case changes the test desktop setting. Other cases retain false by default,
            // except blacklist, which opts in locally to prove the sensitive target still cannot bind.
            if (name != "disabled") { await host.StandardInput.WriteLineAsync("enter-on"); await host.StandardInput.FlushAsync(); }
            var state = Path.Combine(directory, "fixture.json");
            fixture = Program.Child("--fixture", state);
            await Program.WaitAsync(() => File.Exists(state), "Enter fixture ready");
            var initial = Read(state); var targetId = name == "blacklist" ? "password" : "single";
            var field = initial.GetProperty("fields").GetProperty(targetId);
            var rect = field.GetProperty("rect"); var x = rect.GetProperty("X").GetInt32() + 12; var y = rect.GetProperty("Y").GetInt32() + 10;
            await phone.BeginAsync();
            if (physicalClicks)
                Console.WriteLine($"[{scenario}] 請在 30 秒內用實體滑鼠點選 {targetId} 欄位（約 {x},{y}）；之後不要操作鍵盤滑鼠。");
            else Native.Click((nint)initial.GetProperty("window").GetInt64(), (nint)field.GetProperty("hwnd").GetInt64(), x, y);

            if (name == "blacklist")
            {
                await phone.WaitStatusAsync(j => j.GetProperty("phase").GetString() is "REVOKED" or "PAUSED", physicalClicks ? 30000 : 10000);
                // This target is rejected before an epoch exists. A forged Enter intent must also fail.
                var rejected = await phone.PostAsync("/api/realtime/enter", new { v = 1, request_id = phone.RequestId, target_epoch = Guid.NewGuid().ToString("N"), enter_id = Guid.NewGuid().ToString("N"), after_seq = 0, hold_ms = 640 });
                if (rejected.Status == 200) throw new IOException("password blacklist fixture accepted an Enter intent");
            }
            else
            {
                await phone.BindAsync(physicalClicks ? 30000 : 10000);
                await phone.WaitStatusAsync(j => j.GetProperty("enter_available").GetBoolean() == (name == "enabled"));
                await phone.SendAsync(1, "M4_SINGLE_ENTER", 0);
                var enterId = Guid.NewGuid().ToString("N");
                var response = await phone.HoldEnterAsync(enterId, 1);
                if (name == "disabled")
                {
                    if (response.Status != 409 || response.Json.GetProperty("code").GetString() != "enter_disabled")
                        throw new IOException("default-off Enter must return 409 enter_disabled");
                }
                else
                {
                    Phone.Ensure(response);
                    var paused = await phone.WaitStatusAsync(j => j.GetProperty("phase").GetString() == "PAUSED" && j.GetProperty("reason").GetString() == "enter_sent");
                    if (paused.GetProperty("enter").GetProperty("status").GetString() != "applied") throw new IOException("Enter result was not applied");
                    if (paused.GetProperty("enter_available").GetBoolean()) throw new IOException("paused epoch still advertises Enter");
                    Phone.Ensure(await phone.PostAsync("/api/realtime/enter", phone.Enter(enterId, 1, 640)));
                    var baseline = Count(operations, "edit");
                    var later = await phone.PostAsync("/api/realtime/states", phone.State(2, "SHOULD_NOT_APPLY"));
                    if (later.Status == 200) throw new IOException("post-Enter text accepted");
                    await Task.Delay(300);
                    afterEnterEdits = Count(operations, "edit") - baseline;
                    if (afterEnterEdits != 0) throw new IOException("post-Enter adapter edit observed");
                }
            }
            await Task.Delay(200);
            var snapshot = Read(state);
            enterEvents = snapshot.GetProperty("enter_events").GetInt32();
            defaultClicks = snapshot.GetProperty("default_button_clicks").GetInt32();
            attempts = Count(operations, "enter_attempt");
            var expected = name == "enabled" ? 1 : 0;
            if (enterEvents != expected || defaultClicks != expected || attempts != expected)
                throw new IOException($"expected {expected} Enter: dialog events={enterEvents}, default actions={defaultClicks}, adapter attempts={attempts}");
            if (snapshot.GetProperty("fields").GetProperty("decoy").GetProperty("text").GetString() != "DECOY_UNCHANGED"
                || snapshot.GetProperty("fields").GetProperty("password").GetProperty("text").GetString() != "") throw new IOException("decoy/password changed");
            if (name != "blacklist" && snapshot.GetProperty("fields").GetProperty("single").GetProperty("text").GetString() != "M4_SINGLE_ENTER")
                throw new IOException("single-line field text unexpectedly changed");
            await phone.ReleaseAsync();
            return new(scenario, true, null, null, null, 0, null, null, [], [], enterEvents, defaultClicks, attempts, afterEnterEdits);
        }
        catch (Exception e) { return new(scenario, false, e.GetType().Name + ": " + e.Message, null, null, null, null, null, [], [], enterEvents, defaultClicks, attempts, afterEnterEdits); }
        finally
        {
            phone?.Dispose();
            if (fixture is not null) { await Program.StopAsync(fixture); fixture.Dispose(); }
            if (host is not null) { await Program.StopAsync(host); host.Dispose(); }
        }
    }
    private static int Count(IEnumerable<JsonElement> operations, string kind) => operations.Count(j => j.GetProperty("kind").GetString() == kind);
    private static JsonElement Read(string path) { using var json = JsonDocument.Parse(File.ReadAllText(path)); return json.RootElement.Clone(); }
}
