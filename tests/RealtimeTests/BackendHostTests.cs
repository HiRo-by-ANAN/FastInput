using System.Net;
using System.Net.Sockets;
using FastInput.Windows;
using FastInput.Windows.Realtime;

namespace FastInput.RealtimeTests;

internal static class BackendHostTests
{
    public static void Register(TestSuite suite)
    {
        suite.Add("host.native_hello_and_heartbeat", () => RunAsync(native: true));
        suite.Add("host.legacy_no_realtime_fallback", () => RunAsync(native: false));
    }

    private static async Task RunAsync(bool native)
    {
        var root = FindRoot();
        var binary = Environment.GetEnvironmentVariable("FASTINPUT_TEST_SERVER") ?? Path.Combine(root, "target-p10", "debug", "fastinput.exe");
        Check.True(File.Exists(binary), "real Rust binary missing for BackendHost integration");
        File.Copy(binary, Path.Combine(AppContext.BaseDirectory, "fastinput-server.exe"), overwrite: true);
        var data = Path.Combine(root, "tests", "RealtimeTests", "obj", "host-" + Guid.NewGuid().ToString("N"));
        var previousData = Environment.GetEnvironmentVariable("FASTINPUT_DATA_DIR");
        Environment.SetEnvironmentVariable("FASTINPUT_DATA_DIR", data);
        try
        {
            using var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            var port = ((IPEndPoint)listener.LocalEndpoint).Port;
            listener.Stop();
            using var host = new BackendHost();
            var created = 0;
            var hello = 0;
            host.BridgeCreated += bridge =>
            {
                created++;
                bridge.MessageReceived += message => { if (message is HelloMessage) Interlocked.Increment(ref hello); };
            };
            var args = new List<string> { "--lan-ip", "127.0.0.1", "--port", port.ToString() };
            if (!native) args.Add("--no-realtime");
            await host.StartAsync(args.ToArray(), CancellationToken.None);
            Check.True(host.IsRunning);
            Check.Equal(port, host.DesktopUrl.Port);
            Check.True(FrameCodec.ValidDesktopUrl(host.DesktopUrl.AbsoluteUri));
            Check.Equal(native ? 1 : 0, created);
            if (native)
            {
                await Check.EventuallyAsync(() => Volatile.Read(ref hello) == 1, "hello was not delivered");
                // Cross the 3-second Rust native liveness deadline without HTTP activity.
                await Task.Delay(3200);
                Check.True(host.Bridge!.IsHealthy && host.IsRunning, "native heartbeat did not maintain the owned link");
            }
            else Check.True(host.Bridge is null, "legacy mode exposed a native link");
            await host.StopAsync();
            Check.True(!host.IsRunning);
            Check.Equal<int?>(0, host.ExitCode, "backend did not shut down gracefully");
        }
        finally
        {
            Environment.SetEnvironmentVariable("FASTINPUT_DATA_DIR", previousData);
            var allowed = Path.GetFullPath(Path.Combine(root, "tests", "RealtimeTests", "obj")) + Path.DirectorySeparatorChar;
            var resolved = Path.GetFullPath(data);
            Check.True(resolved.StartsWith(allowed, StringComparison.OrdinalIgnoreCase), "test cleanup path escaped workspace");
            if (Directory.Exists(resolved)) Directory.Delete(resolved, recursive: true);
        }
    }

    private static string FindRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "Cargo.toml"))) return directory.FullName;
            directory = directory.Parent;
        }
        throw new InvalidOperationException("workspace root missing");
    }
}
