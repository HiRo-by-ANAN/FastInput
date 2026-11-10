using System.Diagnostics;
using System.Runtime.InteropServices;

namespace FastInput.Windows.Realtime;

public sealed record WindowSafetyFacts(bool FocusMatches = false, bool IsPassword = true, bool IsReadOnly = true,
    bool IsEnabled = false, bool IsLocked = true, bool IsSecureDesktop = true, bool IsRunDialog = true, bool Supported = false,
    bool PhysicalInputPending = false);

/// <summary>
/// Read-only Windows implementation. Any unavailable native fact fails closed.
/// </summary>
public sealed class WindowsSafetyProbe : ISafetyProbe
{
    private readonly Func<TargetCandidate, WindowSafetyFacts> _facts;
    public WindowsSafetyProbe(Func<TargetCandidate, WindowSafetyFacts>? facts = null) => _facts = facts ?? ReadWindowFacts;

    internal static WindowSafetyFacts ReadWindowFacts(TargetCandidate target)
    {
        if (target.NativeWindow == 0 || target.TopLevelWindow == 0) return new();
        var desktop = DesktopNative.DefaultDesktop();
        Marshal.SetLastPInvokeError(0);
        var style = DesktopNative.GetWindowLong(target.NativeWindow, -16);
        if (style == 0 && Marshal.GetLastPInvokeError() != 0) throw new InvalidOperationException("window_style_query_failed");
        var topClass = DesktopNative.Class(target.TopLevelWindow);
        var kind = TargetClassification.Classify(DesktopNative.Class(target.NativeWindow), topClass, target.ProcessName);
        // UIA password/editability/runtime identity is checked in ChromiumUiaAdapter on its worker,
        // not while the controller holds its authorization lock. No UIA provider call belongs in Tick.
        return new(DesktopNative.FocusMatches(target), target.AdapterKind == "win32" && (style & 0x20) != 0,
            target.AdapterKind == "win32" && (style & 0x800) != 0,
            DesktopNative.IsWindowEnabled(target.NativeWindow), !desktop, !desktop,
            topClass == "#32770", kind == target.AdapterKind && kind != "unsupported");
    }

    public SafetySnapshot Inspect(TargetCandidate target)
    {
        using var process = Process.GetProcessById(target.ProcessId);
        using var host = Process.GetCurrentProcess();
        var facts = _facts(target);
        return new(process.Id, process.StartTime.ToUniversalTime().Ticks, process.ProcessName,
            process.SessionId, host.SessionId, ReadIntegrityLevel(process.Id), ReadIntegrityLevel(host.Id),
            ReadHostElevated(), IsHostOrDescendant(process.Id, host.Id), facts.FocusMatches, facts.IsPassword,
            facts.IsReadOnly, facts.IsEnabled, facts.IsLocked, facts.IsSecureDesktop, facts.IsRunDialog, facts.Supported, facts.PhysicalInputPending);
    }

    public static int GetWindowProcessId(nint localWindow)
    {
        if (GetWindowThreadProcessId(localWindow, out var process) == 0) throw new InvalidOperationException("Window query failed.");
        return checked((int)process);
    }

    public static bool ReadHostElevated()
    {
        using var process = Process.GetCurrentProcess();
        var token = OpenToken(process.Id);
        try
        {
            var buffer = Marshal.AllocHGlobal(sizeof(int));
            try
            {
                if (!GetTokenInformation(token, 20, buffer, sizeof(int), out _)) throw new InvalidOperationException("Token query failed.");
                return Marshal.ReadInt32(buffer) != 0;
            }
            finally { Marshal.FreeHGlobal(buffer); }
        }
        finally { CloseHandle(token); }
    }

    private static int ReadIntegrityLevel(int processId)
    {
        var token = OpenToken(processId);
        try
        {
            _ = GetTokenInformation(token, 25, 0, 0, out var length);
            if (length <= 0 || length > 65536) throw new InvalidOperationException("Integrity query failed.");
            var buffer = Marshal.AllocHGlobal(length);
            try
            {
                if (!GetTokenInformation(token, 25, buffer, length, out _)) throw new InvalidOperationException("Integrity query failed.");
                var sid = Marshal.ReadIntPtr(buffer);
                var count = Marshal.ReadByte(GetSidSubAuthorityCount(sid));
                if (count == 0) throw new InvalidOperationException("Invalid integrity SID.");
                return Marshal.ReadInt32(GetSidSubAuthority(sid, (uint)(count - 1)));
            }
            finally { Marshal.FreeHGlobal(buffer); }
        }
        finally { CloseHandle(token); }
    }

    private static nint OpenToken(int processId)
    {
        var process = OpenProcess(0x1000, false, processId);
        if (process == 0) throw new InvalidOperationException("Process query failed.");
        try
        {
            if (!OpenProcessToken(process, 0x0008, out var token)) throw new InvalidOperationException("Token query failed.");
            return token;
        }
        finally { CloseHandle(process); }
    }

    internal static bool IsHostOrDescendant(int processId, int hostId)
    {
        var parents = new Dictionary<int, int>();
        var snapshot = CreateToolhelp32Snapshot(2, 0);
        if (snapshot == -1) throw new InvalidOperationException("Process ancestry query failed.");
        try
        {
            var entry = new ProcessEntry { Size = (uint)Marshal.SizeOf<ProcessEntry>() };
            if (!Process32First(snapshot, ref entry)) throw new InvalidOperationException("Process ancestry query failed.");
            do { parents[(int)entry.ProcessId] = (int)entry.ParentProcessId; } while (Process32Next(snapshot, ref entry));
        }
        finally { CloseHandle(snapshot); }
        var seen = new HashSet<int>();
        while (seen.Add(processId))
        {
            if (processId == hostId) return true;
            if (!parents.TryGetValue(processId, out processId) || processId == 0) return false;
        }
        return false;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct ProcessEntry
    {
        public uint Size, Usage, ProcessId;
        public nuint DefaultHeapId;
        public uint ModuleId, Threads, ParentProcessId;
        public int PriorityClassBase;
        public uint Flags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string Executable;
    }
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(nint window, out uint processId);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern nint OpenProcess(uint access, bool inherit, int processId);
    [DllImport("kernel32.dll")] private static extern bool CloseHandle(nint handle);
    [DllImport("advapi32.dll", SetLastError = true)] private static extern bool OpenProcessToken(nint process, uint access, out nint token);
    [DllImport("advapi32.dll", SetLastError = true)] private static extern bool GetTokenInformation(nint token, int kind, nint buffer, int length, out int required);
    [DllImport("advapi32.dll")] private static extern nint GetSidSubAuthorityCount(nint sid);
    [DllImport("advapi32.dll")] private static extern nint GetSidSubAuthority(nint sid, uint index);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern nint CreateToolhelp32Snapshot(uint flags, uint processId);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern bool Process32First(nint snapshot, ref ProcessEntry entry);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern bool Process32Next(nint snapshot, ref ProcessEntry entry);
}
