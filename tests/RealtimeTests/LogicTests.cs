using FastInput.Windows.Realtime;

namespace FastInput.RealtimeTests;

internal static class LogicTests
{
    public static void Register(TestSuite suite)
    {
        suite.Add("edit.single-character-replacement", () =>
        {
            var edit = MinimalEditCalculator.Calculate("Goagle", "Google").Edit!;
            Check.Equal(2, edit.Start); Check.Equal(1, edit.DeleteLength); Check.Equal("o", edit.InsertText);
        });
        suite.Add("edit.incremental-no-clear", () =>
        {
            foreach (var pair in new[] { ("", "G"), ("G", "Go"), ("Go", "Google") })
                Check.Equal(0, MinimalEditCalculator.Calculate(pair.Item1, pair.Item2).Edit!.DeleteLength);
        });
        suite.Add("edit.emoji-and-context-boundaries", () =>
        {
            Check.True(MinimalEditCalculator.Calculate("A😀", "A😀B").Allowed);
            Check.True(MinimalEditCalculator.Calculate("A", "A😀").Allowed);
            Check.True(!MinimalEditCalculator.Calculate("A😀B", "C😀B").Allowed);
            Check.True(!MinimalEditCalculator.Calculate("A", "AB", "😀").Allowed);
            Check.True(!MinimalEditCalculator.Calculate("A", "AB", "", "😀").Allowed);
        });
        suite.Add("edit.multiple-regions-pause", () => Check.True(!MinimalEditCalculator.Calculate("abcXdefYghi", "abcZdefWghi").Allowed));
        suite.Add("safety.control-bidi-surrogate-filter", () =>
        {
            foreach (var c in new[] { '\0', '\n', '\r', '\t', '\u007f', '\u0085', '\u061c', '\u200f', '\u2028', '\u2029', '\u202e', '\u2067' })
                Check.True(!TextSafety.IsAllowed("a" + c));
            Check.True(!TextSafety.IsAllowed("\ud800")); Check.True(!TextSafety.IsAllowed("\udc00"));
            Check.True(TextSafety.IsAllowed("中😀")); Check.True(!TextSafety.IsBoundary("A😀", 2));
            Check.True(!TextSafety.IsAllowed(new string('a', 4097)));
        });
        suite.Add("safety.denied-process-session-integrity-identity", () =>
        {
            var target = Target;
            var good = new FakeSafetyProbe().Inspect(target);
            Check.True(SafetyPolicy.Check(target, good) is null);
            foreach (var bad in new[] { good with { ProcessName = "powershell.exe" }, good with { ProcessName = "cmd" },
                good with { SessionId = 2 }, good with { HostElevated = true }, good with { IntegrityLevel = 12288 },
                good with { ProcessStartTimeUtcTicks = 999 }, good with { IsHostOrDescendant = true }, good with { IsPassword = true },
                good with { IsReadOnly = true }, good with { IsEnabled = false }, good with { IsLocked = true },
                good with { IsSecureDesktop = true }, good with { IsRunDialog = true } })
                Check.True(SafetyPolicy.Check(target, bad) is not null);
        });
        suite.Add("controller.normal-verified-readback", async () =>
        {
            using var fixture = new Fixture(); await fixture.Activate();
            await fixture.Apply(1, "G"); await fixture.Apply(2, "Go"); await fixture.Apply(3, "Google");
            Check.Equal("Google", fixture.Adapter.Text); Check.Equal(3, fixture.Adapter.ApplyCount);
            Check.True(fixture.Link.Commands.OfType<ResultCommand>().All(x => x.Status == "applied" && x.Proof == "verified_readback"));
            Check.True(fixture.Adapter.Edits.All(x => x.DeleteLength == 0));
        });
        suite.Add("controller.disabled-zero-ui-zero-grant", async () =>
        {
            using var fixture = new Fixture(false); await fixture.Controller.HandleAsync(Request);
            Check.Equal(0, fixture.Ui.RequestCount); Check.Equal(0, fixture.Ui.ShowCount);
            Check.Equal(0, fixture.Link.Commands.OfType<GrantCommand>().Count());
            Check.Equal(1, fixture.Link.Commands.OfType<RevokeCommand>().Count());
        });
        suite.Add("controller.hotkey-failure-zero-ui-zero-grant", async () =>
        {
            using var fixture = new Fixture(); fixture.Controller.SetHotkeyAvailable(false);
            await fixture.Controller.HandleAsync(Request); Check.Equal(0, fixture.Ui.RequestCount);
            Check.Equal(0, fixture.Link.Commands.OfType<GrantCommand>().Count());
        });
        suite.Add("controller.approval-rejected", async () =>
        {
            using var fixture = new Fixture(); fixture.Ui.Approved = false;
            await fixture.Controller.HandleAsync(Request); Check.Equal(RealtimeState.REVOKED, fixture.Controller.State);
            Check.Equal(0, fixture.Link.Commands.OfType<GrantCommand>().Count());
        });
        suite.Add("controller.waiting-overlay-countdown", async () =>
        {
            using var fixture = new Fixture();
            await fixture.Controller.HandleAsync(Request);
            fixture.Clock.Advance(1000);
            fixture.Controller.Tick();
            Check.Equal(RealtimeState.ARMED, fixture.Ui.LastView!.State);
            Check.Equal(299000L, fixture.Ui.LastView.RemainingMilliseconds);
        });
        suite.Add("controller.approval-timeout", async () =>
        {
            using var fixture = new Fixture(); fixture.Ui.Pending = new();
            var handling = fixture.Controller.HandleAsync(Request); fixture.Clock.Advance(30000); await handling;
            Check.Equal(RealtimeState.REVOKED, fixture.Controller.State); Check.Equal(0, fixture.Link.Commands.OfType<GrantCommand>().Count());
        });
        suite.Add("controller.confirm-rechecks-target", async () =>
        {
            using var fixture = new Fixture(); await fixture.Controller.HandleAsync(Request); await fixture.Controller.BindTargetAsync(Target);
            fixture.Safety.Snapshot = fixture.Safety.Inspect(Target) with { FocusMatches = false };
            await fixture.Controller.HandleAsync(new ConfirmEmptyMessage(Request.RequestId, fixture.Controller.TargetEpoch!));
            Check.Equal(RealtimeState.REVOKED, fixture.Controller.State);
        });
        suite.Add("controller.focus-change-no-apply", async () =>
        {
            using var fixture = new Fixture(); await fixture.Activate();
            fixture.Safety.Snapshot = fixture.Safety.Inspect(Target) with { FocusMatches = false };
            await fixture.Apply(1, "A"); Check.Equal(0, fixture.Adapter.ApplyCount);
            Check.Equal(RealtimeState.PAUSED, fixture.Controller.State); Check.True(fixture.Controller.TargetEpoch is null);
        });
        suite.Add("controller.physical-input-pauses", async () =>
        {
            using var fixture = new Fixture(); await fixture.Activate(); fixture.Controller.NotifyPhysicalInput();
            await fixture.Apply(1, "A"); Check.Equal(0, fixture.Adapter.ApplyCount);
            Check.Equal(RealtimeState.PAUSED, fixture.Controller.State); Check.True(fixture.Controller.TargetEpoch is null);
        });
        suite.Add("controller.readback-mismatch-pauses-without-rewrite", async () =>
        {
            using var fixture = new Fixture(); await fixture.Activate(); fixture.Adapter.MismatchReadBack = true;
            await fixture.Apply(1, "A"); Check.Equal(RealtimeState.PAUSED, fixture.Controller.State);
            Check.Equal(1, fixture.Adapter.ApplyCount); Check.Equal("A", fixture.Adapter.Text);
            Check.Equal("rejected", fixture.Link.Commands.OfType<ResultCommand>().Last().Status);
        });
        suite.Add("controller.selection-diverged-no-apply", async () =>
        {
            using var fixture = new Fixture(); await fixture.Activate(); await fixture.Apply(1, "AB");
            fixture.Adapter.SelectionStart = fixture.Adapter.SelectionEnd = 0;
            await fixture.Apply(2, "ABC"); Check.Equal(1, fixture.Adapter.ApplyCount); Check.Equal(RealtimeState.PAUSED, fixture.Controller.State);
        });
        suite.Add("controller.adapter-timeout-is-unknown", async () =>
        {
            using var fixture = new Fixture(); await fixture.Activate(); fixture.Adapter.DelayMilliseconds = 2000;
            var handling = fixture.Apply(1, "A"); fixture.Clock.Advance(1500); await handling;
            Check.Equal(RealtimeState.UNKNOWN, fixture.Controller.State);
            Check.Equal("unknown", fixture.Link.Commands.OfType<ResultCommand>().Last().Status);
            Check.Equal(0, fixture.Adapter.ApplyCount);
        });
        suite.Add("controller.unknown-partial-do-not-rebind", async () =>
        {
            using var fixture = new Fixture(); await fixture.Activate(); fixture.Adapter.Outcome = AdapterOutcome.Partial;
            await fixture.Apply(1, "A"); Check.Equal(RealtimeState.UNKNOWN, fixture.Controller.State);
            await fixture.Controller.BindTargetAsync(Target); Check.True(fixture.Controller.TargetEpoch is null);
            Check.Equal(1, fixture.Adapter.ApplyCount);
        });
        suite.Add("controller.disconnect-revokes", async () =>
        {
            using var fixture = new Fixture(); await fixture.Activate(); fixture.Controller.Disconnected();
            await fixture.Apply(1, "A"); Check.Equal(RealtimeState.REVOKED, fixture.Controller.State); Check.Equal(0, fixture.Adapter.ApplyCount);
        });
        suite.Add("controller.emergency-clears-queue-no-new-operations", async () =>
        {
            using var fixture = new Fixture(); await fixture.Activate(); fixture.Adapter.DelayMilliseconds = 2000;
            var first = fixture.Apply(1, "A"); var second = fixture.Apply(2, "AB");
            fixture.Controller.EmergencyStop(); Check.Equal(0, fixture.Controller.PendingCount);
            Check.True(fixture.Link.ClearCount > 0); await Task.WhenAll(first, second);
            Check.Equal(0, fixture.Adapter.ApplyCount); await fixture.Apply(3, "ABC"); Check.Equal(0, fixture.Adapter.ApplyCount);
        });
        suite.Add("controller.lease-idle-limits", async () =>
        {
            using var fixture = new Fixture(); await fixture.Activate(); fixture.Clock.Advance(60000); fixture.Controller.Tick();
            Check.Equal(RealtimeState.REVOKED, fixture.Controller.State); Check.True(fixture.Controller.TargetEpoch is null);
        });
        suite.Add("controller.selection-only-does-not-extend-idle", async () =>
        {
            using var fixture = new Fixture(); await fixture.Activate(); await fixture.Apply(1, "A");
            fixture.Clock.Advance(59999); await fixture.Apply(2, "A"); fixture.Clock.Advance(1); fixture.Controller.Tick();
            Check.Equal(RealtimeState.REVOKED, fixture.Controller.State);
        });
        suite.Add("controller.absolute-lease-cannot-extend", async () =>
        {
            using var fixture = new Fixture(); await fixture.Activate();
            for (var i = 1; i <= 5; i++) { fixture.Clock.Advance(59000); await fixture.Apply(i, new string('A', i)); }
            fixture.Clock.Advance(5000); fixture.Controller.Tick();
            Check.Equal(RealtimeState.REVOKED, fixture.Controller.State); Check.Equal(5, fixture.Adapter.ApplyCount);
        });
        suite.Add("controller.send-failure-remains-revoked", async () =>
        {
            using var fixture = new Fixture(); await fixture.Activate(); fixture.Link.Connected = false;
            fixture.Safety.Snapshot = fixture.Safety.Inspect(Target) with { FocusMatches = false };
            await fixture.Apply(1, "A"); Check.Equal(RealtimeState.REVOKED, fixture.Controller.State);
            Check.True(fixture.Controller.TargetEpoch is null); Check.Equal(0, fixture.Adapter.ApplyCount);
        });
        suite.Add("controller.pause-inflight-preserves-unknown", async () =>
        {
            using var fixture = new Fixture(); await fixture.Activate(); fixture.Adapter.DelayMilliseconds = 2000;
            var pending = fixture.Apply(1, "A"); fixture.Controller.Pause();
            Check.Equal(RealtimeState.UNKNOWN, fixture.Controller.State); await pending;
            await fixture.Controller.HandleAsync(Request); Check.Equal(RealtimeState.UNKNOWN, fixture.Controller.State);
            Check.Equal(1, fixture.Ui.RequestCount);
        });
        suite.Add("controller.timeout-late-side-effect-stays-unknown", async () =>
        {
            using var fixture = new Fixture(); await fixture.Activate();
            fixture.Adapter.DelayMilliseconds = 60; fixture.Adapter.IgnoreCancellation = true; fixture.Adapter.DispatchBeforeDelay = true;
            var pending = fixture.Apply(1, "A"); fixture.Clock.Advance(1500); await pending;
            Check.Equal(RealtimeState.UNKNOWN, fixture.Controller.State);
            await Task.Delay(100); Check.Equal(1, fixture.Adapter.ApplyCount); Check.Equal("A", fixture.Adapter.Text);
            Check.Equal(0, fixture.Link.Commands.OfType<ResultCommand>().Count(x => x.Status == "applied"));
            Check.Equal(RealtimeState.UNKNOWN, fixture.Controller.State);
        });
        suite.Add("controller.stale-revoke-cannot-stop-new-request", async () =>
        {
            using var fixture = new Fixture(); await fixture.Activate();
            await fixture.Controller.HandleAsync(new RevokeMessage(new string('f', 32), "released"));
            Check.Equal(RealtimeState.ACTIVE, fixture.Controller.State);
        });
        suite.Add("controller.epoch-rebind-resets-owned-text", async () =>
        {
            using var fixture = new Fixture(); await fixture.Activate(); var old = fixture.Controller.TargetEpoch;
            await fixture.Apply(1, "abcXdefYghi"); await fixture.Apply(2, "abcZdefWghi");
            Check.Equal(RealtimeState.PAUSED, fixture.Controller.State);
            await fixture.Controller.BindTargetAsync(Target); Check.True(old != fixture.Controller.TargetEpoch);
            await fixture.Controller.HandleAsync(new ConfirmEmptyMessage(Request.RequestId, fixture.Controller.TargetEpoch!));
            await fixture.Apply(1, "A"); Check.Equal("abcXdefYghiA", fixture.Adapter.Text);
        });
        suite.Add("audit.no-sensitive-input-and-rotation-bound", async () =>
        {
            var path = DataPath(); var log = new AuditLog(path, 1024, 2);
            const string secret = "SPECIAL_PRIVATE_TEXT_9e27";
            using (var fixture = new Fixture(audit: log)) { await fixture.Activate(); await fixture.Apply(1, secret); }
            var initialFiles = Directory.GetFiles(path);
            Check.True(initialFiles.All(x => !File.ReadAllText(x).Contains(secret, StringComparison.Ordinal)));
            var initialBytes = initialFiles.Sum(x => new FileInfo(x).Length);
            for (var i = 0; i < 30; i++) log.Write(new(secret, secret, secret, secret, i, 10, 2, secret, secret));
            var files = Directory.GetFiles(path);
            Check.True(files.Length <= 3); Check.True(files.All(x => new FileInfo(x).Length <= 1024));
            Check.True(files.All(x => !File.ReadAllText(x).Contains(secret, StringComparison.Ordinal)));
            Console.WriteLine($"AUDIT initial_secret_occurrences=0 initial_bytes={initialBytes} rotated_secret_occurrences=0 files={files.Length} total_bytes={files.Sum(x => new FileInfo(x).Length)}");
        });
        suite.Add("settings.defaults-and-persistence", () =>
        {
            var path = Path.Combine(DataPath(), "settings.json"); var settings = RealtimeSettings.Load(path);
            Check.True(!settings.RealtimeEnabled && !settings.VirtualEnterEnabled);
            settings.RealtimeEnabled = true; settings.VirtualEnterEnabled = true; settings.Save(path);
            var loaded = RealtimeSettings.Load(path); Check.True(loaded.RealtimeEnabled && loaded.VirtualEnterEnabled);
            File.WriteAllText(path, "invalid"); loaded = RealtimeSettings.Load(path); Check.True(!loaded.RealtimeEnabled && !loaded.VirtualEnterEnabled);
        });
        suite.Add("settings.environment-path-overrides", () =>
        {
            var path = Path.Combine(DataPath(), "environment-settings.json");
            var auditPath = DataPath();
            var previousSettings = Environment.GetEnvironmentVariable("FASTINPUT_REALTIME_SETTINGS_PATH");
            var previousAudit = Environment.GetEnvironmentVariable("FASTINPUT_REALTIME_AUDIT_PATH");
            try
            {
                Environment.SetEnvironmentVariable("FASTINPUT_REALTIME_SETTINGS_PATH", path);
                Environment.SetEnvironmentVariable("FASTINPUT_REALTIME_AUDIT_PATH", auditPath);
                new RealtimeSettings { RealtimeEnabled = true }.Save();
                Check.True(RealtimeSettings.Load().RealtimeEnabled); Check.Equal(path, RealtimeSettings.DefaultPath);
                Check.Equal(auditPath, AuditLog.DefaultDirectory);
            }
            finally
            {
                Environment.SetEnvironmentVariable("FASTINPUT_REALTIME_SETTINGS_PATH", previousSettings);
                Environment.SetEnvironmentVariable("FASTINPUT_REALTIME_AUDIT_PATH", previousAudit);
            }
        });
    }

    private static string DataPath()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "test-data", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path); return path;
    }
    private static RequestMessage Request => new(new string('a', 32), "07", "fixture phone");
    private static TargetCandidate Target => new("fake-local-only", 123, 456, new(10, 20, 100, 30), new(0, 0, 1000, 800));

    private sealed class Fixture : IDisposable
    {
        public TestLink Link { get; } = new();
        public TestClock Clock { get; } = new();
        public TestUi Ui { get; } = new();
        public FakeAdapter Adapter { get; } = new();
        public FakeSafetyProbe Safety { get; } = new();
        public RealtimeController Controller { get; }
        public Fixture(bool enabled = true, IAuditLog? audit = null)
        {
            Controller = new(Link, Clock, Ui, new FakeTargetPicker(), Adapter, Safety,
                new RealtimeSettings { RealtimeEnabled = enabled }, audit ?? new NullAuditLog());
            Controller.SetHotkeyAvailable(true);
        }
        public async Task Activate()
        {
            await Controller.HandleAsync(Request); await Controller.BindTargetAsync(Target);
            await Controller.HandleAsync(new ConfirmEmptyMessage(Request.RequestId, Controller.TargetEpoch!));
            Check.Equal(RealtimeState.ACTIVE, Controller.State);
        }
        public Task Apply(long seq, string text) => Controller.HandleAsync(new ApplyMessage(Request.RequestId,
            Controller.TargetEpoch ?? "invalid-old-epoch", seq, seq - 1, text, new(text.Length, text.Length, "none"), "committed"));
        public void Dispose() => Controller.Dispose();
    }
    private sealed class TestLink : INativeLink
    {
        public List<NativeCommand> Commands { get; } = [];
        public int ClearCount { get; private set; }
        public bool Connected { get; set; } = true;
        public bool TrySend(NativeCommand command) { if (!Connected) return false; Commands.Add(command); return true; }
        public void ClearPending() => ClearCount++;
    }
    private sealed class TestUi : IApprovalUi
    {
        public bool Approved { get; set; } = true;
        public TaskCompletionSource<bool>? Pending { get; set; }
        public int RequestCount { get; private set; }
        public int ShowCount { get; private set; }
        public RealtimeView? LastView { get; private set; }
        public Task<bool> RequestApprovalAsync(RequestMessage request, CancellationToken cancellationToken)
        { RequestCount++; return Pending?.Task ?? Task.FromResult(Approved); }
        public void CloseApproval() { }
        public void ShowState(RealtimeView view) { ShowCount++; LastView = view; }
    }
    private sealed class TestClock : IRealtimeClock
    {
        private readonly List<(long Due, TaskCompletionSource Completion)> _delays = [];
        public long Milliseconds { get; private set; }
        public Task DelayAsync(int milliseconds, CancellationToken cancellationToken)
        {
            var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            _delays.Add((Milliseconds + milliseconds, completion));
            cancellationToken.Register(() => completion.TrySetCanceled());
            return completion.Task;
        }
        public void Advance(int milliseconds)
        {
            Milliseconds += milliseconds;
            foreach (var delay in _delays.ToArray()) if (delay.Due <= Milliseconds) delay.Completion.TrySetResult();
        }
    }
}
