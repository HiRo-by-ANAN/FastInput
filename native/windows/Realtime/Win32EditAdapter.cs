using System.Runtime.InteropServices;

namespace FastInput.Windows.Realtime;

public interface IWin32EditApi
{
    TargetReadBack Read(nint hwnd);
    void Select(nint hwnd, int start, int end);
    void Replace(nint hwnd, string text);
}

internal sealed class WindowsEditApi : IWin32EditApi
{
    // WM_* and EM_* below are system messages: user32 performs cross-process marshalling.
    // No pointer-bearing WM_USER RichEdit message is sent across processes.
    private const int MaximumRead = 1_048_576;
    private static nuint Message(nint h, uint message, nint w, nint l)
    {
        if (SendMessageTimeout(h, message, w, l, 2, 100, out var result) == 0)
            throw new TimeoutException("win32_message_result_unknown");
        return result;
    }
    public TargetReadBack Read(nint hwnd)
    {
        var length = checked((int)Message(hwnd, 0xE, 0, 0));
        // EM_GETSEL's packed return is only 16 bits per endpoint. Reject, never truncate.
        if (length < 0 || length > MaximumRead || length > 65534) throw new InvalidOperationException("win32_text_too_long");
        var buffer = Marshal.AllocHGlobal((length + 1) * 2);
        try
        {
            var copied = checked((int)Message(hwnd, 0xD, length + 1, buffer));
            var text = Marshal.PtrToStringUni(buffer, copied) ?? "";
            if (!TargetClassification.HasVerifiedWin32Offsets(DesktopNative.Class(hwnd), text))
                throw new TargetAccessException(Loc.T("win32editadapter.richedit.line.break.position.mapping.for.wm.gettext.em"));
            var sel = (uint)Message(hwnd, 0xB0, 0, 0);
            var lengthAfter = checked((int)Message(hwnd, 0xE, 0, 0));
            if (copied != length || lengthAfter != length || sel == uint.MaxValue)
                throw new InvalidOperationException("win32_read_unstable_or_selection_too_long");
            int start = (int)(sel & 65535), end = (int)(sel >> 16);
            if (start > end || end > text.Length) throw new InvalidOperationException("win32_selection_invalid");
            return new(text, start, end);
        }
        finally { Marshal.FreeHGlobal(buffer); }
    }
    public void Select(nint hwnd, int start, int end) => Message(hwnd, 0xB1, start, end);
    public void Replace(nint hwnd, string text)
    {
        var buffer = Marshal.StringToHGlobalUni(text);
        try { Message(hwnd, 0xC2, 1, buffer); }
        finally { Marshal.FreeHGlobal(buffer); }
    }
    [DllImport("user32.dll", EntryPoint = "SendMessageTimeoutW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern nint SendMessageTimeout(nint h, uint msg, nint wp, nint lp, uint flags, uint timeout, out nuint result);
}

public sealed class Win32EditAdapter : ITargetAdapter
{
    private readonly IWin32EditApi _api;
    private readonly Func<TargetCandidate, bool> _safe;
    private TargetCandidate? _bound;
    private TargetReadBack? _verified;
    public Win32EditAdapter() : this(new WindowsEditApi(), t => SafetyPolicy.Check(t, new WindowsSafetyProbe().Inspect(t)) is null) { }
    // Injectable transport and guard let tests prove timeout/partial semantics without touching a desktop.
    public Win32EditAdapter(IWin32EditApi api, Func<TargetCandidate, bool> safe) { _api = api; _safe = safe; }
    public AdapterProbe Probe(TargetCandidate target) => new(target.AdapterKind == "win32", "win32", target.UnsupportedReason);
    public Task<TargetReadBack> BindAsync(TargetCandidate target, CancellationToken token) => Task.Run(() =>
    {
        token.ThrowIfCancellationRequested();
        if (!Probe(target).Supported || !_safe(target)) throw new InvalidOperationException("unsafe_win32_target");
        var read = _api.Read(target.NativeWindow);
        _bound = target; _verified = read;
        return read;
    }, token);
    public Task<TargetReadBack> ReadBackAsync(TargetCandidate target, CancellationToken token) => Task.Run(() =>
    {
        token.ThrowIfCancellationRequested();
        if (_bound != target || !_safe(target)) throw new InvalidOperationException("target_changed");
        return _api.Read(target.NativeWindow);
    }, token);
    public Task<AdapterOutcome> ApplyMinimalEditAsync(TargetCandidate target, MinimalEdit edit, TextSelection selection,
        Func<bool> mayApply, CancellationToken token) => Task.Run(() => Apply(target, edit, selection, mayApply, token));
    private AdapterOutcome Apply(TargetCandidate t, MinimalEdit edit, TextSelection selection, Func<bool> mayApply, CancellationToken token)
    {
        var touched = false;
        try
        {
            bool Guard() => !token.IsCancellationRequested && mayApply() && _safe(t);
            if (_bound != t || _verified is null || !Guard()) return AdapterOutcome.Rejected;
            var before = _api.Read(t.NativeWindow);
            if (before != _verified || !AdapterValidation.Valid(before.Text, edit, selection)) return AdapterOutcome.Rejected;
            var expected = before.Text.Remove(edit.Start, edit.DeleteLength).Insert(edit.Start, edit.InsertText);
            if (!Guard()) return AdapterOutcome.Rejected;
            touched = true; // EM_SETSEL also has a side effect and may time out after dispatch.
            _api.Select(t.NativeWindow, edit.Start, edit.Start + edit.DeleteLength);
            var selected = _api.Read(t.NativeWindow);
            if (selected.Text != before.Text || selected.SelectionStart != edit.Start || selected.SelectionEnd != edit.Start + edit.DeleteLength)
                return AdapterOutcome.Unknown;
            if (!Guard()) return AdapterOutcome.Unknown;
            if (!edit.IsEmpty) _api.Replace(t.NativeWindow, edit.InsertText);
            if (!Guard()) return AdapterOutcome.Unknown;
            _api.Select(t.NativeWindow, selection.Start, selection.End);
            var after = _api.Read(t.NativeWindow);
            if (!Guard() || after.Text != expected || after.SelectionStart != selection.Start || after.SelectionEnd != selection.End)
                return AdapterOutcome.Unknown;
            _verified = after;
            return AdapterOutcome.Completed;
        }
        catch (TimeoutException) { return AdapterOutcome.Unknown; }
        catch (Exception) { return touched ? AdapterOutcome.Unknown : AdapterOutcome.Rejected; }
    }
}

public static class AdapterValidation
{
    public static bool Valid(string previous, MinimalEdit edit, TextSelection selection)
    {
        if (!TextSafety.IsAllowed(edit.InsertText) || edit.DeleteLength < 0 || edit.Start < 0
            || edit.Start > previous.Length || edit.DeleteLength > previous.Length - edit.Start
            || !TextSafety.IsBoundary(previous, edit.Start) || !TextSafety.IsBoundary(previous, edit.Start + edit.DeleteLength)) return false;
        var next = previous.Remove(edit.Start, edit.DeleteLength).Insert(edit.Start, edit.InsertText);
        return selection.Start <= selection.End && TextSafety.IsBoundary(next, selection.Start) && TextSafety.IsBoundary(next, selection.End);
    }
}

public sealed class TargetAdapterRouter : ITargetAdapter, IVirtualEnterAdapter
{
    private readonly Win32EditAdapter _win32 = new();
    private readonly ChromiumUiaAdapter _chromium = new();
    private readonly VirtualEnterSender _enter = new(new WindowsVirtualEnterInput());
    private ITargetAdapter Adapter(TargetCandidate target) => target.AdapterKind switch
    { "win32" => _win32, "chromium" => _chromium, _ => throw new InvalidOperationException(target.UnsupportedReason ?? "unsupported_target") };
    public AdapterProbe Probe(TargetCandidate target) => target.AdapterKind is "win32" or "chromium"
        ? Adapter(target).Probe(target) : new(false, "other", target.UnsupportedReason);
    public Task<TargetReadBack> BindAsync(TargetCandidate t, CancellationToken c) => Adapter(t).BindAsync(t, c);
    public Task<TargetReadBack> ReadBackAsync(TargetCandidate t, CancellationToken c) => Adapter(t).ReadBackAsync(t, c);
    public Task<AdapterOutcome> ApplyMinimalEditAsync(TargetCandidate t, MinimalEdit e, TextSelection s, Func<bool> guard, CancellationToken c)
        => Adapter(t).ApplyMinimalEditAsync(t, e, s, guard, c);
    public Task<AdapterOutcome> PressEnterAsync(TargetCandidate target, Func<bool> mayApply, CancellationToken token)
        => Task.Run(() => Probe(target).Supported ? _enter.Send(target, mayApply, token) : AdapterOutcome.Rejected);
}
