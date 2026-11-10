using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Automation;

namespace FastInput.Windows.Realtime;

internal static class DesktopNative
{
    [StructLayout(LayoutKind.Sequential)] internal struct Point { public int X, Y; }
    [StructLayout(LayoutKind.Sequential)] internal struct Rect { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)] internal struct GuiInfo
    { public uint Size, Flags; public nint Active, Focus, Capture, Menu, Move, Caret; public Rect CaretRect; }
    internal static nint Focus()
    {
        var thread = GetWindowThreadProcessId(GetForegroundWindow(), out _);
        var info = new GuiInfo { Size = (uint)Marshal.SizeOf<GuiInfo>() };
        if (thread == 0 || !GetGUIThreadInfo(thread, ref info)) throw new InvalidOperationException("focus_query_failed");
        return info.Focus;
    }
    internal static string Class(nint hwnd)
    {
        var text = new StringBuilder(256);
        if (GetClassName(hwnd, text, text.Capacity) == 0) throw new InvalidOperationException("class_query_failed");
        return text.ToString();
    }
    internal static bool Identity(TargetCandidate t) => IsWindow(t.NativeWindow)
        && WindowsSafetyProbe.GetWindowProcessId(t.NativeWindow) == t.ProcessId
        && ProcessStart(t.ProcessId) == t.ProcessStartTimeUtcTicks;
    internal static long ProcessStart(int pid) { using var p = Process.GetProcessById(pid); return p.StartTime.ToUniversalTime().Ticks; }
    internal static bool FocusMatches(TargetCandidate t) => Identity(t) && GetForegroundWindow() == t.TopLevelWindow
        && Focus() == t.NativeWindow;
    internal static bool DefaultDesktop()
    {
        var handle = OpenInputDesktop(0, false, 1);
        if (handle == 0) return false;
        try
        {
            var name = new StringBuilder(256);
            return GetUserObjectInformation(handle, 2, name, 512, out _) && name.ToString() == "Default";
        }
        finally { CloseDesktop(handle); }
    }
    [DllImport("user32.dll")] internal static extern nint GetForegroundWindow();
    [DllImport("user32.dll")] internal static extern uint GetWindowThreadProcessId(nint h, out uint pid);
    [DllImport("user32.dll")] internal static extern bool IsWindow(nint h);
    [DllImport("user32.dll")] internal static extern bool IsWindowEnabled(nint h);
    [DllImport("user32.dll")] internal static extern nint GetAncestor(nint h, uint flags);
    [DllImport("user32.dll")] internal static extern nint WindowFromPoint(Point p);
    [DllImport("user32.dll")] internal static extern bool GetWindowRect(nint h, out Rect r);
    [DllImport("user32.dll")] internal static extern bool GetGUIThreadInfo(uint thread, ref GuiInfo info);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] internal static extern int GetClassName(nint h, StringBuilder text, int length);
    [DllImport("user32.dll", EntryPoint = "GetWindowLongW", SetLastError = true)] internal static extern int GetWindowLong(nint h, int index);
    [DllImport("user32.dll")] private static extern nint OpenInputDesktop(uint flags, bool inherit, uint access);
    [DllImport("user32.dll")] private static extern bool CloseDesktop(nint desktop);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern bool GetUserObjectInformation(nint h, int index, StringBuilder name, int length, out int required);
}

public static class TargetClassification
{
    public static bool HasVerifiedWin32Offsets(string controlClass, string text) =>
        !controlClass.Contains("RichEdit", StringComparison.OrdinalIgnoreCase) || (!text.Contains('\r') && !text.Contains('\n'));
    public static string Classify(string controlClass, string topClass, string processName)
    {
        if (controlClass.Contains("RichEditD2DPT", StringComparison.OrdinalIgnoreCase)) return "unsupported";
        if (topClass == "Chrome_WidgetWin_1" && (processName.Equals("chrome", StringComparison.OrdinalIgnoreCase)
            || processName.Equals("msedge", StringComparison.OrdinalIgnoreCase))) return "chromium";
        if (controlClass.Equals("Edit", StringComparison.OrdinalIgnoreCase)
            || controlClass.StartsWith("RichEdit", StringComparison.OrdinalIgnoreCase)
            || controlClass.StartsWith("RICHEDIT", StringComparison.OrdinalIgnoreCase)
            || controlClass.StartsWith("WindowsForms10.EDIT.", StringComparison.OrdinalIgnoreCase)
            || controlClass.StartsWith("WindowsForms10.RichEdit", StringComparison.OrdinalIgnoreCase)) return "win32";
        return "unsupported";
    }
}

public static class WindowsTarget
{
    // Called only after a local click. Nothing in the Rust contract supplies these arguments.
    public static TargetCandidate Capture(int x, int y)
    {
        var hit = DesktopNative.WindowFromPoint(new() { X = x, Y = y });
        var top = DesktopNative.GetAncestor(hit, 2);
        var focus = DesktopNative.Focus();
        if (top == 0 || top != DesktopNative.GetForegroundWindow() || DesktopNative.GetAncestor(focus, 2) != top)
            throw new InvalidOperationException("click_focus_mismatch");
        var pid = WindowsSafetyProbe.GetWindowProcessId(focus);
        using var p = Process.GetProcessById(pid);
        var cls = DesktopNative.Class(focus);
        var kind = TargetClassification.Classify(cls, DesktopNative.Class(top), p.ProcessName);
        if (kind == "win32" && hit != focus) throw new InvalidOperationException("click_did_not_hit_focused_edit");
        if (!DesktopNative.GetWindowRect(focus, out var rect)) throw new InvalidOperationException("bounds_query_failed");
        string? identity = null;
        if (kind == "chromium")
        {
            var element = AutomationElement.FocusedElement;
            if (!element.Current.HasKeyboardFocus || element.Current.ProcessId != pid)
                throw new InvalidOperationException("browser_uia_focus_process_mismatch");
            var underPoint = AutomationElement.FromPoint(new System.Windows.Point(x, y));
            var matched = false;
            for (var depth = 0; underPoint is not null && depth < 12; depth++, underPoint = TreeWalker.RawViewWalker.GetParent(underPoint))
                if (Automation.Compare(underPoint, element)) { matched = true; break; }
            if (!matched) throw new InvalidOperationException("click_did_not_hit_focused_browser_element");
            identity = ChromiumUiaAdapter.Identity(element);
            var bounds = element.Current.BoundingRectangle;
            rect = new() { Left = (int)bounds.Left, Top = (int)bounds.Top, Right = (int)bounds.Right, Bottom = (int)bounds.Bottom };
        }
        var work = Screen.GetWorkingArea(new System.Drawing.Point(x, y));
        return new($"local:{focus:X}:{p.StartTime.ToUniversalTime().Ticks}", pid, p.StartTime.ToUniversalTime().Ticks,
            new(rect.Left, rect.Top, rect.Right - rect.Left, rect.Bottom - rect.Top), new(work.X, work.Y, work.Width, work.Height))
        {
            NativeWindow = focus, TopLevelWindow = top, ProcessName = p.ProcessName, AdapterKind = kind, AutomationIdentity = identity,
            UnsupportedReason = kind != "unsupported" ? null : cls.Contains("RichEditD2DPT", StringComparison.OrdinalIgnoreCase)
                ? Loc.T("windowstarget.the.new.notepad.richeditd2dpt.is.not.supported.because.it") : Loc.T("windowstarget.only.classic.edit.richedit.and.editable.chrome.edge.fields")
        };
    }
}
