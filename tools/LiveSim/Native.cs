using System.Runtime.InteropServices;
namespace FastInput.LiveSim;
internal static class Native
{
    public static readonly nuint TestMarker = 0x4C53494D;
    [StructLayout(LayoutKind.Sequential)] public struct Point { public int X, Y; }
    [StructLayout(LayoutKind.Sequential)] public struct Mouse { public Point Point; public uint Data, Flags, Time; public nuint Extra; }
    [StructLayout(LayoutKind.Sequential)] private struct Input { public uint Type; public InputUnion Union; }
    [StructLayout(LayoutKind.Explicit)] private struct InputUnion { [FieldOffset(0)] public MouseInput Mouse; [FieldOffset(0)] public KeyboardInput Keyboard; }
    [StructLayout(LayoutKind.Sequential)] private struct MouseInput { public int X, Y; public uint Data, Flags, Time; public nuint Extra; }
    [StructLayout(LayoutKind.Sequential)] private struct KeyboardInput { public ushort Key, Scan; public uint Flags, Time; public nuint Extra; }
    public delegate nint HookProc(int code, nint message, nint data);
    [DllImport("user32.dll", SetLastError = true)] public static extern nint SetWindowsHookEx(int id, HookProc callback, nint module, uint thread);
    [DllImport("user32.dll")] public static extern bool UnhookWindowsHookEx(nint hook);
    [DllImport("user32.dll")] public static extern nint CallNextHookEx(nint hook, int code, nint message, nint data);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] public static extern nint GetModuleHandle(string? module);
    [DllImport("user32.dll")] public static extern bool SetForegroundWindow(nint hwnd);
    [DllImport("user32.dll")] public static extern nint GetForegroundWindow();
    [DllImport("user32.dll")] public static extern nint WindowFromPoint(Point point);
    [DllImport("user32.dll")] public static extern nint GetAncestor(nint hwnd, uint flags);
    [DllImport("user32.dll")] private static extern int GetSystemMetrics(int index);
    [DllImport("user32.dll", SetLastError = true)] private static extern uint SendInput(uint count, Input[] inputs, int size);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(nint hwnd, out uint pid);
    [DllImport("kernel32.dll")] private static extern uint GetCurrentThreadId();
    [DllImport("user32.dll")] private static extern bool AttachThreadInput(uint from, uint to, bool attach);
    [DllImport("user32.dll")] private static extern bool BringWindowToTop(nint hwnd);
    [DllImport("user32.dll")] private static extern bool ShowWindow(nint hwnd, int command);
    /// <summary>前景鎖定的標準繞法：暫時與目前前景視窗的執行緒共用輸入佇列。僅測試工具使用，且只作用於自己建立的 fixture。</summary>
    private static bool ActivateOwned(nint window)
    {
        if (GetForegroundWindow() == window) return true;
        ShowWindow(window, 9);
        var foreground = GetForegroundWindow();
        var foregroundThread = foreground == 0 ? 0 : GetWindowThreadProcessId(foreground, out _);
        var current = GetCurrentThreadId();
        var attached = foregroundThread != 0 && foregroundThread != current && AttachThreadInput(current, foregroundThread, true);
        try { BringWindowToTop(window); SetForegroundWindow(window); }
        finally { if (attached) AttachThreadInput(current, foregroundThread, false); }
        for (var i = 0; i < 20 && GetForegroundWindow() != window; i++) Thread.Sleep(25);
        return GetForegroundWindow() == window;
    }
    public static void Click(nint ownedWindow, nint exactField, int x, int y)
    {
        if (!ActivateOwned(ownedWindow)) throw new IOException("cannot activate owned fixture");
        Thread.Sleep(100);
        if (GetForegroundWindow() != ownedWindow) throw new IOException("foreground not owned fixture");
        var hit = WindowFromPoint(new Point { X = x, Y = y });
        if (exactField != 0 ? hit != exactField : GetAncestor(hit, 2) != ownedWindow) throw new IOException("fixture hit-test mismatch; no input sent");
        var left = GetSystemMetrics(76); var top = GetSystemMetrics(77); var width = GetSystemMetrics(78); var height = GetSystemMetrics(79);
        MouseInput Make(uint flags) => new() { X = (int)Math.Round((x - left) * 65535.0 / (width - 1)), Y = (int)Math.Round((y - top) * 65535.0 / (height - 1)), Flags = flags, Extra = TestMarker };
        var events = new[] { new Input { Union = new InputUnion { Mouse = Make(0xC001) } }, new Input { Union = new InputUnion { Mouse = Make(0xC003) } }, new Input { Union = new InputUnion { Mouse = Make(0xC005) } } };
        if (GetForegroundWindow() != ownedWindow || SendInput(3, events, Marshal.SizeOf<Input>()) != 3) throw new IOException("fixture click incomplete");
    }
}
