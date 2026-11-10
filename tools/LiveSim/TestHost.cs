using System.Diagnostics;
using System.Runtime.InteropServices;
using FastInput.Windows.Realtime;
namespace FastInput.LiveSim;
internal static class TestHost
{
    public static void Run(string server, string data, bool physicalClicks)
    {
        using var form = new HostForm { Text = "LiveSim TEST HOST — auto approval", Size = new Size(380, 100), StartPosition = FormStartPosition.Manual, Location = new Point(0, 0) };
        form.Shown += async (_, _) =>
        {
            var start = new ProcessStartInfo(server) { UseShellExecute = false, RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true };
            foreach (var arg in new[] { "--native-stdio", "--no-browser", "--lan-ip", "127.0.0.1", "--port", Net.FreePort().ToString() }) start.ArgumentList.Add(arg);
            start.Environment["FASTINPUT_DATA_DIR"] = data;
            using var backend = Process.Start(start) ?? throw new IOException("backend start failed");
            _ = Task.Run(async () => { while (await backend.StandardError.ReadLineAsync() is not null) { } });
            using var link = new NativeBridgeClient(backend.StandardOutput.BaseStream, backend.StandardInput.BaseStream);
            using var physical = new LocalPhysicalInputMonitor();
            ITargetPicker picker = physicalClicks ? new RealTargetPicker() : new TestPicker(physical, form);
            using var pickerLifetime = (IDisposable)picker;
            // Subscribe before the controller: monitoring begins after capture and before binding.
            if (picker is RealTargetPicker realPicker)
            {
                realPicker.PickingStarted += physical.Stop;
                realPicker.TargetPicked += _ => physical.Start();
            }
            using var controller = new RealtimeController(link, new RealtimeClock(), new AutoApproval(), picker, new ObservedAdapter(), new WindowsSafetyProbe(t => physical.HasPendingInput ? new(PhysicalInputPending: true) : WindowsSafetyProbe.ReadWindowFacts(t)), new RealtimeSettings { RealtimeEnabled = true }, new NullAuditLog());
            physical.PhysicalInput += () => { if (controller.IsMonitoringTarget) controller.NotifyPhysicalInput(); };
            using var hotkey = new EmergencyHotKey(new WindowsHotKeyApi());
            if (!hotkey.Register(form.Handle)) throw new InvalidOperationException(hotkey.FailureReason);
            form.StopRequested = controller.EmergencyStop;
            controller.SetHotkeyAvailable(true);
            link.MessageReceived += m => _ = controller.HandleAsync(m);
            link.ChannelFailed += controller.Disconnected;
            using var timer = new System.Threading.Timer(_ => controller.Tick(), null, 25, 25);
            try
            {
                link.Start(); var hello = await link.Hello.WaitAsync(TimeSpan.FromSeconds(12));
                Console.WriteLine(System.Text.Json.JsonSerializer.Serialize(new { desktop_url = hello.DesktopUrl, window = (long)form.Handle }));
                await Task.Run(async () =>
                {
                    while (await Console.In.ReadLineAsync() is { } command)
                    {
                        if (command == "stop") controller.EmergencyStop();
                        // Test-host-only local desktop setting. No HTTP request can change it.
                        if (command == "enter-on") controller.SetVirtualEnterEnabled(true);
                        if (command == "enter-off") controller.SetVirtualEnterEnabled(false);
                        if (command == "close") break;
                    }
                });
            }
            catch (Exception e) { Console.Error.WriteLine(e.GetType().Name + ": " + e.Message); }
            finally
            {
                controller.EmergencyStop(); physical.Stop();
                if (!backend.HasExited) { backend.StandardInput.Close(); try { await backend.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(2)); } catch (TimeoutException) { backend.Kill(true); } }
                form.Close();
            }
        };
        Application.Run(form);
    }
    private sealed class HostForm : Form
    {
        public Action? StopRequested;
        protected override void WndProc(ref Message m)
        {
            if (m.Msg == EmergencyHotKey.Message && (int)m.WParam == EmergencyHotKey.Id) { StopRequested?.Invoke(); return; }
            base.WndProc(ref m);
        }
    }
    private sealed class AutoApproval : IApprovalUi
    {
        public Task<bool> RequestApprovalAsync(RequestMessage request, CancellationToken token) => Task.FromResult(Environment.GetEnvironmentVariable("FASTINPUT_LIVESIM") == "1" && !token.IsCancellationRequested);
        public void CloseApproval() { }
        public void ShowState(RealtimeView view) { }
    }
    // The formal picker rejects ALL injected mouse events. This test-only picker accepts ONLY
    // LiveSim's foreign marker, after a real SendInput hit; it cannot be linked into the production shell.
    private sealed class TestPicker(LocalPhysicalInputMonitor physical, Control dispatcher) : ITargetPicker, IDisposable
    {
        private nint _hook; private bool _picking; private readonly Native.HookProc _callback = CallbackPlaceholder;
        private Native.HookProc? _liveCallback;
        public event Action<TargetCandidate>? TargetPicked;
        private static nint CallbackPlaceholder(int c, nint m, nint p) => Native.CallNextHookEx(0, c, m, p);
        public void Start()
        {
            if (dispatcher.InvokeRequired) { dispatcher.Invoke(new Action(Start)); return; }
            _picking = true; physical.Stop();
            if (_hook != 0) return;
            _liveCallback = Hook;
            _hook = Native.SetWindowsHookEx(14, _liveCallback, Native.GetModuleHandle(null), 0);
            if (_hook == 0) throw new IOException("test picker hook installation failed");
        }
        private nint Hook(int code, nint message, nint data)
        {
            if (code >= 0 && message == 0x201 && _picking)
            {
                var mouse = Marshal.PtrToStructure<Native.Mouse>(data);
                if (mouse.Extra == Native.TestMarker)
                {
                    _picking = false;
                    _ = Task.Run(async () =>
                    {
                        await Task.Delay(90);
                        try { var candidate = WindowsTarget.Capture(mouse.Point.X, mouse.Point.Y); physical.Start(); TargetPicked?.Invoke(candidate); }
                        catch (Exception e) { Console.Error.WriteLine("capture: " + e.GetType().Name); }
                    });
                }
            }
            return Native.CallNextHookEx(0, code, message, data);
        }
        public void Stop() { _picking = false; if (_hook != 0) { Native.UnhookWindowsHookEx(_hook); _hook = 0; } }
        public void Dispose() { Stop(); GC.KeepAlive(_callback); GC.KeepAlive(_liveCallback); }
    }
}



