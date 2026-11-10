using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading.Channels;

namespace FastInput.Windows.Realtime;

public static class HookEventPolicy
{
    public static bool IsOwn(bool injected, nuint extra) => injected && extra == NativeInput.Marker;
    public static bool IsPickerClick(bool injected, nuint extra, int message) => message == 0x201 && !injected && extra != NativeInput.Marker;
    public static bool IsTakeover(bool keyboard, int message, bool injected, nuint extra) => !IsOwn(injected, extra)
        && (keyboard ? message is 0x100 or 0x101 or 0x104 or 0x105 : message is 3 or 0x201 or 0x204 or 0x207 or 0x20B or 0x20A or 0x20E);
}

internal readonly record struct HookNotice(bool Keyboard, int Message, bool Injected, nuint Extra, int X, int Y, long Timestamp);

// A dedicated message-loop thread owns the rooted delegates. Callback only copies metadata into a bounded channel.
internal sealed class LowLevelHooks : IDisposable
{
    [StructLayout(LayoutKind.Sequential)] private struct KeyRaw { public uint Vk, Scan, Flags, Time; public nuint Extra; }
    [StructLayout(LayoutKind.Sequential)] private struct MouseRaw { public DesktopNative.Point Point; public uint Data, Flags, Time; public nuint Extra; }
    private delegate nint Callback(int code, nint message, nint data);
    private delegate void WinEventCallback(nint hook, uint kind, nint hwnd, int objectId, int childId, uint thread, uint time);
    private readonly Callback _keyboard, _mouse;
    private readonly WinEventCallback _foreground;
    private nint _keyHook, _mouseHook, _foregroundHook;
    private uint _threadId;
    private readonly Channel<HookNotice> _queue = Channel.CreateBounded<HookNotice>(new BoundedChannelOptions(64)
    { SingleReader = true, SingleWriter = true, FullMode = BoundedChannelFullMode.Wait });
    private readonly Action<HookNotice> _handler;
    private int _overflow, _disposed, _pendingInput;
    public bool Overflowed => Volatile.Read(ref _overflow) != 0;
    public bool PendingInput => Volatile.Read(ref _pendingInput) != 0;
    public LowLevelHooks(bool keyboard, Action<HookNotice> handler)
    {
        _handler = handler;
        _foreground = (_, _, _, _, _, _, _) => Enqueue(new(false, 3, false, 0, 0, 0, Stopwatch.GetTimestamp()));
        _keyboard = (code, message, data) =>
        {
            if (code >= 0)
            {
                var raw = Marshal.PtrToStructure<KeyRaw>(data);
                Enqueue(new(true, (int)message, (raw.Flags & 0x12) != 0, raw.Extra, 0, 0, Stopwatch.GetTimestamp()));
            }
            return CallNextHookEx(0, code, message, data);
        };
        _mouse = (code, message, data) =>
        {
            if (code >= 0)
            {
                var raw = Marshal.PtrToStructure<MouseRaw>(data);
                // No mouse-move flood and no key values are retained.
                if ((int)message != 0x200) Enqueue(new(false, (int)message, (raw.Flags & 3) != 0, raw.Extra, raw.Point.X, raw.Point.Y, Stopwatch.GetTimestamp()));
            }
            return CallNextHookEx(0, code, message, data);
        };
        var ready = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            _threadId = GetCurrentThreadId();
            using var context = new ApplicationContext();
            // Force a thread message queue before publishing readiness (Stop may immediately post WM_QUIT).
            PeekMessage(out _, 0, 0, 0, 0);
            var module = GetModuleHandle(null);
            if (keyboard) _keyHook = SetWindowsHookEx(13, _keyboard, module, 0);
            if (keyboard) _foregroundHook = SetWinEventHook(3, 3, 0, _foreground, 0, 0, 0);
            _mouseHook = SetWindowsHookEx(14, _mouse, module, 0);
            var ok = _mouseHook != 0 && (!keyboard || (_keyHook != 0 && _foregroundHook != 0));
            ready.TrySetResult(ok);
            try { if (ok) Application.Run(context); }
            finally { Uninstall(); _queue.Writer.TryComplete(); }
        }) { IsBackground = true, Name = "FastInput local input hooks" };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        if (!ready.Task.GetAwaiter().GetResult()) { Dispose(); throw new InvalidOperationException("global_hook_install_failed"); }
        _ = Task.Run(async () =>
        {
            await foreach (var notice in _queue.Reader.ReadAllAsync())
            {
                if (Volatile.Read(ref _disposed) != 0) break;
                try { _handler(notice); } catch { Interlocked.Exchange(ref _overflow, 1); }
            }
        });
    }
    private void Enqueue(HookNotice notice)
    {
        if (HookEventPolicy.IsOwn(notice.Injected, notice.Extra)) return;
        if (HookEventPolicy.IsTakeover(notice.Keyboard, notice.Message, notice.Injected, notice.Extra))
            Interlocked.Exchange(ref _pendingInput, 1);
        if (!_queue.Writer.TryWrite(notice)) Interlocked.Exchange(ref _overflow, 1);
    }
    private void Uninstall()
    {
        var key = Interlocked.Exchange(ref _keyHook, 0); if (key != 0) UnhookWindowsHookEx(key);
        var mouse = Interlocked.Exchange(ref _mouseHook, 0); if (mouse != 0) UnhookWindowsHookEx(mouse);
        // WinEvent unhook must run on its installing thread, in the message-loop finally block.
        if (GetCurrentThreadId() == _threadId)
        {
            var foreground = Interlocked.Exchange(ref _foregroundHook, 0); if (foreground != 0) UnhookWinEvent(foreground);
        }
    }
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        Uninstall(); // Immediate unhook; do not wait for a slow UIA consumer.
        PostThreadMessage(_threadId, 0x12, 0, 0);
        _queue.Writer.TryComplete();
        GC.KeepAlive(_keyboard); GC.KeepAlive(_mouse);
        GC.KeepAlive(_foreground);
    }
    [StructLayout(LayoutKind.Sequential)] private struct Msg { public nint H; public uint M; public nuint W; public nint L; public uint T; public DesktopNative.Point P; public uint Private; }
    [DllImport("user32.dll")] private static extern bool PeekMessage(out Msg msg, nint h, uint min, uint max, uint remove);
    [DllImport("user32.dll")] private static extern nint SetWindowsHookEx(int id, Callback callback, nint module, uint thread);
    [DllImport("user32.dll")] private static extern bool UnhookWindowsHookEx(nint hook);
    [DllImport("user32.dll")] private static extern nint CallNextHookEx(nint hook, int code, nint message, nint data);
    [DllImport("user32.dll")] private static extern bool PostThreadMessage(uint thread, uint message, nuint w, nint l);
    [DllImport("user32.dll")] private static extern nint SetWinEventHook(uint min, uint max, nint module, WinEventCallback callback, uint process, uint thread, uint flags);
    [DllImport("user32.dll")] private static extern bool UnhookWinEvent(nint hook);
    [DllImport("kernel32.dll")] private static extern uint GetCurrentThreadId();
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern nint GetModuleHandle(string? name);
}

public sealed class RealTargetPicker : ITargetPicker, IDisposable
{
    private LowLevelHooks? _hooks;
    private int _generation;
    public event Action<TargetCandidate>? TargetPicked;
    public event Action<string>? PickRejected;
    public event Action? PickingStarted;
    public bool Failed => _hooks?.Overflowed == true;
    public void Start()
    {
        if (_hooks is not null) return;
        PickingStarted?.Invoke();
        var generation = Interlocked.Increment(ref _generation);
        _hooks = new(false, notice =>
        {
            if (!HookEventPolicy.IsPickerClick(notice.Injected, notice.Extra, notice.Message)) return;
            // Background worker, never the hook callback. Let the original click establish focus first.
            Thread.Sleep(60);
            if (generation != Volatile.Read(ref _generation)) return;
            try
            {
                var hit = DesktopNative.WindowFromPoint(new() { X = notice.X, Y = notice.Y });
                if (WindowsSafetyProbe.IsHostOrDescendant(WindowsSafetyProbe.GetWindowProcessId(hit), Environment.ProcessId)) return;
                var target = WindowsTarget.Capture(notice.X, notice.Y);
                if (generation != Volatile.Read(ref _generation)) return;
                TargetPicked?.Invoke(target);
            }
            catch { PickRejected?.Invoke(Loc.T("localinputhooks.cannot.verify.the.selected.target.focus.or.process.identity")); }
        });
    }
    public void Stop() { Interlocked.Increment(ref _generation); Interlocked.Exchange(ref _hooks, null)?.Dispose(); }
    public void Dispose() => Stop();
}

public sealed class LocalPhysicalInputMonitor : IDisposable
{
    private LowLevelHooks? _hooks;
    public event Action? PhysicalInput;
    public bool Failed => _hooks?.Overflowed == true;
    public bool HasPendingInput => _hooks?.PendingInput == true;
    public void Start()
    {
        if (_hooks is not null) return;
        _hooks = new(true, notice =>
        {
            if (HookEventPolicy.IsTakeover(notice.Keyboard, notice.Message, notice.Injected, notice.Extra)) PhysicalInput?.Invoke();
        });
    }
    public void Stop() => Interlocked.Exchange(ref _hooks, null)?.Dispose();
    public void Dispose() => Stop();
}
