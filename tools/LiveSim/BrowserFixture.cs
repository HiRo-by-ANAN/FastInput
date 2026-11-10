using System.Diagnostics;
using System.Net.WebSockets;
using System.Text.Json;
using System.Windows.Automation;
namespace FastInput.LiveSim;
internal sealed class BrowserFixture : IAsyncDisposable
{
    private readonly string _profile;
    private readonly Process _launcher;
    private Process? _owner;
    private readonly int _port;
    private ClientWebSocket? _socket;
    private int _id;
    private bool _profileVerified;
    public nint Window { get; private set; }
    private BrowserFixture(string profile, Process launcher, int port) { _profile = profile; _launcher = launcher; _port = port; }
    public static async Task<BrowserFixture> StartAsync(string directory)
    {
        var configured = Environment.GetEnvironmentVariable("LIVESIM_BROWSER_PATH");
        var candidates = new[] { configured, Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Google", "Chrome", "Application", "chrome.exe"), Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Google", "Chrome", "Application", "chrome.exe"), Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Google", "Chrome", "Application", "chrome.exe"), Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Microsoft", "Edge", "Application", "msedge.exe") };
        if (Environment.GetEnvironmentVariable("PROBELAB_BROWSER") == "chrome") candidates = candidates.Where(c => c?.EndsWith("chrome.exe", StringComparison.OrdinalIgnoreCase) == true).ToArray();
        var exe = candidates.FirstOrDefault(c => c is not null && File.Exists(c)) ?? throw new FileNotFoundException("Chrome/Edge not found; set LIVESIM_BROWSER_PATH");
        var profile = Path.Combine(directory, "chrome-profile"); Directory.CreateDirectory(profile); var port = Net.FreePort();
        var start = new ProcessStartInfo(exe) { UseShellExecute = false };
        var title = "FastInput LiveSim " + Guid.NewGuid().ToString("N");
        var pagePath = Path.Combine(directory, "browser-fixture.html");
        File.WriteAllText(pagePath, File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "browser-fixture.html")).Replace("FastInput LiveSim isolated fixture", title, StringComparison.Ordinal));
        var page = new Uri(pagePath).AbsoluteUri;
        foreach (var arg in new[] { "--user-data-dir=" + profile, "--remote-debugging-port=" + port, "--remote-debugging-address=127.0.0.1", "--no-first-run", "--no-default-browser-check", "--enable-automation", "--force-renderer-accessibility", "--disable-background-networking", "--disable-component-update", "--disable-sync", "--disable-extensions", "--disable-default-apps", "--disable-domain-reliability", "--safebrowsing-disable-auto-update", "--disable-features=OptimizationHints,MediaRouter,Translate", "--proxy-server=http://127.0.0.1:" + port, "--proxy-bypass-list=<-loopback>", "--disable-quic", "--host-resolver-rules=MAP * ~NOTFOUND, EXCLUDE 127.0.0.1", "--new-window", page }) start.ArgumentList.Add(arg);
        var result = new BrowserFixture(profile, Process.Start(start) ?? throw new IOException("browser start"), port);
        try
        {
            using var http = new HttpClient(new HttpClientHandler { UseProxy = false }) { Timeout = TimeSpan.FromSeconds(2) };
            string? socket = null;
            for (var n = 0; n < 100 && socket is null; n++)
            {
                try { using var json = JsonDocument.Parse(await http.GetStringAsync($"http://127.0.0.1:{port}/json/list")); socket = json.RootElement.EnumerateArray().Where(j => j.TryGetProperty("url", out var u) && u.GetString() == page).Select(j => j.GetProperty("webSocketDebuggerUrl").GetString()).FirstOrDefault(); } catch (HttpRequestException) { } catch (TaskCanceledException) { }
                if (socket is null) await Task.Delay(100);
            }
            if (socket is null) throw new TimeoutException("isolated CDP page");
            var uri = new Uri(socket); if (uri.Host != "127.0.0.1" || uri.Port != port) throw new IOException("CDP not loopback owned port");
            result._socket = new(); await result._socket.ConnectAsync(uri, CancellationToken.None);
            var args = await result.CallAsync("Browser.getBrowserCommandLine", new { });
            if (!args.GetProperty("arguments").EnumerateArray().Any(a => a.GetString() == "--user-data-dir=" + profile)) throw new IOException("CDP profile identity mismatch");
            result._profileVerified = true;
            for (var n = 0; n < 100 && result.Window == 0; n++)
            {
                var roots = AutomationElement.RootElement.FindAll(TreeScope.Children, new PropertyCondition(AutomationElement.ClassNameProperty, "Chrome_WidgetWin_1"));
                foreach (AutomationElement root in roots)
                    if (root.Current.Name.Contains(title, StringComparison.Ordinal)) { result.Window = root.Current.NativeWindowHandle; result._owner = Process.GetProcessById(root.Current.ProcessId); break; }
                if (result.Window == 0) await Task.Delay(100);
            }
            if (result.Window == 0) throw new TimeoutException("isolated browser window");
            return result;
        }
        catch { await result.DisposeAsync(); throw; }
    }
    public (int X, int Y) FieldPoint(string id)
    {
        var root = AutomationElement.FromHandle(Window);
        var e = root.FindFirst(TreeScope.Descendants, new PropertyCondition(AutomationElement.NameProperty, "LiveSim-" + id)) ?? throw new IOException("UIA field not found");
        var r = e.Current.BoundingRectangle; return ((int)(r.Left + 12), (int)(r.Top + r.Height / 2));
    }
    public async Task<JsonElement> SnapshotAsync(string id)
    {
        // CDP is a read-only oracle. It never focuses, clicks, sets selection, or writes a field.
        var code = "(()=>{const e=document.getElementById(" + JsonSerializer.Serialize(id) + ");return {text:e.isContentEditable?e.textContent:e.value,decoy:document.getElementById('password').value,events:window.liveEvents}})()";
        var value = await CallAsync("Runtime.evaluate", new { expression = code, returnByValue = true }); return value.GetProperty("result").GetProperty("value").Clone();
    }
    private async Task<JsonElement> CallAsync(string method, object parameters)
    {
        using var timeout = new CancellationTokenSource(6000); var id = ++_id; var bytes = JsonSerializer.SerializeToUtf8Bytes(new { id, method, @params = parameters });
        await _socket!.SendAsync(new ArraySegment<byte>(bytes), WebSocketMessageType.Text, true, timeout.Token);
        while (true)
        {
            using var memory = new MemoryStream(); var buffer = new byte[32768]; WebSocketReceiveResult part;
            do { part = await _socket.ReceiveAsync(new ArraySegment<byte>(buffer), timeout.Token); if (part.MessageType == WebSocketMessageType.Close) throw new IOException("CDP closed"); memory.Write(buffer, 0, part.Count); if (memory.Length > 4 * 1024 * 1024) throw new IOException("CDP limit"); } while (!part.EndOfMessage);
            using var json = JsonDocument.Parse(memory.ToArray()); var root = json.RootElement;
            if (!root.TryGetProperty("id", out var reply) || reply.GetInt32() != id) continue;
            if (root.TryGetProperty("error", out _)) throw new IOException("CDP command failed"); return root.GetProperty("result").Clone();
        }
    }
    public async ValueTask DisposeAsync()
    {
        if (_profileVerified && _socket?.State == WebSocketState.Open)
        {
            try { await CallAsync("Browser.close", new { }); } catch (Exception) { /* Browser.close may close the socket before acknowledging. */ }
        }
        _socket?.Dispose();
        if (_owner is { HasExited: false }) { _owner.Kill(true); await _owner.WaitForExitAsync(); }
        if (!_launcher.HasExited) { _launcher.Kill(true); await _launcher.WaitForExitAsync(); }
        _owner?.Dispose(); _launcher.Dispose();
        // The runner deletes its verified per-run work directory after every owned process has exited.
        _ = _profile; _ = _port;
    }
}


