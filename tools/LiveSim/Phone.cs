using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
namespace FastInput.LiveSim;
internal static class Net
{
    public static int FreePort() { var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start(); var port = ((IPEndPoint)listener.LocalEndpoint).Port; listener.Stop(); return port; }
}
internal sealed class Phone : IDisposable
{
    private HttpClient _client;
    private readonly CookieContainer _cookies = new();
    private readonly Uri _origin;
    private CancellationTokenSource? _heartbeats;
    private Task _heartbeatTask = Task.CompletedTask;
    public string RequestId { get; private set; } = "";
    public string Capability { get; private set; } = "";
    public string Epoch { get; private set; } = "";
    public Phone(string desktop)
    {
        var uri = new Uri(desktop); _origin = new Uri(uri.GetLeftPart(UriPartial.Authority));
        if (uri.Host != "127.0.0.1") throw new InvalidOperationException("LiveSim only connects to 127.0.0.1");
        _cookies.Add(_origin, new Cookie("fi_session", uri.Query.Split('=', 2)[1])); _client = Client();
    }
    private HttpClient Client() => new(new HttpClientHandler { CookieContainer = _cookies, UseProxy = false }) { BaseAddress = _origin, Timeout = TimeSpan.FromSeconds(6) };
    public async Task<JsonElement> GetAsync(string path)
    {
        using var response = await _client.GetAsync(path); response.EnsureSuccessStatusCode(); using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync()); return json.RootElement.Clone();
    }
    public async Task<(int Status, JsonElement Json)> PostAsync(string path, object body, bool capability = true)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, path) { Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json") };
        request.Headers.Add("Origin", _origin.AbsoluteUri.TrimEnd('/')); if (capability && Capability.Length > 0) request.Headers.Add("X-FI-Realtime", Capability);
        using var response = await _client.SendAsync(request); using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync()); return ((int)response.StatusCode, json.RootElement.Clone());
    }
    public async Task BeginAsync()
    {
        var pin = (await GetAsync("/api/state")).GetProperty("pairing").GetProperty("pin").GetString();
        Ensure(await PostAsync("/api/pairing/pin", new { pin }, false));
        var nonce = Guid.NewGuid().ToString("N");
        var request = Ensure(await PostAsync("/api/realtime/request", new { client_nonce = nonce }, false)); RequestId = request.GetProperty("request_id").GetString()!;
        for (var n = 0; n < 200; n++)
        {
            var claim = await PostAsync("/api/realtime/claim", new { request_id = RequestId, client_nonce = nonce }, false);
            if (claim.Status == 200) { Capability = claim.Json.GetProperty("capability").GetString()!; break; }
            await Task.Delay(20);
        }
        if (Capability.Length == 0) throw new TimeoutException("test host approval/claim");
        StartHeartbeats();
    }
    public async Task BindAsync(int timeout = 10000)
    {
        var status = await WaitStatusAsync(j => j.GetProperty("phase").GetString() == "BINDING", timeout); Epoch = status.GetProperty("target_epoch").GetString()!;
        Ensure(await PostAsync("/api/realtime/heartbeat", new { request_id = RequestId, target_epoch = Epoch, confirm_empty = true }));
        await WaitStatusAsync(j => j.GetProperty("phase").GetString() == "ACTIVE");
    }
    public object State(int seq, string text) => new { request_id = RequestId, target_epoch = Epoch, states = new[] { new { seq, prev_seq = seq - 1, text, selection_start = text.Length, selection_end = text.Length, selection_direction = "none", composition = "committed" } } };
    public object Enter(string enterId, int afterSeq, int holdMs) => new { v = 1, request_id = RequestId, target_epoch = Epoch, enter_id = enterId, after_seq = afterSeq, hold_ms = holdMs };
    // Intent is sent once after a completed hold. Heartbeats/status never call this method.
    public async Task<(int Status, JsonElement Json)> HoldEnterAsync(string enterId, int afterSeq, int holdMs = 640)
    {
        await Task.Delay(holdMs);
        return await PostAsync("/api/realtime/enter", Enter(enterId, afterSeq, holdMs));
    }
    public async Task<double> SendAsync(int seq, string text, int delay)
    {
        var started = Stopwatch.GetTimestamp(); if (delay > 0) await Task.Delay(delay);
        Ensure(await PostAsync("/api/realtime/states", State(seq, text)));
        await WaitStatusAsync(j => j.GetProperty("applied_seq").GetInt32() == seq && j.GetProperty("proof").GetString() == "verified_readback");
        return Stopwatch.GetElapsedTime(started).TotalMilliseconds;
    }
    public async Task<JsonElement> WaitStatusAsync(Func<JsonElement, bool> check, int timeout = 10000)
    {
        var watch = Stopwatch.StartNew(); JsonElement last = default;
        while (watch.ElapsedMilliseconds < timeout) { last = await GetAsync("/api/realtime/status"); if (check(last)) return last; await Task.Delay(15); }
        throw new TimeoutException("protocol state: " + (last.ValueKind == JsonValueKind.Object && last.TryGetProperty("phase", out var phase) ? phase.GetString() : "missing") + " " + last.ToString());
    }
    public async Task ReconnectAsync()
    {
        // Drop and recreate the phone HTTP transport; the session and capability remain in memory.
        await StopHeartbeatsAsync(); _client.Dispose(); _client = Client(); _ = await GetAsync("/api/realtime/status"); StartHeartbeats();
    }
    public async Task TimeoutDisconnectAsync()
    {
        await StopHeartbeatsAsync(); await Task.Delay(3500);
        await WaitStatusAsync(j => j.GetProperty("phase").GetString() is "REVOKED" or "UNKNOWN");
    }
    private void StartHeartbeats()
    {
        _heartbeats = new(); var token = _heartbeats.Token;
        _heartbeatTask = Task.Run(async () => { try { while (!token.IsCancellationRequested) { await PostAsync("/api/realtime/heartbeat", new { request_id = RequestId }); await Task.Delay(700, token); } } catch (OperationCanceledException) when (token.IsCancellationRequested) { } });
    }
    private async Task StopHeartbeatsAsync() { _heartbeats?.Cancel(); await _heartbeatTask; _heartbeats?.Dispose(); _heartbeats = null; }
    public async Task ReleaseAsync() { await StopHeartbeatsAsync(); if (Capability.Length > 0) await PostAsync("/api/realtime/release", new { request_id = RequestId }); }
    public static JsonElement Ensure((int Status, JsonElement Json) result) { if (result.Status != 200) throw new IOException("HTTP " + result.Status + " " + (result.Json.TryGetProperty("code", out var code) ? code.GetString() : "error")); return result.Json; }
    public void Dispose() { _heartbeats?.Cancel(); try { _heartbeatTask.GetAwaiter().GetResult(); } catch (Exception) { } _heartbeats?.Dispose(); _client.Dispose(); }
}
