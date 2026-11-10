using System.Diagnostics;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using FastInput.Windows.Realtime;

namespace FastInput.Windows;

internal sealed class BackendHost : IDisposable
{
    private Process? _server;
    private HttpClient? _client;
    private Uri? _origin;

    public bool IsRunning => _server is { HasExited: false };
    public Uri DesktopUrl { get; private set; } = null!;
    public int? ExitCode => _server is { HasExited: true } ? _server.ExitCode : null;
    public NativeBridgeClient? Bridge { get; private set; }
    public event Action<NativeBridgeClient>? BridgeCreated;

    public async Task StartAsync(string[] args, CancellationToken cancellationToken)
    {
        var directory = AppContext.BaseDirectory;
        var executable = Path.Combine(directory, "fastinput-server.exe");
        if (!File.Exists(executable))
            throw new FileNotFoundException(Loc.T("backendhost.cannot.find.fastinput.server.exe.extract.the.complete.package"), executable);

        var realtime = !args.Contains("--no-realtime", StringComparer.Ordinal);
        var start = new ProcessStartInfo(executable)
        {
            WorkingDirectory = directory,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = realtime
        };
        foreach (var argument in args.Where(value => value is not ("--no-realtime" or "--native-stdio")))
            start.ArgumentList.Add(argument);
        start.ArgumentList.Add("--no-browser");
        if (realtime) start.ArgumentList.Add("--native-stdio");

        _server = new Process { StartInfo = start };
        if (!_server.Start()) throw new InvalidOperationException(Loc.T("backendhost.cannot.start.the.local.server"));

        // Never retain peer diagnostics. They could contain credentials or text.
        var errorStream = _server.StandardError.BaseStream;
        _ = Task.Run(async () =>
        {
            var bytes = new byte[4096];
            try
            {
                while (await errorStream.ReadAsync(bytes) != 0) Array.Clear(bytes);
            }
            catch (ObjectDisposedException) { }
            catch (IOException) { }
            finally { Array.Clear(bytes); }
        }, CancellationToken.None);

        Uri? desktopUrl;
        try
        {
            if (realtime)
            {
                Bridge = new NativeBridgeClient(_server.StandardOutput.BaseStream, _server.StandardInput.BaseStream);
                BridgeCreated?.Invoke(Bridge);
                Bridge.Start();
                var hello = await Bridge.Hello.WaitAsync(TimeSpan.FromSeconds(12), cancellationToken);
                desktopUrl = new Uri(hello.DesktopUrl);
            }
            else
            {
                desktopUrl = await Task.Run(() =>
                {
                    string? line;
                    while ((line = _server.StandardOutput.ReadLine()) is not null)
                    {
                        if (line.StartsWith("Computer: ", StringComparison.Ordinal)
                            && Uri.TryCreate(line[10..].Trim(), UriKind.Absolute, out var found))
                            return found;
                    }
                    return null;
                }, CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(12), cancellationToken);
            }
        }
        catch
        {
            Bridge?.Dispose();
            KillServer();
            throw;
        }

        if (desktopUrl is null)
        {
            KillServer();
            throw new InvalidOperationException(Loc.T("backendhost.the.local.server.could.not.start.check.whether.another"));
        }

        var keyMatch = Regex.Match(desktopUrl.Query, @"^\?key=([0-9a-fA-F]{64})$");
        if (!FrameCodec.ValidDesktopUrl(desktopUrl.AbsoluteUri) || !keyMatch.Success)
        {
            KillServer();
            throw new InvalidOperationException(Loc.T("backendhost.the.local.server.returned.invalid.desktop.authentication.information"));
        }

        _origin = new Uri(desktopUrl.GetLeftPart(UriPartial.Authority));
        DesktopUrl = desktopUrl;
        _client = new HttpClient(new HttpClientHandler
        {
            AllowAutoRedirect = false,
            UseCookies = false,
            UseProxy = false
        })
        {
            BaseAddress = _origin,
            Timeout = TimeSpan.FromSeconds(6)
        };
        if (!_client.DefaultRequestHeaders.TryAddWithoutValidation("Cookie", $"fi_session={keyMatch.Groups[1].Value}"))
            throw new InvalidOperationException(Loc.T("backendhost.cannot.establish.an.authenticated.desktop.connection"));
        _client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
    }

    public async Task PostActionAsync(string path, object body, CancellationToken cancellationToken)
    {
        using var response = await SendJsonAsync(path, body, cancellationToken);
        response.EnsureSuccessStatusCode();
    }

    private async Task<HttpResponseMessage> SendJsonAsync(string path, object body, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, path)
        {
            Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json")
        };
        if (!request.Headers.TryAddWithoutValidation("Origin", _origin!.GetLeftPart(UriPartial.Authority)))
            throw new InvalidOperationException(Loc.T("backendhost.cannot.add.local.origin.verification.information"));
        return await Client.SendAsync(request, cancellationToken);
    }

    public async Task StopAsync()
    {
        if (Bridge is { IsHealthy: true })
        {
            Bridge.ClearPending();
            Bridge.TrySend(new RevokeCommand(null, "released"));
        }
        if (_server is null) return;
        if (!_server.HasExited && _client is not null)
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(1));
            try { await PostActionAsync("/api/shutdown", new { }, timeout.Token); }
            catch (Exception) { /* The owned process is stopped below. */ }
        }
        if (!_server.HasExited)
        {
            try { await _server.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(2)); }
            catch (TimeoutException)
            {
                KillServer();
                await _server.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(2));
            }
        }
    }

    private HttpClient Client => _client ?? throw new InvalidOperationException(Loc.T("backendhost.the.local.server.has.not.started"));

    private void KillServer()
    {
        try
        {
            if (_server is { HasExited: false }) _server.Kill(entireProcessTree: true);
        }
        catch (InvalidOperationException) { }
        catch (System.ComponentModel.Win32Exception) { }
    }

    public void Dispose()
    {
        Bridge?.Dispose();
        KillServer();
        _client?.Dispose();
        _server?.Dispose();
    }
}

