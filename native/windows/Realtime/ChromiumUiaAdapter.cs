using System.Runtime.InteropServices;
using System.Windows.Automation;
using System.Windows.Automation.Text;

namespace FastInput.Windows.Realtime;

public static class NativeInput
{
    public const uint Marker = 0x46494E50;
    [StructLayout(LayoutKind.Sequential)] internal struct Input { public uint Type; public Union Data; }
    [StructLayout(LayoutKind.Explicit)] internal struct Union
    { [FieldOffset(0)] public Keyboard Key; [FieldOffset(0)] public Mouse Mouse; }
    [StructLayout(LayoutKind.Sequential)] internal struct Keyboard { public ushort Vk, Scan; public uint Flags, Time; public nuint Extra; }
    [StructLayout(LayoutKind.Sequential)] internal struct Mouse { public int X, Y; public uint Data, Flags, Time; public nuint Extra; }
    internal static Input[] Events(string text) => text.SelectMany(c => new[]
    {
        new Input { Type = 1, Data = new() { Key = new() { Scan = c, Flags = 4, Extra = Marker } } },
        new Input { Type = 1, Data = new() { Key = new() { Scan = c, Flags = 6, Extra = Marker } } }
    }).ToArray();
    internal static Input[] Delete() => [new() { Type = 1, Data = new() { Key = new() { Vk = 0x2E, Extra = Marker } } },
        new() { Type = 1, Data = new() { Key = new() { Vk = 0x2E, Flags = 2, Extra = Marker } } }];
    internal static Input[] Enter() => [new() { Type = 1, Data = new() { Key = new() { Vk = 0x0D, Extra = Marker } } },
        new() { Type = 1, Data = new() { Key = new() { Vk = 0x0D, Flags = 2, Extra = Marker } } }];
    internal static bool ModifiersReleased() => new[] { 0x10, 0x11, 0x12, 0x5B, 0x5C }.All(k => (GetAsyncKeyState(k) & 0x8000) == 0);
    [DllImport("user32.dll", SetLastError = true)] internal static extern uint SendInput(uint count, Input[] input, int size);
    [DllImport("user32.dll")] private static extern short GetAsyncKeyState(int key);
}

public interface IChromiumUiaApi
{
    TargetReadBack Read(TargetCandidate target);
    void Select(TargetCandidate target, int start, int length);
    // Select and return the read-back taken right after the selection (saves one slow UIA round trip).
    TargetReadBack SelectAndRead(TargetCandidate target, int start, int length) { Select(target, start, length); return Read(target); }
    bool FinalCheck(TargetCandidate target);
    uint Inject(TargetCandidate target, string text, bool deleteSelection, Func<bool> mayApply);
}

public sealed class ChromiumUiaAdapter : ITargetAdapter
{
    private readonly IChromiumUiaApi _api;
    public ChromiumUiaAdapter() : this(new WindowsChromiumApi()) { }
    public ChromiumUiaAdapter(IChromiumUiaApi api) => _api = api;
    private sealed class WindowsChromiumApi : IChromiumUiaApi
    {
        public TargetReadBack Read(TargetCandidate t) => ChromiumUiaAdapter.Read(Focused(t));
        public void Select(TargetCandidate t, int start, int length) => ChromiumUiaAdapter.Select(Focused(t), start, length);
        public TargetReadBack SelectAndRead(TargetCandidate t, int start, int length) => ChromiumUiaAdapter.Select(Focused(t), start, length);
        public bool FinalCheck(TargetCandidate t) => NativeInput.ModifiersReleased()
            && Identity(AutomationElement.FocusedElement) == t.AutomationIdentity && DesktopNative.FocusMatches(t);
        public uint Inject(TargetCandidate target, string text, bool deleteSelection, Func<bool> mayApply)
        {
            var events = text.Length > 0 ? NativeInput.Events(text) : deleteSelection ? NativeInput.Delete() : [];
            // Build the batch before the final authorization/native-focus check, not inside its time window.
            if (!mayApply() || !NativeInput.ModifiersReleased() || !DesktopNative.FocusMatches(target)) return 0;
            return events.Length == 0 ? 0 : NativeInput.SendInput((uint)events.Length, events, Marshal.SizeOf<NativeInput.Input>());
        }
    }
    private TargetCandidate? _bound;
    private TargetReadBack? _verified;
    public static string Identity(AutomationElement e) => string.Join(".", e.GetRuntimeId());
    internal static bool EnterTargetSafe(TargetCandidate target)
    {
        try { _ = Focused(target); return true; }
        catch (Exception) { return false; }
    }
    public AdapterProbe Probe(TargetCandidate target) => new(target.AdapterKind == "chromium" && target.AutomationIdentity is not null,
        "chromium", target.UnsupportedReason);
    private static AutomationElement Focused(TargetCandidate target)
    {
        if (!DesktopNative.FocusMatches(target) || SafetyPolicy.Check(target, new WindowsSafetyProbe().Inspect(target)) is not null)
            throw new InvalidOperationException("browser_focus_or_safety_changed");
        var e = AutomationElement.FocusedElement;
        var c = e.Current;
        if (!c.HasKeyboardFocus || c.ProcessId != target.ProcessId || Identity(e) != target.AutomationIdentity)
            throw new TargetAccessException(Loc.T("chromiumuiaadapter.browser.focus.element.or.process.identity.changed"));
        if (c.IsPassword) throw new TargetAccessException(Loc.T("chromiumuiaadapter.password.fields.are.not.supported.uia.ispassword"));
        if (!c.IsEnabled) throw new TargetAccessException(Loc.T("chromiumuiaadapter.the.browser.field.is.disabled"));
        if (e.TryGetCurrentPattern(ValuePattern.Pattern, out var value) && ((ValuePattern)value).Current.IsReadOnly)
            throw new TargetAccessException(Loc.T("chromiumuiaadapter.the.browser.field.is.read.only"));
        if (!e.TryGetCurrentPattern(TextPattern.Pattern, out var pattern)
            || !Equals(((TextPattern)pattern).DocumentRange.GetAttributeValue(TextPattern.IsReadOnlyAttribute), false))
            throw new TargetAccessException(Loc.T("chromiumuiaadapter.this.field.is.not.supported.uia.does.not.expose"));
        return e;
    }
    private static TargetReadBack Read(AutomationElement e)
    {
        // TextPattern is mandatory for positioning even if ValuePattern is available.
        var p = (TextPattern)e.GetCurrentPattern(TextPattern.Pattern);
        var text = p.DocumentRange.GetText(-1);
        if (text.Length > 65534) throw new InvalidOperationException("browser_text_too_long");
        var hasValue = e.TryGetCurrentPattern(ValuePattern.Pattern, out var vp);
        // Chrome exposes the accessible name (aria-label/placeholder) as TextPattern text while the field is empty.
        // ValuePattern is authoritative only for that exact case: empty value + text equal to Name/HelpText.
        var nameLike = text.Length > 0 && (text == e.Current.Name || text == e.Current.HelpText);
        var placeholderOnly = nameLike && hasValue && ((ValuePattern)vp).Current.Value.Length == 0;
        if (placeholderOnly) text = "";
        if (hasValue && ((ValuePattern)vp).Current.Value != text)
            throw new InvalidOperationException("browser_text_value_disagree");
        var ranges = p.GetSelection();
        if (ranges.Length != 1) throw new InvalidOperationException("browser_selection_unavailable");
        var prefix = p.DocumentRange.Clone();
        prefix.MoveEndpointByRange(TextPatternRangeEndpoint.End, ranges[0], TextPatternRangeEndpoint.Start);
        int start = prefix.GetText(-1).Length, end = start + ranges[0].GetText(-1).Length;
        // contenteditable has no ValuePattern: accept the name-as-text case only with a collapsed caret at 0.
        if (!hasValue && nameLike && start == 0 && end == 0) { placeholderOnly = true; text = ""; }
        if (placeholderOnly) start = end = 0;
        if (!TextSafety.IsBoundary(text, start) || !TextSafety.IsBoundary(text, end)) throw new InvalidOperationException("browser_selection_invalid");
        return new(text, start, end);
    }
    private static TargetReadBack Select(AutomationElement e, int start, int length)
    {
        var p = (TextPattern)e.GetCurrentPattern(TextPattern.Pattern);
        var range = p.DocumentRange.Clone();
        range.MoveEndpointByRange(TextPatternRangeEndpoint.End, range, TextPatternRangeEndpoint.Start);
        var moved = range.Move(TextUnit.Character, start);
        var grown = range.MoveEndpointByUnit(TextPatternRangeEndpoint.End, TextUnit.Character, length);
        if (moved != start || grown != length) throw new InvalidOperationException("browser_range_mapping_unproven");
        range.Select();
        // Chrome applies the selection asynchronously; poll (read-only) until it is visible or the deadline passes.
        var settle = System.Diagnostics.Stopwatch.StartNew();
        while (true)
        {
            try
            {
                var actual = Read(e);
                if (actual.SelectionStart == start && actual.SelectionEnd == start + length) return actual;
            }
            catch (InvalidOperationException) when (settle.ElapsedMilliseconds < 300) { }
            if (settle.ElapsedMilliseconds >= 300) throw new InvalidOperationException("browser_range_mapping_mismatch");
            Thread.Sleep(10);
        }
    }
    public Task<TargetReadBack> BindAsync(TargetCandidate target, CancellationToken token) => Task.Run(() =>
    {
        token.ThrowIfCancellationRequested();
        if (!Probe(target).Supported) throw new InvalidOperationException("unsupported_browser");
        var read = _api.Read(target); _bound = target; _verified = read; return read;
    }, token);
    public Task<TargetReadBack> ReadBackAsync(TargetCandidate target, CancellationToken token) => Task.Run(() =>
    {
        token.ThrowIfCancellationRequested();
        if (_bound != target) throw new InvalidOperationException("browser_not_bound");
        return _api.Read(target);
    }, token);
    public Task<AdapterOutcome> ApplyMinimalEditAsync(TargetCandidate target, MinimalEdit edit, TextSelection selection,
        Func<bool> mayApply, CancellationToken token) => Task.Run(() => Apply(target, edit, selection, mayApply, token));
    private AdapterOutcome Apply(TargetCandidate t, MinimalEdit edit, TextSelection selection, Func<bool> mayApply, CancellationToken token)
    {
        bool touched = false, injected = false;
        try
        {
            bool Allowed() => !token.IsCancellationRequested && mayApply();
            if (_bound != t || _verified is null || !Allowed()) return AdapterOutcome.Rejected;
            var before = _api.Read(t);
            if (before != _verified || !AdapterValidation.Valid(before.Text, edit, selection)) return AdapterOutcome.Rejected;
            var expected = before.Text.Remove(edit.Start, edit.DeleteLength).Insert(edit.Start, edit.InsertText);
            bool nonBmp = MinimalEditCalculator.HasSurrogate(before.Text);
            // Existing non-BMP offsets are unverified. Only keep the existing collapsed caret for an append.
            if (nonBmp && (edit.DeleteLength != 0 || edit.Start != before.SelectionStart || before.SelectionStart != before.SelectionEnd
                || selection.Start != edit.Start + edit.InsertText.Length || selection.End != selection.Start)) return AdapterOutcome.Rejected;
            if (!Allowed()) return AdapterOutcome.Rejected;
            TargetReadBack ready;
            if (!nonBmp) { touched = true; ready = _api.SelectAndRead(t, edit.Start, edit.DeleteLength); }
            else ready = null!;
            if (!Allowed()) return AdapterOutcome.Unknown;
            if (nonBmp) ready = _api.Read(t);
            if (ready.Text != before.Text || ready.SelectionStart != edit.Start || ready.SelectionEnd != edit.Start + edit.DeleteLength)
                return AdapterOutcome.Unknown;
            var count = edit.InsertText.Length > 0 ? edit.InsertText.Length * 2 : edit.DeleteLength > 0 ? 2 : 0;
            // Last checks immediately before the single SendInput call. This is still NOT atomic with foreground changes.
            if (!_api.FinalCheck(t) || !Allowed()) return touched ? AdapterOutcome.Unknown : AdapterOutcome.Rejected;
            if (count > 0)
            {
                injected = touched = true;
                uint sent = _api.Inject(t, edit.InsertText, edit.DeleteLength > 0, Allowed);
                if (sent != count) return sent > 0 ? AdapterOutcome.Partial : AdapterOutcome.Unknown;
            }
            // Read immediately, then allow a bounded read-only settling period for the target's input queue.
            // There is exactly one input dispatch; never retry, repair or roll back a failed batch.
            var settle = System.Diagnostics.Stopwatch.StartNew();
            // Chrome may transiently report an inconsistent selection while it processes input; read errors here are retried
            // (read-only) until the settle deadline.
            TargetReadBack? after = null;
            try { after = _api.Read(t); } catch (Exception) { }
            while ((after is null || after.Text != expected) && settle.ElapsedMilliseconds < 300)
            {
                if (!Allowed()) return AdapterOutcome.Unknown;
                Thread.Sleep(5);
                try { after = _api.Read(t); } catch (Exception) { after = null; }
            }
            if (after is null) return injected ? AdapterOutcome.Partial : AdapterOutcome.Unknown;
            if (!Allowed() || after.Text != expected) return AdapterOutcome.Unknown;
            if (after.SelectionStart != selection.Start || after.SelectionEnd != selection.End)
            {
                if (MinimalEditCalculator.HasSurrogate(expected)) return AdapterOutcome.Unknown;
                if (!Allowed()) return AdapterOutcome.Unknown;
                touched = true; _api.Select(t, selection.Start, selection.End - selection.Start);
                after = _api.Read(t);
            }
            if (!Allowed() || after.Text != expected || after.SelectionStart != selection.Start || after.SelectionEnd != selection.End)
                return AdapterOutcome.Unknown;
            _verified = after; return AdapterOutcome.Completed;
        }
        catch (Exception) { return injected ? AdapterOutcome.Partial : touched ? AdapterOutcome.Unknown : AdapterOutcome.Rejected; }
    }
}
