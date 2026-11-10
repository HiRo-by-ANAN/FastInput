using System.Diagnostics;
using System.Text.Json;
namespace FastInput.LiveSim;
internal static class Program
{
    [STAThread] private static int Main(string[] args)
    {
        try
        {
            if (args.Length == 0 || args[0] is "help" or "--help") { Console.WriteLine("LiveSim all|fixture|chrome|blacklist|enter [--case disabled|enabled|blacklist] [--server PATH] [--output DIR] [--delay-ms N] [--physical-clicks]\nInternal child modes require FASTINPUT_LIVESIM=1."); return 0; }
            if (args[0] is "--fixture" or "--host" or "--host-physical")
            {
                if (Environment.GetEnvironmentVariable("FASTINPUT_LIVESIM") != "1") throw new InvalidOperationException("Internal test host requires FASTINPUT_LIVESIM=1; never enabled in the production shell.");
                Application.SetHighDpiMode(HighDpiMode.PerMonitorV2); Application.EnableVisualStyles();
                if (args[0] == "--fixture") Application.Run(new FixtureWindow(args[1]));
                else TestHost.Run(args[1], args[2], args[0] == "--host-physical");
                return 0;
            }
            return Runner.RunAsync(args).GetAwaiter().GetResult();
        }
        catch (Exception e) { Console.Error.WriteLine(e.GetType().Name + ": " + e.Message); return 1; }
    }
    internal static Process Child(params string[] args)
    {
        var start = new ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = false, RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true };
        foreach (var a in args) start.ArgumentList.Add(a);
        start.Environment["FASTINPUT_LIVESIM"] = "1";
        var child = Process.Start(start) ?? throw new IOException("child start failed");
        _ = Task.Run(async () => { while (await child.StandardError.ReadLineAsync() is not null) { } });
        return child;
    }
    internal static void AtomicJson(string path, object value)
    {
        var temp = path + ".tmp"; File.WriteAllText(temp, JsonSerializer.Serialize(value)); File.Move(temp, path, true);
    }
    internal static async Task WaitAsync(Func<bool> predicate, string reason, int timeout = 10000)
    {
        var watch = Stopwatch.StartNew();
        while (watch.ElapsedMilliseconds < timeout) { if (predicate()) return; await Task.Delay(20); }
        throw new TimeoutException(reason);
    }
    internal static async Task StopAsync(Process process)
    {
        if (process.HasExited) return;
        try { await process.StandardInput.WriteLineAsync("close"); await process.StandardInput.FlushAsync(); await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(3)); }
        catch (Exception) { if (!process.HasExited) { process.Kill(entireProcessTree: true); await process.WaitForExitAsync(); } }
    }
}
