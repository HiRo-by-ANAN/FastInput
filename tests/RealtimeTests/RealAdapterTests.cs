using FastInput.Windows.Realtime;

namespace FastInput.RealtimeTests;

internal static class RealAdapterTests
{
    private static readonly TargetCandidate Target = new("local-test", 1, 2, new(0, 0, 50, 20), new(0, 0, 800, 600))
    { NativeWindow = 100, TopLevelWindow = 90, AdapterKind = "win32" };
    public static void Register(TestSuite suite)
    {
        suite.Add("real.overlay-outside-or-hidden-never-covers-field", () =>
        {
            var work = new LocalRect(-1920, 0, 1920, 1080);
            Check.True(OverlayLayout.PlaceOutside(work, work, 400, 130) is null);
            var side = OverlayLayout.PlaceOutside(new(-1900, 0, 1000, 1080), work, 400, 130)!;
            Check.True(side.X >= -900 && side.X + side.Width <= 0);
            Check.True(OverlayLayout.PlaceOutside(new(0, 0, 10, 10), new(0, 0, 100, 100), 400, 130) is null);
        });
        suite.Add("real.classification-edit-rich-chrome-exclusions", () =>
        {
            foreach (var cls in new[] { "Edit", "RICHEDIT50W", "RichEdit20W", "WindowsForms10.EDIT.app.0", "WindowsForms10.RichEdit50W.app.0" })
                Check.Equal("win32", TargetClassification.Classify(cls, "other", "fixture"));
            foreach (var p in new[] { "chrome", "msedge" }) Check.Equal("chromium", TargetClassification.Classify("Chrome_RenderWidgetHostHWND", "Chrome_WidgetWin_1", p));
            foreach (var c in new[] { "RichEditD2DPT", "WindowsForms10.RichEditD2DPT", "Button", "WPF" })
                Check.Equal("unsupported", TargetClassification.Classify(c, "other", "fixture"));
            Check.Equal("unsupported", TargetClassification.Classify("Chrome_RenderWidgetHostHWND", "Chrome_WidgetWin_1", "electron"));
            Check.True(!TargetClassification.HasVerifiedWin32Offsets("RICHEDIT50W", "P\r\nQ"));
            Check.True(TargetClassification.HasVerifiedWin32Offsets("Edit", "P\r\nQ"));
        });
        suite.Add("real.hook-picker-rejects-all-injected-and-own-marker", () =>
        {
            Check.True(HookEventPolicy.IsPickerClick(false, 0, 0x201));
            Check.True(!HookEventPolicy.IsPickerClick(true, 123, 0x201));
            Check.True(!HookEventPolicy.IsPickerClick(false, NativeInput.Marker, 0x201));
            Check.True(!HookEventPolicy.IsPickerClick(false, 0, 0x202));
        });
        suite.Add("real.hook-takeover-foreign-injected-own-ignored", () =>
        {
            Check.True(!HookEventPolicy.IsTakeover(true, 0x100, true, NativeInput.Marker));
            Check.True(HookEventPolicy.IsTakeover(true, 0x100, true, 123));
            Check.True(HookEventPolicy.IsTakeover(true, 0x100, false, NativeInput.Marker));
            Check.True(HookEventPolicy.IsTakeover(true, 0x101, false, 0));
            Check.True(HookEventPolicy.IsTakeover(false, 3, false, 0));
            Check.True(HookEventPolicy.IsTakeover(false, 0x201, false, 0));
            Check.True(!HookEventPolicy.IsTakeover(false, 0x202, false, 0));
            Check.True(!HookEventPolicy.IsTakeover(false, 0x200, false, 0));
            Check.True(HookEventPolicy.IsTakeover(false, 0x20A, false, 0));
        });
        suite.Add("real.unicode-events-marked-no-modifiers", () =>
        {
            var events = NativeInput.Events("A中😀");
            Check.Equal(8, events.Length);
            Check.True(events.All(e => e.Type == 1 && e.Data.Key.Vk == 0 && e.Data.Key.Extra == NativeInput.Marker
                && e.Data.Key.Flags is 4 or 6));
            Check.True(NativeInput.Delete().All(e => e.Data.Key.Vk == 0x2E && e.Data.Key.Extra == NativeInput.Marker));
        });
        suite.Add("real.validation-rejects-unsafe-ranges-control-surrogates", () =>
        {
            Check.True(!AdapterValidation.Valid("abc", new(2, 9, ""), new(0, 0, "none")));
            Check.True(!AdapterValidation.Valid("A😀", new(2, 1, ""), new(0, 0, "none")));
            Check.True(!AdapterValidation.Valid("", new(0, 0, "\n"), new(1, 1, "none")));
            Check.True(!AdapterValidation.Valid("", new(0, 0, "a"), new(2, 2, "none")));
        });
        suite.Add("real.win32-single-character-preserves-context", async () =>
        {
            var api = new EditApi("PGoagleQ", 7); var adapter = new Win32EditAdapter(api, _ => true);
            await adapter.BindAsync(Target, default);
            var outcome = await adapter.ApplyMinimalEditAsync(Target, new(3, 1, "o"), new(7, 7, "none"), () => true, default);
            Check.Equal(AdapterOutcome.Completed, outcome); Check.Equal("PGoogleQ", api.State.Text);
            Check.Equal(1, api.Replaces); Check.Equal(new MinimalEdit(3, 1, "o"), api.Edits.Single());
        });
        suite.Add("real.win32-recheck-before-replace", async () =>
        {
            var api = new EditApi("abc", 3); var adapter = new Win32EditAdapter(api, _ => true);
            await adapter.BindAsync(Target, default); api.OnSelect = () => api.Allow = false;
            Check.Equal(AdapterOutcome.Unknown, await adapter.ApplyMinimalEditAsync(Target, new(3, 0, "x"), new(4, 4, "none"), () => api.Allow, default));
            Check.Equal(0, api.Replaces);
        });
        suite.Add("real.win32-timeout-late-dispatch-unknown-no-retry", async () =>
        {
            var api = new EditApi("abc", 3); var adapter = new Win32EditAdapter(api, _ => true);
            await adapter.BindAsync(Target, default); api.TimeoutAfterReplace = true;
            Check.Equal(AdapterOutcome.Unknown, await adapter.ApplyMinimalEditAsync(Target, new(3, 0, "x"), new(4, 4, "none"), () => true, default));
            Check.Equal("abcx", api.State.Text); Check.Equal(1, api.Replaces);
        });
        suite.Add("real.win32-diverged-before-edit-zero-messages", async () =>
        {
            var api = new EditApi("abc", 3); var adapter = new Win32EditAdapter(api, _ => true);
            await adapter.BindAsync(Target, default); api.State = new("changed", 0, 0);
            Check.Equal(AdapterOutcome.Rejected, await adapter.ApplyMinimalEditAsync(Target, new(3, 0, "x"), new(4, 4, "none"), () => true, default));
            Check.Equal(0, api.Selects); Check.Equal(0, api.Replaces);
        });
        suite.Add("real.win32-readback-mismatch-no-rewrite", async () =>
        {
            var api = new EditApi("abc", 3) { Corrupt = true }; var adapter = new Win32EditAdapter(api, _ => true);
            await adapter.BindAsync(Target, default);
            Check.Equal(AdapterOutcome.Unknown, await adapter.ApplyMinimalEditAsync(Target, new(3, 0, "x"), new(4, 4, "none"), () => true, default));
            Check.Equal(1, api.Replaces);
        });
        suite.Add("real.win32-guard-rejected-zero-operations", async () =>
        {
            var api = new EditApi("abc", 3); bool safe = true; var adapter = new Win32EditAdapter(api, _ => safe);
            await adapter.BindAsync(Target, default); safe = false;
            Check.Equal(AdapterOutcome.Rejected, await adapter.ApplyMinimalEditAsync(Target, new(3, 0, "x"), new(4, 4, "none"), () => true, default));
            Check.Equal(0, api.Selects); Check.Equal(0, api.Replaces);
        });
        suite.Add("real.chromium-select-single-replace-readback", async () =>
        {
            var (a, api, t) = await Browser("Goagle", 6);
            Check.Equal(AdapterOutcome.Completed, await a.ApplyMinimalEditAsync(t, new(2, 1, "o"), new(6, 6, "none"), () => true, default));
            Check.Equal("Google", api.State.Text); Check.Equal(1, api.Injections);
        });
        suite.Add("real.chromium-last-focus-check-no-input", async () =>
        {
            var (a, api, t) = await Browser("", 0); api.FinalSafe = false;
            Check.Equal(AdapterOutcome.Unknown, await a.ApplyMinimalEditAsync(t, new(0, 0, "G"), new(1, 1, "none"), () => true, default));
            Check.Equal(0, api.Injections);
        });
        suite.Add("real.chromium-focus-race-after-final-check-partial", async () =>
        {
            var (a, api, t) = await Browser("", 0); api.LoseFocusOnInject = true;
            Check.Equal(AdapterOutcome.Partial, await a.ApplyMinimalEditAsync(t, new(0, 0, "G"), new(1, 1, "none"), () => true, default));
            Check.Equal(1, api.Injections);
        });
        suite.Add("real.chromium-short-send-partial-zero-retry", async () =>
        {
            var (a, api, t) = await Browser("", 0); api.ShortSend = true;
            Check.Equal(AdapterOutcome.Partial, await a.ApplyMinimalEditAsync(t, new(0, 0, "Go"), new(2, 2, "none"), () => true, default));
            Check.Equal(1, api.Injections);
        });
        suite.Add("real.chromium-emoji-tail-keeps-existing-caret", async () =>
        {
            var (a, api, t) = await Browser("A😀", 3);
            Check.Equal(AdapterOutcome.Completed, await a.ApplyMinimalEditAsync(t, new(3, 0, "😀"), new(5, 5, "none"), () => true, default));
            Check.Equal(0, api.Selects); Check.Equal("A😀😀", api.State.Text);
        });
        suite.Add("real.chromium-emoji-middle-rejected-zero-input", async () =>
        {
            var (a, api, t) = await Browser("A😀", 3);
            Check.Equal(AdapterOutcome.Rejected, await a.ApplyMinimalEditAsync(t, new(0, 1, "B"), new(3, 3, "none"), () => true, default));
            Check.Equal(0, api.Injections); Check.Equal(0, api.Selects);
        });
        suite.Add("real.chromium-readback-diverged-zero-input", async () =>
        {
            var (a, api, t) = await Browser("a", 1); api.State = new("b", 1, 1);
            Check.Equal(AdapterOutcome.Rejected, await a.ApplyMinimalEditAsync(t, new(1, 0, "c"), new(2, 2, "none"), () => true, default));
            Check.Equal(0, api.Injections);
        });
        suite.Add("real.safety-blacklist-reasons-and-zero-injection-policy", () =>
        {
            var good = new FakeSafetyProbe().Inspect(Target);
            Check.Equal("physical_input", SafetyPolicy.Check(Target, good with { PhysicalInputPending = true }));
            foreach (var process in new[] { "WindowsTerminal", "conhost", "cmd", "powershell", "pwsh", "wt", "OpenConsole", "consent", "LogonUI" })
            {
                var bad = good with { ProcessName = process };
                Check.Equal("unsupported", SafetyPolicy.Check(Target, bad)); Check.True(SafetyPolicy.Explain(Target, bad).Length > 5);
            }
            Check.True(SafetyPolicy.Check(Target, good with { IsPassword = true }) is not null);
        });
    }
    private static async Task<(ChromiumUiaAdapter, BrowserApi, TargetCandidate)> Browser(string text, int caret)
    {
        var api = new BrowserApi { State = new(text, caret, caret) };
        var adapter = new ChromiumUiaAdapter(api); var t = Target with { AdapterKind = "chromium", AutomationIdentity = "test-runtime-id" };
        await adapter.BindAsync(t, default); return (adapter, api, t);
    }
    private sealed class EditApi(string text, int caret) : IWin32EditApi
    {
        public TargetReadBack State = new(text, caret, caret);
        public int Selects, Replaces; public bool Allow = true, TimeoutAfterReplace, Corrupt; public Action? OnSelect;
        public List<MinimalEdit> Edits = [];
        public TargetReadBack Read(nint hwnd) => State;
        public void Select(nint hwnd, int start, int end) { Selects++; State = State with { SelectionStart = start, SelectionEnd = end }; OnSelect?.Invoke(); }
        public void Replace(nint hwnd, string value)
        {
            Replaces++; Edits.Add(new(State.SelectionStart, State.SelectionEnd - State.SelectionStart, value));
            var next = State.Text.Remove(State.SelectionStart, State.SelectionEnd - State.SelectionStart).Insert(State.SelectionStart, value);
            var caret = State.SelectionStart + value.Length; State = new(next + (Corrupt ? "!" : ""), caret, caret);
            if (TimeoutAfterReplace) throw new TimeoutException();
        }
    }
    private sealed class BrowserApi : IChromiumUiaApi
    {
        public TargetReadBack State = new("", 0, 0); public bool FinalSafe = true, LoseFocusOnInject, ShortSend, LostFocus;
        public int Injections, Selects;
        public TargetReadBack Read(TargetCandidate t) => LostFocus ? throw new InvalidOperationException("fake_focus_lost") : State;
        public void Select(TargetCandidate t, int start, int length) { Selects++; State = State with { SelectionStart = start, SelectionEnd = start + length }; }
        public bool FinalCheck(TargetCandidate t) => FinalSafe;
        public uint Inject(TargetCandidate target, string text, bool deleteSelection, Func<bool> mayApply)
        {
            if (!mayApply()) return 0;
            Injections++;
            var next = State.Text.Remove(State.SelectionStart, State.SelectionEnd - State.SelectionStart).Insert(State.SelectionStart, text);
            var caret = State.SelectionStart + text.Length; State = new(next, caret, caret); LostFocus = LoseFocusOnInject;
            return ShortSend ? 1u : (uint)(text.Length > 0 ? text.Length * 2 : deleteSelection ? 2 : 0);
        }
    }
}
