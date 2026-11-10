using System.Text.Json;
using FastInput.Windows.Realtime;

namespace FastInput.RealtimeTests;

internal static class EnterTests
{
    private static readonly RequestMessage Request = new(new string('a', 32), "07", "phone");
    private static readonly TargetCandidate Target = new("local", 123, 456, new(0, 0, 10, 10), new(0, 0, 100, 100));

    public static void Register(TestSuite suite)
    {
        suite.Add("enter.default-capabilities-false-target-metadata", async () =>
        {
            using var f = new Fixture();
            await f.Controller.HandleAsync(new HelloMessage(1, new string('b', 32), ""));
            Check.True(!f.Link.Commands.OfType<CapabilitiesCommand>().Single().VirtualEnter);
            await f.Activate();
            Check.True(f.Link.Commands.OfType<TargetBoundCommand>().All(x => x.AppLabel == "fixture" && x.EnterAllowed));
        });
        suite.Add("enter.disabled-zero-injection", async () =>
        {
            using var f = new Fixture(); await f.Activate(); await f.Enter();
            Check.Equal("enter_disabled", f.Result.Reason); Check.Equal("rejected", f.Result.Status);
            Check.Equal(0, f.Adapter.EnterCount); Check.Equal(RealtimeState.PAUSED, f.Controller.State);
        });
        suite.Add("enter.blacklist-integrity-password-unsupported-zero-injection", async () =>
        {
            var good = new FakeSafetyProbe().Inspect(Target);
            foreach (var bad in new[] { good with { ProcessName = "powershell.exe" }, good with { HostElevated = true },
                good with { IntegrityLevel = 12288 }, good with { IsPassword = true }, good with { Supported = false } })
            {
                using var f = new Fixture(); await f.Activate(); f.Enable(); f.Safety.Snapshot = bad; await f.Enter();
                Check.Equal("enter_blocked_target", f.Result.Reason); Check.Equal(0, f.Adapter.EnterCount);
            }
        });
        suite.Add("enter.focus-physical-input-zero-injection", async () =>
        {
            foreach (var physical in new[] { false, true })
            {
                using var f = new Fixture(); await f.Activate(); f.Enable();
                f.Safety.Snapshot = f.Safety.Inspect(Target) with { FocusMatches = false, PhysicalInputPending = physical };
                await f.Enter(); Check.Equal(0, f.Adapter.EnterCount); Check.Equal("rejected", f.Result.Status);
            }
        });
        suite.Add("enter.after-seq-mismatch-zero-injection", async () =>
        {
            using var f = new Fixture(); await f.Activate(); f.Enable(); await f.Enter(after: 1);
            Check.Equal("text_pending", f.Result.Reason); Check.Equal(0, f.Adapter.EnterCount);
        });
        suite.Add("enter.pending-text-zero-injection", async () =>
        {
            using var f = new Fixture(); await f.Activate(); f.Enable(); f.Adapter.DelayMilliseconds = 1000;
            var apply = f.Apply(1, "private"); await f.Enter(); await apply;
            Check.Equal("text_pending", f.Result.Reason); Check.Equal(0, f.Adapter.EnterCount); Check.Equal(0, f.Adapter.ApplyCount);
        });
        suite.Add("enter.duplicate-inflight-once", async () =>
        {
            using var f = new Fixture(); await f.Activate(); f.Enable(); f.Adapter.DelayMilliseconds = 50;
            var message = f.Message(); var first = f.Controller.HandleAsync(message); await f.Controller.HandleAsync(message); await first;
            Check.Equal(1, f.Adapter.EnterCount); Check.Equal(1, f.Link.Commands.OfType<EnterResultCommand>().Count());
        });
        suite.Add("enter.duplicate-completed-replays-conflict-rejected", async () =>
        {
            using var f = new Fixture(); await f.Activate(); f.Enable(); var message = f.Message();
            await f.Controller.HandleAsync(message); await f.Controller.HandleAsync(message);
            Check.Equal(1, f.Adapter.EnterCount); Check.Equal("applied", f.Result.Status);
            await f.Controller.HandleAsync(message with { AfterSeq = 1 });
            Check.Equal("sequence_conflict", f.Result.Reason); Check.Equal(1, f.Adapter.EnterCount);
        });
        suite.Add("enter.paused-no-readback-no-following-injection", async () =>
        {
            using var f = new Fixture(); await f.Activate(); f.Enable(); await f.Apply(1, "private");
            var reads = f.Adapter.ReadCount; var message = f.Message(1); await f.Controller.HandleAsync(message);
            Check.Equal(1, f.Adapter.EnterCount); Check.Equal(reads, f.Adapter.ReadCount);
            Check.Equal(RealtimeState.PAUSED, f.Controller.State); Check.True(f.Controller.TargetEpoch is null);
            Check.Equal("enter_sent", f.Ui.Last!.Reason);
            await f.Controller.HandleAsync(new ApplyMessage(message.RequestId, message.TargetEpoch, 2, 1, "privateX", new(8, 8, "none"), "committed"));
            Check.Equal(1, f.Adapter.ApplyCount); Check.Equal("private", f.Adapter.Text);
        });
        suite.Add("enter.timeout-unknown-no-retry-late-result-ignored", async () =>
        {
            using var f = new Fixture(); await f.Activate(); f.Enable(); f.Adapter.DelayMilliseconds = 100;
            f.Adapter.IgnoreCancellation = true; var message = f.Message();
            var pending = f.Controller.HandleAsync(message); f.Clock.Advance(1500); await pending;
            Check.Equal("unknown", f.Result.Status); Check.Equal(RealtimeState.PAUSED, f.Controller.State);
            await f.Controller.HandleAsync(message); await Task.Delay(150);
            Check.Equal(0, f.Adapter.EnterCount); Check.Equal("unknown", f.Result.Status);
            Check.Equal(2, f.Link.Commands.OfType<EnterResultCommand>().Count());
        });
        suite.Add("enter.disable-inflight-immediate-rejected-zero-injection", async () =>
        {
            using var f = new Fixture(); await f.Activate(); f.Enable(); f.Adapter.DelayMilliseconds = 1000;
            var pending = f.Enter(); f.Controller.SetVirtualEnterEnabled(false); await pending;
            Check.Equal("enter_disabled", f.Result.Reason); Check.Equal("rejected", f.Result.Status);
            Check.Equal(0, f.Adapter.EnterCount); Check.True(!f.Link.Commands.OfType<CapabilitiesCommand>().Last().VirtualEnter);
        });
        suite.Add("enter.setting-reread-at-final-guard", async () =>
        {
            using var f = new Fixture(); await f.Activate(); f.Enable();
            f.Adapter.BeforeEnter = () => f.Settings.VirtualEnterEnabled = false;
            await f.Enter(); Check.Equal(0, f.Adapter.EnterCount); Check.Equal("enter_disabled", f.Result.Reason);
        });
        suite.Add("enter.disable-after-dispatch-unknown-no-retry", async () =>
        {
            using var f = new Fixture(); await f.Activate(); f.Enable();
            f.Adapter.DelayMilliseconds = 50; f.Adapter.DispatchBeforeDelay = true; f.Adapter.IgnoreCancellation = true;
            var message = f.Message(); var pending = f.Controller.HandleAsync(message);
            Check.Equal(1, f.Adapter.EnterCount);
            f.Controller.SetVirtualEnterEnabled(false); await pending; await f.Controller.HandleAsync(message);
            Check.Equal("unknown", f.Result.Status); Check.Equal(1, f.Adapter.EnterCount);
            Check.Equal(RealtimeState.PAUSED, f.Controller.State);
        });
        suite.Add("enter.rust-timeout-revoke-preserves-paused", async () =>
        {
            using var f = new Fixture(); await f.Activate(); f.Enable(); f.Adapter.DelayMilliseconds = 1000;
            var pending = f.Enter();
            await f.Controller.HandleAsync(new RevokeMessage(Request.RequestId, "enter_sent")); await pending;
            Check.Equal(RealtimeState.PAUSED, f.Controller.State); Check.Equal("unknown", f.Result.Status);
            Check.Equal("enter_sent", f.Ui.Last!.Reason); Check.Equal(0, f.Adapter.EnterCount);
            await f.Controller.HandleAsync(new RevokeMessage(Request.RequestId, "enter_sent"));
            Check.Equal(RealtimeState.PAUSED, f.Controller.State);
        });
        suite.Add("enter.emergency-revokes-retains-terminal-result", async () =>
        {
            using var f = new Fixture(); await f.Activate(); f.Enable(); f.Adapter.DelayMilliseconds = 1000;
            var pending = f.Enter(); f.Controller.EmergencyStop(); await pending;
            Check.Equal("unknown", f.Result.Status); Check.Equal(0, f.Adapter.EnterCount);
            Check.Equal(RealtimeState.REVOKED, f.Controller.State);
            Check.Equal(1, f.Link.Pending.OfType<EnterResultCommand>().Count());
        });
        suite.Add("enter.old-epoch-cannot-inject", async () =>
        {
            using var f = new Fixture(); await f.Activate(); f.Enable();
            await f.Controller.HandleAsync(f.Message() with { TargetEpoch = "old" });
            Check.Equal("epoch_mismatch", f.Result.Reason); Check.Equal(0, f.Adapter.EnterCount);
            Check.Equal(RealtimeState.ACTIVE, f.Controller.State);
        });
        suite.Add("enter.audit-event-contains-no-text", async () =>
        {
            var path = Path.Combine(AppContext.BaseDirectory, "test-data", Guid.NewGuid().ToString("N"));
            using var f = new Fixture(new AuditLog(path)); await f.Activate(); f.Enable();
            const string secret = "NEVER_LOG_ENTER_TEXT_74c1";
            await f.Apply(1, secret); await f.Enter(1);
            var lines = File.ReadAllLines(Path.Combine(path, "realtime.jsonl"));
            Check.True(lines.All(x => !x.Contains(secret, StringComparison.Ordinal)));
            using var json = JsonDocument.Parse(lines.Last());
            Check.Equal("enter", json.RootElement.GetProperty("event").GetString());
            Check.Equal(0, json.RootElement.GetProperty("characters").GetInt32());
        });
        suite.Add("enter.single-marked-return-down-up", () =>
        {
            var events = NativeInput.Enter(); Check.Equal(2, events.Length);
            Check.Equal((ushort)0x0d, events[0].Data.Key.Vk); Check.Equal((ushort)0x0d, events[1].Data.Key.Vk);
            Check.Equal(0u, events[0].Data.Key.Flags); Check.Equal(2u, events[1].Data.Key.Flags);
            Check.True(events.All(x => x.Type == 1 && x.Data.Key.Scan == 0 && x.Data.Key.Extra == NativeInput.Marker));
        });
        suite.Add("enter.sender-final-guard-zero-and-unknown-no-retry", () =>
        {
            var api = new InputApi { Final = false }; var sender = new VirtualEnterSender(api);
            Check.Equal(AdapterOutcome.Rejected, sender.Send(Target, () => true, CancellationToken.None)); Check.Equal(0, api.Count);
            api.Final = true;
            Check.Equal(AdapterOutcome.Rejected, sender.Send(Target, () => false, CancellationToken.None)); Check.Equal(0, api.Count);
            api.Outcome = AdapterOutcome.Unknown;
            Check.Equal(AdapterOutcome.Unknown, sender.Send(Target, () => true, CancellationToken.None)); Check.Equal(1, api.Count);
        });
        suite.Add("enter.frame-strict-schema-and-label-validation", () =>
        {
            var frame = FrameCodec.EncodeJson("{\"type\":\"press_enter\",\"request_id\":\"" + Request.RequestId +
                "\",\"target_epoch\":\"epoch\",\"enter_id\":\"" + new string('c', 32) + "\",\"after_seq\":0}");
            Check.True(new FrameDecoder().Feed(frame).Single() is PressEnterMessage);
            var bad = System.Text.Encoding.UTF8.GetString(frame, 4, frame.Length - 4)[..^1] + ",\"key_code\":13}";
            Check.Throws(() => new FrameDecoder().Feed(FrameCodec.EncodeJson(bad)));
            foreach (var label in new[] { "<script>", "https://a", "C:\\a", "a\n", new string('a', 33), "a\u202e" })
                Check.Throws(() => FrameCodec.Encode(new TargetBoundCommand(Request.RequestId, "epoch", "active", label, true)));
            Check.True(FrameCodec.IsAppLabel("Chrome 測試 日本 한글._-"));
            Check.Throws(() => FrameCodec.Encode(new EnterResultCommand(Request.RequestId, "epoch", new string('c', 32), "partial")));
        });
        suite.Add("enter.uppercase-id-preserved-roundtrip", async () =>
        {
            var id = "ABCDEF0123456789ABCDEF0123456789";
            var payload = JsonSerializer.Serialize(new { type = "press_enter", request_id = Request.RequestId,
                target_epoch = "epoch", enter_id = id, after_seq = 0 });
            var decoded = (PressEnterMessage)new FrameDecoder().Feed(FrameCodec.EncodeJson(payload)).Single();
            Check.Equal(id, decoded.EnterId);
            using var f = new Fixture(); await f.Activate(); f.Enable();
            var message = f.Message() with { EnterId = id }; await f.Controller.HandleAsync(message);
            Check.Equal(id, f.Result.EnterId); Check.Equal(1, f.Adapter.EnterCount);
            using var json = JsonDocument.Parse(FrameCodec.Encode(f.Result).AsMemory(4));
            Check.Equal(id, json.RootElement.GetProperty("enter_id").GetString());
        });
    }

    private sealed class Fixture : IDisposable
    {
        public Link Link { get; } = new();
        public Clock Clock { get; } = new();
        public Ui Ui { get; } = new();
        public FakeAdapter Adapter { get; } = new();
        public FakeSafetyProbe Safety { get; } = new();
        public RealtimeSettings Settings { get; } = new() { RealtimeEnabled = true };
        public RealtimeController Controller { get; }
        public EnterResultCommand Result => Link.Commands.OfType<EnterResultCommand>().Last();
        public Fixture(IAuditLog? audit = null)
        {
            Controller = new(Link, Clock, Ui, new FakeTargetPicker(), Adapter, Safety, Settings, audit ?? new NullAuditLog());
            Controller.SetHotkeyAvailable(true);
        }
        public async Task Activate()
        {
            await Controller.HandleAsync(Request); await Controller.BindTargetAsync(Target);
            await Controller.HandleAsync(new ConfirmEmptyMessage(Request.RequestId, Controller.TargetEpoch!));
            Check.Equal(RealtimeState.ACTIVE, Controller.State);
        }
        public void Enable() => Controller.SetVirtualEnterEnabled(true);
        public PressEnterMessage Message(long after = 0) => new(Request.RequestId, Controller.TargetEpoch!, new string('c', 32), after);
        public Task Enter(long after = 0) => Controller.HandleAsync(Message(after));
        public Task Apply(long seq, string text) => Controller.HandleAsync(new ApplyMessage(Request.RequestId, Controller.TargetEpoch!,
            seq, seq - 1, text, new(text.Length, text.Length, "none"), "committed"));
        public void Dispose() => Controller.Dispose();
    }
    private sealed class Link : INativeLink
    {
        public List<NativeCommand> Commands { get; } = [];
        public List<NativeCommand> Pending { get; } = [];
        public bool TrySend(NativeCommand command) { Commands.Add(command); Pending.Add(command); return true; }
        public void ClearPending() => Pending.Clear();
    }
    private sealed class Ui : IApprovalUi
    {
        public RealtimeView? Last { get; private set; }
        public Task<bool> RequestApprovalAsync(RequestMessage request, CancellationToken token) => Task.FromResult(true);
        public void CloseApproval() { }
        public void ShowState(RealtimeView view) => Last = view;
    }
    private sealed class Clock : IRealtimeClock
    {
        private readonly List<(long Due, TaskCompletionSource Completion)> _delays = [];
        public long Milliseconds { get; private set; }
        public Task DelayAsync(int milliseconds, CancellationToken token)
        {
            var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            _delays.Add((Milliseconds + milliseconds, completion)); token.Register(() => completion.TrySetCanceled());
            return completion.Task;
        }
        public void Advance(int milliseconds)
        {
            Milliseconds += milliseconds;
            foreach (var delay in _delays.ToArray()) if (delay.Due <= Milliseconds) delay.Completion.TrySetResult();
        }
    }
    private sealed class InputApi : IVirtualEnterInputApi
    {
        public bool Final { get; set; }
        public int Count { get; private set; }
        public AdapterOutcome Outcome { get; set; } = AdapterOutcome.Completed;
        public bool FinalCheck(TargetCandidate target) => Final;
        public AdapterOutcome SendSingleEnter(TargetCandidate target, Func<bool> mayApply)
        { if (!mayApply()) return AdapterOutcome.Rejected; Count++; return Outcome; }
    }
}
