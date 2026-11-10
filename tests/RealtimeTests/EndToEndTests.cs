using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using FastInput.Windows.Realtime;

namespace FastInput.RealtimeTests;

internal static class EndToEndTests
{
    public static void Register(TestSuite suite) => suite.Add("e2e.real_stdio_http_sse_100_states", RunAsync);

    private static async Task RunAsync()
    {
        var root = FindRoot();
        var configured = Environment.GetEnvironmentVariable("FASTINPUT_TEST_SERVER");
        var server = configured ?? Path.Combine(root, "target-p10", "debug", "fastinput.exe");
        Check.True(File.Exists(server), "real Rust server missing; run cargo build --offline --target-dir .\\target-p10");
        var data = Path.Combine(root, "tests", "RealtimeTests", "obj", "e2e-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(data);
        var port = FreePort();
        var origin = new Uri($"http://127.0.0.1:{port}");
        var start = new ProcessStartInfo(server)
        {
            UseShellExecute = false, RedirectStandardInput = true, RedirectStandardOutput = true,
            RedirectStandardError = true, CreateNoWindow = true
        };
        foreach (var argument in new[] { "--native-stdio", "--no-browser", "--lan-ip", "127.0.0.1", "--port", port.ToString() })
            start.ArgumentList.Add(argument);
        start.Environment["FASTINPUT_DATA_DIR"] = data;
        using var process = Process.Start(start) ?? throw new InvalidOperationException("server start failed");
        var errors = DrainAsync(process.StandardError);
        using var link = new NativeBridgeClient(process.StandardOutput.BaseStream, process.StandardInput.BaseStream);
        using var handler = new HttpClientHandler { CookieContainer = new CookieContainer(), UseProxy = false };
        using var phone = new HttpClient(handler) { BaseAddress = origin, Timeout = TimeSpan.FromSeconds(5) };
        var adapter = new TimedAdapter();
        var picker = new FakeTargetPicker();
        var ui = new ApprovedUi();
        var settings = new RealtimeSettings { RealtimeEnabled = true };
        using var controller = new RealtimeController(link, new RealtimeClock(), ui, picker, adapter,
            new FakeSafetyProbe(), settings, new NullAuditLog());
        controller.SetHotkeyAvailable(true);
        Exception? callbackError = null;
        link.MessageReceived += async message =>
        {
            try { await controller.HandleAsync(message); }
            catch (Exception error) { callbackError = error; }
        };
        link.ChannelFailed += controller.Disconnected;
        using var heartbeatLifetime = new CancellationTokenSource();
        Task heartbeat = Task.CompletedTask;
        SseReader? events = null;
        try
        {
            link.Start();
            var hello = await link.Hello.WaitAsync(TimeSpan.FromSeconds(5));
            var desktop = new Uri(hello.DesktopUrl);
            var desktopToken = desktop.Query.Split('=', 2)[1];
            handler.CookieContainer.Add(origin, new Cookie("fi_session", desktopToken));
            var state = await GetJsonAsync(phone, "/api/state");
            var pin = state.GetProperty("pairing").GetProperty("pin").GetString();
            var pairing = await PostAsync(phone, "/api/pairing/pin", new { pin });
            Check.Equal(200, pairing.Status, "PIN pairing failed");
            events = await SseReader.OpenAsync(origin, handler.CookieContainer);
            const string nonce = "0123456789abcdef0123456789abcdef";
            var request = await PostAsync(phone, "/api/realtime/request", new { client_nonce = nonce });
            Check.Equal(200, request.Status, "realtime request failed");
            var requestId = request.Json.GetProperty("request_id").GetString()!;
            Check.Equal(2, request.Json.GetProperty("check_code").GetString()!.Length);
            var claim = new HttpResult(0, default);
            await Check.EventuallyAsync(() => controller.State == RealtimeState.ARMED, "native grant not produced");
            for (var attempt = 0; attempt < 100; attempt++)
            {
                claim = await PostAsync(phone, "/api/realtime/claim", new { request_id = requestId, client_nonce = nonce });
                if (claim.Status == 200) break;
                Check.Equal("approval_pending", claim.Json.GetProperty("code").GetString(), "unexpected claim failure");
                await Task.Delay(5);
            }
            Check.Equal(200, claim.Status, "claim did not observe grant");
            var capability = claim.Json.GetProperty("capability").GetString()!;
            heartbeat = PhoneHeartbeatsAsync(phone, requestId, capability, heartbeatLifetime.Token);
            var target = new TargetCandidate("memory-fixture", 123, 456, new LocalRect(100, 100, 400, 30),
                new LocalRect(0, 0, 1920, 1040));
            picker.Pick(target);
            await Check.EventuallyAsync(() => controller.State == RealtimeState.BINDING, "target did not enter binding");
            var epoch = controller.TargetEpoch!;
            var observedApplied = new HashSet<int>();
            await WaitPhaseAsync(phone, "BINDING");
            var confirm = await PostAsync(phone, "/api/realtime/heartbeat",
                new { request_id = requestId, target_epoch = epoch, confirm_empty = true }, capability);
            Check.Equal(200, confirm.Status, "empty confirmation rejected");
            await Check.EventuallyAsync(() => controller.State == RealtimeState.ACTIVE, "native active not produced");
            await WaitPhaseAsync(phone, "ACTIVE");

            var gap = await PostAsync(phone, "/api/realtime/states", States(requestId, epoch, 2), capability);
            Check.Equal(409, gap.Status, "out-of-order state accepted");
            Check.Equal("sequence_gap", gap.Json.GetProperty("code").GetString());
            Check.Equal(0, adapter.Fake.ApplyCount, "gap reached adapter");
            for (var seq = 1; seq <= 100; seq++)
            {
                adapter.Start(seq);
                var result = await PostAsync(phone, "/api/realtime/states", States(requestId, epoch, seq), capability);
                Check.Equal(200, result.Status, "state POST rejected");
                await Check.EventuallyAsync(() => adapter.Fake.ApplyCount == seq, "adapter failed continuous sequence");
                await WaitAppliedAsync(phone, seq);
                if (seq == 10)
                {
                    var duplicate = await PostAsync(phone, "/api/realtime/states", States(requestId, epoch, seq), capability);
                    Check.Equal(200, duplicate.Status, "duplicate state rejected");
                    await Task.Delay(30);
                    Check.Equal(seq, adapter.Fake.ApplyCount, "duplicate reapplied");
                }
                if (seq == 50)
                {
                    await Check.EventuallyAsync(() => events.AppliedSequences.Contains(50), "SSE missing first connection result");
                    var cursor = events.LastEventId;
                    await events.DisposeAsync();
                    observedApplied.UnionWith(events.AppliedSequences);
                    events = await SseReader.OpenAsync(origin, handler.CookieContainer, cursor);
                    var status = await GetJsonAsync(phone, "/api/realtime/status");
                    Check.Equal(50L, status.GetProperty("applied_seq").GetInt64(), "reconnect changed sequence");
                    var resend = await PostAsync(phone, "/api/realtime/states", States(requestId, epoch, seq), capability);
                    Check.Equal(200, resend.Status);
                    Check.Equal(seq, adapter.Fake.ApplyCount, "reconnect duplicate reapplied");
                }
                await Task.Delay(18);
            }
            await Check.EventuallyAsync(() => events.AppliedSequences.Contains(100), "SSE missing final verified result");
            observedApplied.UnionWith(events.AppliedSequences);
            Check.Equal(100, observedApplied.Count, "SSE verified result sequence missing");
            Check.Equal(new string('x', 100), adapter.Fake.Text);
            Check.Equal(100, adapter.Fake.ApplyCount);
            Check.Equal(1, ui.ApprovalCount, "unexpected duplicate desktop approval");
            var enterId = "ABCDEF0123456789ABCDEF0123456789";
            object EnterBody(int hold) => new { v = 1, request_id = requestId, target_epoch = epoch,
                enter_id = enterId, after_seq = 100, hold_ms = hold };
            var unavailable = await PostAsync(phone, "/api/realtime/enter", EnterBody(600), capability);
            Check.Equal(409, unavailable.Status); Check.Equal("enter_disabled", unavailable.Json.GetProperty("code").GetString());
            Check.Equal(0, adapter.Fake.EnterCount);
            controller.SetVirtualEnterEnabled(true);
            var ready = false;
            for (var i = 0; i < 100; i++)
            {
                var snapshot = await GetJsonAsync(phone, "/api/realtime/status");
                if (snapshot.GetProperty("enter_available").GetBoolean()) { ready = true; break; }
                await Task.Delay(5);
            }
            Check.True(ready, "native Enter capability did not reach Rust");
            var shortHold = await PostAsync(phone, "/api/realtime/enter", EnterBody(599), capability);
            Check.Equal(422, shortHold.Status); Check.Equal("invalid_hold", shortHold.Json.GetProperty("code").GetString());
            Check.Equal(0, adapter.Fake.EnterCount);
            var entered = await PostAsync(phone, "/api/realtime/enter", EnterBody(600), capability);
            Check.Equal(200, entered.Status);
            await Check.EventuallyAsync(() => adapter.Fake.EnterCount == 1, "Enter did not reach fake adapter");
            await WaitPhaseAsync(phone, "PAUSED");
            var paused = await GetJsonAsync(phone, "/api/realtime/status");
            Check.Equal("applied", paused.GetProperty("enter").GetProperty("status").GetString());
            Check.Equal(enterId, paused.GetProperty("enter").GetProperty("enter_id").GetString());
            Check.Equal("enter_sent", paused.GetProperty("reason").GetString());
            Check.Equal("fixture", paused.GetProperty("target_app").GetString());
            Check.True(!paused.GetProperty("enter_available").GetBoolean());
            var enterDuplicate = await PostAsync(phone, "/api/realtime/enter", EnterBody(600), capability);
            Check.Equal(200, enterDuplicate.Status); Check.Equal(1, adapter.Fake.EnterCount);
            Check.Equal(RealtimeState.PAUSED, controller.State);
            var afterEnter = await PostAsync(phone, "/api/realtime/states", States(requestId, epoch, 101), capability);
            Check.Equal(409, afterEnter.Status); Check.Equal(100, adapter.Fake.ApplyCount);
            Console.WriteLine("E2E ENTER disabled=0 short_hold=0 dispatched=1 duplicates=0 after_enter_text=0 phase=PAUSED uppercase_id=preserved");
            var beforeStop = adapter.Fake.ApplyCount;
            var stop = Stopwatch.StartNew();
            controller.EmergencyStop();
            var localStopMilliseconds = stop.Elapsed.TotalMilliseconds;
            Check.Equal(RealtimeState.REVOKED, controller.State, "stop did not revoke synchronously");
            Check.Equal(0, controller.PendingCount, "stop did not clear pending queue");
            var stopped = await PostAsync(phone, "/api/realtime/states", States(requestId, epoch, 101), capability);
            Check.True(stopped.Status is 403 or 409, "post-stop state accepted");
            await Task.Delay(100);
            var afterStop = adapter.Fake.ApplyCount - beforeStop;
            Check.Equal(0, afterStop, "new adapter operation after stop");
            Check.True(callbackError is null, "controller event callback failed");
            var latencies = adapter.Latencies.Order().ToArray();
            Check.Equal(100, latencies.Length, "latency samples missing");
            Console.WriteLine($"E2E METRICS samples={latencies.Length} p50_ms={Percentile(latencies, .50):F3} p95_ms={Percentile(latencies, .95):F3} applied=100 duplicates=0 gap_operations=0 after_stop={afterStop} stop_local_ms={localStopMilliseconds:F3}");
            heartbeatLifetime.Cancel();
            await heartbeat;
            await events.DisposeAsync();
            events = null;
            process.StandardInput.Close();
            await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
            Check.Equal(0, process.ExitCode, "native EOF did not shut down server cleanly");
            Console.WriteLine("E2E CHAIN request grant claim binding confirm_empty active states apply FakeAdapter verified_readback result SSE; SSE reconnect+status+duplicate; capabilities enter press_enter FakeAdapter enter_result PAUSED; stdin EOF exits");
        }
        finally
        {
            heartbeatLifetime.Cancel();
            try { await heartbeat; } catch (OperationCanceledException) { }
            if (events is not null) await events.DisposeAsync();
            if (!process.HasExited) { process.Kill(entireProcessTree: true); await process.WaitForExitAsync(); }
            await errors;
            // The isolated directory is intentionally retained under obj for reproducible inspection.
        }
    }

    private static object States(string requestId, string epoch, int seq) => new
    {
        request_id = requestId, target_epoch = epoch,
        states = new[] { new { seq, prev_seq = seq - 1, text = new string('x', seq), selection_start = seq,
            selection_end = seq, selection_direction = "none", composition = "committed" } }
    };

    private static async Task PhoneHeartbeatsAsync(HttpClient phone, string requestId, string capability, CancellationToken token)
    {
        try
        {
            while (!token.IsCancellationRequested)
            {
                await PostAsync(phone, "/api/realtime/heartbeat", new { request_id = requestId }, capability);
                await Task.Delay(400, token);
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
    }

    private static async Task WaitPhaseAsync(HttpClient phone, string phase)
    {
        for (var i = 0; i < 200; i++)
        {
            if ((await GetJsonAsync(phone, "/api/realtime/status")).GetProperty("phase").GetString() == phase) return;
            await Task.Delay(5);
        }
        throw new InvalidOperationException("Rust phase did not match native phase");
    }

    private static async Task WaitAppliedAsync(HttpClient phone, int seq)
    {
        for (var i = 0; i < 200; i++)
        {
            var json = await GetJsonAsync(phone, "/api/realtime/status");
            if (json.GetProperty("applied_seq").GetInt64() == seq)
            {
                Check.Equal("verified_readback", json.GetProperty("proof").GetString());
                return;
            }
            await Task.Delay(5);
        }
        throw new InvalidOperationException("Rust did not acknowledge verified readback");
    }

    private sealed record HttpResult(int Status, JsonElement Json);
    private static async Task<HttpResult> PostAsync(HttpClient client, string path, object body, string? capability = null)
    {
        using var message = new HttpRequestMessage(HttpMethod.Post, path);
        message.Headers.Add("Origin", client.BaseAddress!.GetLeftPart(UriPartial.Authority));
        if (capability is not null) message.Headers.Add("X-FI-Realtime", capability);
        message.Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");
        HttpResponseMessage response;
        try { response = await client.SendAsync(message); }
        catch (Exception e) { throw new InvalidOperationException("POST " + path + " failed: " + e.GetType().Name + " / " + e.InnerException?.GetType().Name + " " + e.InnerException?.Message, e); }
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return new((int)response.StatusCode, document.RootElement.Clone());
    }

    private static async Task<JsonElement> GetJsonAsync(HttpClient client, string path)
    {
        using var response = await client.GetAsync(path);
        Check.Equal(HttpStatusCode.OK, response.StatusCode, "GET status failed");
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return document.RootElement.Clone();
    }

    private static async Task DrainAsync(StreamReader stderr)
    {
        var buffer = new char[1024];
        while (await stderr.ReadAsync(buffer) != 0) { }
    }

    private static double Percentile(double[] sorted, double p) => sorted[(int)Math.Ceiling(sorted.Length * p) - 1];
    private static int FreePort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    private static string FindRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Cargo.toml"))) directory = directory.Parent;
        return directory?.FullName ?? throw new InvalidOperationException("workspace root not found");
    }

    private sealed class ApprovedUi : IApprovalUi
    {
        public int ApprovalCount { get; private set; }
        public Task<bool> RequestApprovalAsync(RequestMessage request, CancellationToken cancellationToken)
        {
            ApprovalCount++;
            return Task.FromResult(true);
        }
        public void CloseApproval() { }
        public void ShowState(RealtimeView view) { }
    }

    private sealed class TimedAdapter : ITargetAdapter, IVirtualEnterAdapter
    {
        public FakeAdapter Fake { get; } = new();
        private readonly ConcurrentDictionary<int, long> _starts = new();
        public ConcurrentBag<double> Latencies { get; } = [];
        public void Start(int seq) => _starts[seq] = Stopwatch.GetTimestamp();
        public AdapterProbe Probe(TargetCandidate target) => Fake.Probe(target);
        public Task<TargetReadBack> BindAsync(TargetCandidate target, CancellationToken token) => Fake.BindAsync(target, token);
        public Task<TargetReadBack> ReadBackAsync(TargetCandidate target, CancellationToken token) => Fake.ReadBackAsync(target, token);
        public Task<AdapterOutcome> PressEnterAsync(TargetCandidate target, Func<bool> mayApply, CancellationToken token)
            => Fake.PressEnterAsync(target, mayApply, token);
        public async Task<AdapterOutcome> ApplyMinimalEditAsync(TargetCandidate target, MinimalEdit edit,
            TextSelection selection, Func<bool> mayApply, CancellationToken token)
        {
            var result = await Fake.ApplyMinimalEditAsync(target, edit, selection, mayApply, token);
            var completed = Stopwatch.GetTimestamp();
            if (_starts.TryRemove(Fake.ApplyCount, out var started)) Latencies.Add(Stopwatch.GetElapsedTime(started, completed).TotalMilliseconds);
            return result;
        }
    }

    private sealed class SseReader : IAsyncDisposable
    {
        private readonly HttpClient _client;
        private readonly HttpResponseMessage _response;
        private readonly CancellationTokenSource _lifetime = new();
        private readonly Task _read;
        private long _lastEventId;
        public long LastEventId => Interlocked.Read(ref _lastEventId);
        public ConcurrentBag<int> AppliedSequences { get; } = [];
        private SseReader(HttpClient client, HttpResponseMessage response)
        {
            _client = client;
            _response = response;
            _read = ReadAsync();
        }

        public static async Task<SseReader> OpenAsync(Uri origin, CookieContainer cookies, long? cursor = null)
        {
            var client = new HttpClient(new HttpClientHandler { CookieContainer = cookies, UseProxy = false })
                { BaseAddress = origin, Timeout = Timeout.InfiniteTimeSpan };
            var message = new HttpRequestMessage(HttpMethod.Get, "/api/events");
            if (cursor is not null) message.Headers.Add("Last-Event-ID", cursor.Value.ToString());
            var response = await client.SendAsync(message, HttpCompletionOption.ResponseHeadersRead);
            Check.Equal(HttpStatusCode.OK, response.StatusCode, "SSE connection failed");
            message.Dispose();
            return new SseReader(client, response);
        }

        private async Task ReadAsync()
        {
            try
            {
                using var reader = new StreamReader(await _response.Content.ReadAsStreamAsync(_lifetime.Token));
                var realtime = false;
                while (await reader.ReadLineAsync(_lifetime.Token) is { } line)
                {
                    if (line.Length == 0) { realtime = false; continue; }
                    if (line == "event: realtime") realtime = true;
                    if (realtime && line.StartsWith("id: ") && long.TryParse(line.AsSpan(4), out var id)) Interlocked.Exchange(ref _lastEventId, id);
                    if (!realtime || !line.StartsWith("data: ")) continue;
                    using var document = JsonDocument.Parse(line[6..]);
                    var json = document.RootElement;
                    if (json.TryGetProperty("seq_status", out var status) && status.GetString() == "applied"
                        && json.TryGetProperty("seq", out var seq))
                    {
                        Check.Equal("verified_readback", json.GetProperty("proof").GetString(), "SSE applied lacks proof");
                        AppliedSequences.Add(seq.GetInt32());
                    }
                }
            }
            catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { }
            catch (IOException) when (_lifetime.IsCancellationRequested) { }
            catch (ObjectDisposedException) when (_lifetime.IsCancellationRequested) { }
        }

        public async ValueTask DisposeAsync()
        {
            _lifetime.Cancel();
            _response.Dispose();
            _client.Dispose();
            await _read;
            _lifetime.Dispose();
        }
    }
}
