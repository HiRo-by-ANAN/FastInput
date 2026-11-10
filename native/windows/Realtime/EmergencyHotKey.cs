using System.Runtime.InteropServices;

namespace FastInput.Windows.Realtime;

internal interface IHotKeyApi
{
    bool Register(nint window, int id, uint modifiers, uint key);
    void Unregister(nint window, int id);
    int LastError { get; }
}

internal sealed class WindowsHotKeyApi : IHotKeyApi
{
    public bool Register(nint window, int id, uint modifiers, uint key) => RegisterHotKey(window, id, modifiers, key);
    public void Unregister(nint window, int id) => _ = UnregisterHotKey(window, id);
    public int LastError => Marshal.GetLastWin32Error();
    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool RegisterHotKey(nint window, int id, uint modifiers, uint key);
    [DllImport("user32.dll")]
    private static extern bool UnregisterHotKey(nint window, int id);
}

internal sealed class EmergencyHotKey(IHotKeyApi api) : IDisposable
{
    public const int Id = 0x4612;
    public const int Message = 0x312;
    private nint _window;
    // 依序嘗試；第一個沒被占用的就用（這台電腦的 Ctrl+Alt+F11／F12 已被占用，見 2026-11-07 實測）。
    private static readonly (uint Modifiers, uint Key, string Label)[] Candidates =
    [
        (0x4003, 0x7B, "Ctrl+Alt+F12"),
        (0x4007, 0x7B, "Ctrl+Alt+Shift+F12"),
        (0x4003, 0x13, "Ctrl+Alt+Pause"),
        (0x4007, 0x79, "Ctrl+Alt+Shift+F10"),
        (0x4003, 0x23, "Ctrl+Alt+End"),
    ];
    public static string CurrentLabel { get; private set; } = "Ctrl+Alt+F12";
    public string Label { get; private set; } = "Ctrl+Alt+F12";
    public bool Available { get; private set; }
    private int? _lastError;
    public string FailureReason => Available ? "" : _lastError is null
        ? Loc.T("emergencyhotkey.the.emergency.stop.hotkey.has.not.been.registered")
        : _lastError == 1409
            ? Loc.T("emergencyhotkey.all.emergency.stop.hotkeys.including.ctrl.alt.f12.are")
            : Loc.T("emergencyhotkey.cannot.register.the.emergency.stop.hotkey.real.time.typing", _lastError.Value);

    public bool Register(nint window)
    {
        Dispose();
        var lastError = 0;
        foreach (var candidate in Candidates)
        {
            if (api.Register(window, Id, candidate.Modifiers, candidate.Key))
            {
                Available = true; _window = window; Label = candidate.Label; CurrentLabel = candidate.Label; _lastError = null;
                return true;
            }
            lastError = api.LastError;
        }
        _lastError = lastError;
        return false;
    }

    public void Dispose()
    {
        if (Available) api.Unregister(_window, Id);
        Available = false;
        _window = 0;
    }
}
