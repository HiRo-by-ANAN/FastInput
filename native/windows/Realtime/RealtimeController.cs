namespace FastInput.Windows.Realtime;

/// <summary>Final local authority. No incoming message can create a local target.</summary>
public sealed class RealtimeController : IDisposable
{
    private readonly object _gate = new();
    private readonly SemaphoreSlim _applyGate = new(1, 1);
    private readonly INativeLink _link;
    private readonly IRealtimeClock _clock;
    private readonly IApprovalUi _ui;
    private readonly ITargetPicker _picker;
    private readonly ITargetAdapter _adapter;
    private readonly ISafetyProbe _safety;
    private readonly RealtimeSettings _settings;
    private readonly IAuditLog _audit;
    private CancellationTokenSource _operation = new();
    private readonly HashSet<string> _epochs = [];
    private TargetCandidate? _target;
    private string _prefix = "", _suffix = "", _owned = "", _program = "other", _adapterName = "fake";
    private long _requestedAt, _grantedAt, _lastTextAt, _lastSequence, _generation, _inFlightAt;
    private bool _authorized, _hotkeyAvailable, _stopped = true, _inFlight, _disposed, _bindingInProgress;
    private ApplyMessage? _inFlightMessage;
    private int _verifiedStart, _verifiedEnd;
    private int _pending;
    private string? _targetFailure;
    private string _appLabel = "other";
    private PressEnterMessage? _enterInFlight;
    private long _enterStarted;
    private bool _enterDispatchPossible;
    private readonly Dictionary<string, (PressEnterMessage Message, EnterResultCommand? Result)> _enterHistory = [];
    public RealtimeState State { get; private set; } = RealtimeState.DISABLED;
    public string? RequestId { get; private set; }
    public string? TargetEpoch { get; private set; }
    public int PendingCount { get { lock (_gate) return _stopped ? 0 : _pending; } }
    public bool IsMonitoringTarget { get { lock (_gate) return _bindingInProgress || State is RealtimeState.BINDING or RealtimeState.ACTIVE; } }

    public RealtimeController(INativeLink link, IRealtimeClock clock, IApprovalUi ui, ITargetPicker picker,
        ITargetAdapter adapter, ISafetyProbe safety, RealtimeSettings settings, IAuditLog audit)
    {
        _link = link; _clock = clock; _ui = ui; _picker = picker; _adapter = adapter;
        _safety = safety; _settings = settings; _audit = audit;
        _picker.TargetPicked += TargetPicked;
    }

    public void SetHotkeyAvailable(bool available)
    {
        lock (_gate)
        {
            _hotkeyAvailable = available;
            if (!available && (_authorized || State == RealtimeState.REQUESTED)) RevokeLocal("unsupported", true);
        }
    }

    public void SetEnabled(bool enabled)
    {
        lock (_gate)
        {
            _settings.RealtimeEnabled = enabled;
            if (!enabled)
            {
                RevokeLocal("released", true);
                if (State != RealtimeState.UNKNOWN) State = RealtimeState.DISABLED;
                Publish();
            }
        }
    }

    public void SetVirtualEnterEnabled(bool enabled)
    {
        lock (_gate)
        {
            _settings.VirtualEnterEnabled = enabled;
            // Revoke locally before pipe I/O or persistence. An adapter still running fails its final guard.
            if (!enabled && _enterInFlight is not null)
                CompleteEnter(_enterInFlight, _enterDispatchPossible ? "unknown" : "rejected", "enter_disabled");
            Send(new CapabilitiesCommand(enabled));
        }
    }

    public async Task HandleAsync(NativeMessage message)
    {
        if (message is HelloMessage) { lock (_gate) Send(new CapabilitiesCommand(_settings.VirtualEnterEnabled)); return; }
        if (message is PressEnterMessage enter) { await EnterAsync(enter); return; }
        if (message is RequestMessage request) { await RequestAsync(request); return; }
        if (message is ApplyMessage apply) { await ApplyAsync(apply); return; }
        if (message is ConfirmEmptyMessage confirm) { await ConfirmAsync(confirm); return; }
        if (message is RevokeMessage revoke)
        {
            lock (_gate)
                if (revoke.RequestId is null || revoke.RequestId == RequestId)
                {
                    if (revoke.Reason == "enter_sent")
                    {
                        // Rust may finish its deadline before this worker. Preserve the M4 paused epoch.
                        if (_enterInFlight is not null) CompleteEnter(_enterInFlight, "unknown", "enter_sent");
                        else
                        {
                            ResetOperations(); TargetEpoch = null; _target = null; _inFlight = false; _inFlightMessage = null;
                            State = RealtimeState.PAUSED;
                            Publish("enter_sent");
                        }
                    }
                    else RevokeLocal(revoke.Reason, false);
                }
        }
    }

    private async Task RequestAsync(RequestMessage request)
    {
        long generation;
        CancellationToken token;
        lock (_gate)
        {
            if (_disposed) return;
            if (!_settings.RealtimeEnabled || !_hotkeyAvailable)
            {
                Send(new RevokeCommand(request.RequestId, "unsupported"));
                return; // Disabled requests produce no approval or overlay UI.
            }
            if (State == RealtimeState.REQUESTED && RequestId == request.RequestId) return;
            if (_authorized || State is RealtimeState.REQUESTED or RealtimeState.UNKNOWN)
            { Send(new RevokeCommand(request.RequestId, "busy")); return; }
            ResetOperations();
            RequestId = request.RequestId;
            _requestedAt = _clock.Milliseconds;
            _stopped = false;
            State = RealtimeState.REQUESTED;
            generation = _generation;
            token = _operation.Token;
        }
        bool approved;
        try
        {
            var decision = _ui.RequestApprovalAsync(request, token);
            var timeout = _clock.DelayAsync(30000, token);
            var winner = await Task.WhenAny(decision, timeout);
            approved = winner == decision && await decision;
        }
        catch (OperationCanceledException) { return; }
        catch (Exception) { approved = false; }
        lock (_gate)
        {
            if (generation != _generation || State != RealtimeState.REQUESTED) return;
            _ui.CloseApproval();
            if (!_hotkeyAvailable || !_settings.RealtimeEnabled) { RevokeLocal("unsupported", true); return; }
            if (!approved || _clock.Milliseconds - _requestedAt >= 30000)
            { RevokeLocal(approved ? "request_expired" : "rejected", true); return; }
            _authorized = true;
            _grantedAt = _lastTextAt = _clock.Milliseconds;
            _epochs.Clear();
            _enterHistory.Clear();
            _stopped = false;
            State = RealtimeState.ARMED;
            if (Send(new GrantCommand(RequestId!))) { _picker.Start(); Publish(); }
        }
    }

    private void TargetPicked(TargetCandidate target) => _ = BindTargetAsync(target);

    public async Task BindTargetAsync(TargetCandidate target)
    {
        long generation;
        CancellationToken token;
        lock (_gate)
        {
            if (State is not (RealtimeState.ARMED or RealtimeState.PAUSED) || !Authorized() || _inFlight || _bindingInProgress) return;
            var reason = CheckTarget(target);
            if (reason is not null) { ReportTargetFailure(); RevokeLocal(reason, true); return; }
            AdapterProbe probe;
            try { probe = _adapter.Probe(target); }
            catch (Exception) { RevokeLocal("unsupported", true); return; }
            if (!probe.Supported) { _targetFailure = probe.Reason; ReportTargetFailure(); RevokeLocal("unsupported", true); return; }
            _adapterName = probe.Name;
            _bindingInProgress = true;
            _picker.Stop();
            generation = _generation;
            token = _operation.Token;
        }
        try
        {
            var read = await _adapter.BindAsync(target, token);
            lock (_gate)
            {
                if (generation != _generation || !Authorized()) return;
                var reason = CheckTarget(target);
                if (reason is not null) { RevokeLocal(reason, true); return; }
                if (read.SelectionStart != read.SelectionEnd || !TextSafety.IsBoundary(read.Text, read.SelectionStart))
                { RevokeLocal("unsupported", true); return; }
                _prefix = read.Text[..read.SelectionStart]; _suffix = read.Text[read.SelectionEnd..];
                if (MinimalEditCalculator.HasSurrogate(_prefix) || MinimalEditCalculator.HasSurrogate(_suffix))
                { RevokeLocal("unsupported", true); return; }
                if (_epochs.Count >= 10000) { RevokeLocal("epoch_limit", true); return; }
                _target = target; _owned = ""; _lastSequence = 0;
                _verifiedStart = _verifiedEnd = _prefix.Length;
                TargetEpoch = Guid.NewGuid().ToString("N");
                _epochs.Add(TargetEpoch);
                State = RealtimeState.BINDING;
                _picker.Stop();
                Send(new TargetBoundCommand(RequestId!, TargetEpoch, "binding", _appLabel, EnterTargetAllowed(target)));
                Publish();
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception error)
        {
            lock (_gate) if (generation == _generation)
            {
                _targetFailure = error is TargetAccessException ? error.Message : Loc.T("realtimecontroller.cannot.read.the.target.or.verify.the.collapsed.caret");
                ReportTargetFailure(); RevokeLocal("unsupported", true);
            }
        }
        finally { lock (_gate) if (generation == _generation) _bindingInProgress = false; }
    }

    private async Task ConfirmAsync(ConfirmEmptyMessage message)
    {
        TargetCandidate target;
        long generation;
        CancellationToken token;
        lock (_gate)
        {
            if (State != RealtimeState.BINDING || !Matches(message.RequestId, message.TargetEpoch) || !Authorized() || _target is null) return;
            target = _target; generation = _generation; token = _operation.Token;
        }
        try
        {
            var read = await _adapter.ReadBackAsync(target, token);
            lock (_gate)
            {
                if (generation != _generation || State != RealtimeState.BINDING || !Authorized()) return;
                var reason = CheckTarget(target);
                if (reason is not null || read.Text != _prefix + _suffix || read.SelectionStart != _prefix.Length || read.SelectionEnd != _prefix.Length)
                { RevokeLocal(reason ?? "content_diverged", true); return; }
                State = RealtimeState.ACTIVE;
                Send(new TargetBoundCommand(RequestId!, TargetEpoch!, "active", _appLabel, EnterTargetAllowed(target)));
                Publish();
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception) { lock (_gate) if (generation == _generation) RevokeLocal("unsupported", true); }
    }

    private async Task ApplyAsync(ApplyMessage message)
    {
        long generation;
        long receivedAt;
        CancellationToken token;
        lock (_gate)
        {
            if (State != RealtimeState.ACTIVE || _stopped || !Matches(message.RequestId, message.TargetEpoch)) return;
            if (_enterInFlight is not null) { Send(new ResultCommand(message.RequestId, message.TargetEpoch, message.Seq, "rejected", "enter_busy")); return; }
            if (_pending >= 32) { RevokeLocal("queue_full", true); return; }
            generation = _generation; receivedAt = _clock.Milliseconds; token = _operation.Token;
            _pending++;
        }
        var acquired = false;
        try
        {
            await _applyGate.WaitAsync(token);
            acquired = true;
            lock (_gate)
            {
                if (generation != _generation) return;
                if (_clock.Milliseconds - receivedAt >= 1500) { FailApply(message, "rejected", "stale_state"); return; }
            }
            await ApplyOneAsync(message, generation);
        }
        catch (OperationCanceledException) { }
        finally
        {
            lock (_gate) _pending--;
            if (acquired) _applyGate.Release();
        }
    }

    private async Task ApplyOneAsync(ApplyMessage message, long generation)
    {
        TargetCandidate target;
        CancellationToken token;
        long started;
        lock (_gate)
        {
            if (generation != _generation || State != RealtimeState.ACTIVE || !Matches(message.RequestId, message.TargetEpoch)
                || !Authorized() || _target is null) return;
            if (message.Seq != _lastSequence + 1 || message.PrevSeq != _lastSequence)
            { FailApply(message, "rejected", "sequence_conflict"); return; }
            if (!ValidState(message)) { FailApply(message, "rejected", "invalid_state"); return; }
            var reason = CheckTarget(_target);
            if (reason is not null) { FailApply(message, "rejected", reason); return; }
            target = _target; token = _operation.Token;
            started = _inFlightAt = _clock.Milliseconds;
            _inFlight = true;
            _inFlightMessage = message;
        }
        try
        {
            var work = PerformEditAsync(message, target, generation, token);
            var timeout = _clock.DelayAsync(1500, token);
            var winner = await Task.WhenAny(work, timeout);
            if (winner != work)
            {
                _ = ObserveAsync(work);
                lock (_gate) if (generation == _generation) FailApply(message, "unknown", "native_timeout");
                return;
            }
            var result = await work;
            lock (_gate)
            {
                if (generation != _generation) return;
                if (!Authorized()) return;
                if (_clock.Milliseconds - started >= 1500) { FailApply(message, "unknown", "native_timeout"); return; }
                if (result is not null) { FailApply(message, result == "partial" ? "partial" : result == "unknown" ? "unknown" : "rejected", result); return; }
                _inFlight = false;
                _inFlightMessage = null;
                _lastSequence = message.Seq;
                if (_owned != message.Text) _lastTextAt = _clock.Milliseconds;
                _owned = message.Text;
                _verifiedStart = _prefix.Length + message.Selection.Start;
                _verifiedEnd = _prefix.Length + message.Selection.End;
                Send(new ResultCommand(message.RequestId, message.TargetEpoch, message.Seq, "applied", null, "verified_readback"));
                Audit(message.Seq, message.Text.Length, _clock.Milliseconds - started, "applied", null);
                Publish();
            }
        }
        catch (OperationCanceledException)
        { lock (_gate) if (generation == _generation) FailApply(message, "unknown", "unknown"); }
        catch (Exception)
        { lock (_gate) if (generation == _generation) FailApply(message, "unknown", "unknown"); }
    }

    private async Task<string?> PerformEditAsync(ApplyMessage message, TargetCandidate target, long generation, CancellationToken token)
    {
        var read = await _adapter.ReadBackAsync(target, token);
        MinimalEdit edit;
        TextSelection selection;
        lock (_gate)
        {
            if (!MayApply(generation, message, target)) return "rejected";
            if (read.Text != _prefix + _owned + _suffix || read.SelectionStart != _verifiedStart || read.SelectionEnd != _verifiedEnd)
                return "content_diverged";
            var calculation = MinimalEditCalculator.Calculate(_owned, message.Text, _prefix, _suffix);
            if (!calculation.Allowed) return calculation.Reason;
            edit = calculation.Edit!;
            selection = new(_prefix.Length + message.Selection.Start, _prefix.Length + message.Selection.End, message.Selection.Direction);
        }
        var outcome = await _adapter.ApplyMinimalEditAsync(target, edit, selection,
            () => { lock (_gate) return MayApply(generation, message, target); }, token);
        if (outcome != AdapterOutcome.Completed) return outcome switch
        { AdapterOutcome.Partial => "partial", AdapterOutcome.Unknown => "unknown", _ => "rejected" };
        var after = await _adapter.ReadBackAsync(target, token);
        lock (_gate)
        {
            if (!MayApply(generation, message, target)) return "unknown";
            return after.Text == _prefix + message.Text + _suffix && after.SelectionStart == selection.Start
                && after.SelectionEnd == selection.End ? null : "content_diverged";
        }
    }

    private static async Task ObserveAsync(Task task) { try { await task; } catch (Exception) { } }

    private bool EnterTargetAllowed(TargetCandidate target)
    {
        try { return _adapter is IVirtualEnterAdapter && _adapter.Probe(target).Supported && CheckTarget(target) is null; }
        catch (Exception) { return false; }
    }

    private string? EnterCheck(PressEnterMessage message, TargetCandidate? target, long generation)
    {
        if (!_settings.VirtualEnterEnabled) return "enter_disabled";
        if (generation != _generation || State != RealtimeState.ACTIVE || !Matches(message.RequestId, message.TargetEpoch)
            || !Authorized() || target is null || _target != target) return "stale_state";
        if (!EnterTargetAllowed(target)) return "enter_blocked_target";
        if (message.AfterSeq != _lastSequence || _pending != 0 || _inFlight) return "text_pending";
        return null;
    }

    private async Task EnterAsync(PressEnterMessage message)
    {
        TargetCandidate target;
        long generation;
        CancellationToken token;
        lock (_gate)
        {
            if (_enterHistory.TryGetValue(message.EnterId, out var existing))
            {
                if (existing.Message != message) Send(new EnterResultCommand(message.RequestId, message.TargetEpoch, message.EnterId, "rejected", "sequence_conflict"));
                else if (existing.Result is not null) Send(existing.Result);
                return;
            }
            if (!Matches(message.RequestId, message.TargetEpoch))
            { Send(new EnterResultCommand(message.RequestId, message.TargetEpoch, message.EnterId, "rejected", "epoch_mismatch")); return; }
            if (_enterInFlight is not null)
            { Send(new EnterResultCommand(message.RequestId, message.TargetEpoch, message.EnterId, "rejected", "enter_busy")); return; }
            if (_enterHistory.Count >= 10000) { RevokeLocal("epoch_limit", true); return; }
            _enterHistory[message.EnterId] = (message, null);
            _enterStarted = _clock.Milliseconds;
            generation = _generation;
            var reason = EnterCheck(message, _target, generation);
            if (reason is not null) { CompleteEnter(message, "rejected", reason); return; }
            target = _target!;
            token = _operation.Token;
            _enterInFlight = message;
            _enterDispatchPossible = false;
            _enterStarted = _clock.Milliseconds;
        }
        try
        {
            bool MayEnter()
            {
                lock (_gate)
                {
                    var allowed = _enterInFlight == message && EnterCheck(message, target, generation) is null;
                    // Once a guard returned true, dispatch may race a setting change. Never claim certain rejection.
                    if (allowed) _enterDispatchPossible = true;
                    return allowed;
                }
            }
            var work = ((IVirtualEnterAdapter)_adapter).PressEnterAsync(target, MayEnter, token);
            var timeout = _clock.DelayAsync(1500, token);
            var winner = await Task.WhenAny(work, timeout);
            if (winner != work)
            {
                _ = ObserveAsync(work);
                lock (_gate) if (generation == _generation && _enterInFlight == message) CompleteEnter(message, "unknown", "native_timeout");
                return;
            }
            var outcome = await work;
            lock (_gate)
            {
                if (generation != _generation || _enterInFlight != message) return;
                if (_clock.Milliseconds - _enterStarted >= 1500) { CompleteEnter(message, "unknown", "native_timeout"); return; }
                // No target readback: completed means only that the single Enter batch was dispatched.
                var status = outcome == AdapterOutcome.Completed ? "applied" : outcome == AdapterOutcome.Rejected ? "rejected" : "unknown";
                var reason = status == "applied" ? null : status == "unknown" ? "unknown" : EnterCheck(message, target, generation) ?? "enter_blocked_target";
                CompleteEnter(message, status, reason);
            }
        }
        catch (Exception)
        {
            lock (_gate) if (generation == _generation && _enterInFlight == message) CompleteEnter(message, "unknown", "unknown");
        }
    }

    private void CompleteEnter(PressEnterMessage message, string status, string? reason)
    {
        var result = new EnterResultCommand(message.RequestId, message.TargetEpoch, message.EnterId, status, AuditLog.AllowedReason(reason));
        _enterHistory[message.EnterId] = (message, result);
        _enterInFlight = null;
        _inFlight = false; _inFlightMessage = null;
        ResetOperations();
        TargetEpoch = null; _target = null;
        State = RealtimeState.PAUSED;
        _picker.Stop(); // Only a fresh, explicit local target selection can start another epoch.
        try { _audit.Write(new(RequestId, null, _appLabel, _adapterName, message.AfterSeq, 0,
            Math.Max(0, _clock.Milliseconds - _enterStarted), status, reason, "enter")); } catch (Exception) { }
        if (Send(result))
        {
            // Keep the local lease, but do not automatically bind or continue the old field.
            if (_authorized && !_stopped) _picker.Start();
            Publish("enter_sent");
        }
    }
    private bool Matches(string request, string epoch) => RequestId == request && TargetEpoch == epoch;
    private bool MayApply(long generation, ApplyMessage message, TargetCandidate target) => generation == _generation
        && State == RealtimeState.ACTIVE && Matches(message.RequestId, message.TargetEpoch) && Authorized() && CheckTarget(target) is null;
    private static bool ValidState(ApplyMessage message) => message.Seq is >= 1 and <= 10000 && message.Composition == "committed"
        && TextSafety.IsAllowed(message.Text) && message.Selection.Start <= message.Selection.End
        && TextSafety.IsBoundary(message.Text, message.Selection.Start) && TextSafety.IsBoundary(message.Text, message.Selection.End)
        && message.Selection.Direction is "none" or "forward" or "backward";

    private string? CheckTarget(TargetCandidate target)
    {
        try
        {
            var snapshot = _safety.Inspect(target); _program = SafetyPolicy.ProgramAlias(snapshot.ProcessName);
            var name = Path.GetFileNameWithoutExtension(snapshot.ProcessName);
            _appLabel = FrameCodec.IsAppLabel(name) && name.Length > 0 ? name : "other";
            var reason = SafetyPolicy.Check(target, snapshot);
            _targetFailure = reason is null ? null : SafetyPolicy.Explain(target, snapshot);
            return reason;
        }
        catch (Exception) { _targetFailure = Loc.T("realtimecontroller.native.process.permission.desktop.or.window.safety.checks.failed"); return "unsupported"; }
    }
    private void ReportTargetFailure()
    {
        if (_ui is ILocalTargetFeedback feedback) feedback.ShowTargetFailure(_targetFailure ?? Loc.T("realtimecontroller.this.target.is.not.supported"));
    }

    private bool Authorized()
    {
        if (!_authorized || _stopped || !_hotkeyAvailable || !_settings.RealtimeEnabled) return false;
        if (_clock.Milliseconds - _grantedAt >= 300000) { RevokeLocal("expired", true); return false; }
        if (_clock.Milliseconds - _lastTextAt >= 60000) { RevokeLocal("idle_timeout", true); return false; }
        return true;
    }

    public void Tick()
    {
        lock (_gate)
        {
            if (_disposed) return;
            if (State == RealtimeState.REQUESTED && _clock.Milliseconds - _requestedAt >= 30000) RevokeLocal("request_expired", true);
            if (_authorized && Authorized())
            {
                var reason = _target is null ? null : CheckTarget(_target);
                if (reason is not null) { RevokeLocal(reason, true); return; }
                if (_inFlight && _inFlightMessage is not null && _clock.Milliseconds - _inFlightAt >= 1500)
                {
                    // No cancellation claim is made for an operation already handed to the adapter.
                    FailApply(_inFlightMessage, "unknown", "native_timeout");
                    return;
                }
                if (_enterInFlight is not null && _clock.Milliseconds - _enterStarted >= 1500)
                { CompleteEnter(_enterInFlight, "unknown", "native_timeout"); return; }
                Publish();
            }
        }
    }

    private void FailApply(ApplyMessage message, string status, string reason)
    {
        if (!Send(new ResultCommand(message.RequestId, message.TargetEpoch, message.Seq, status, AuditLog.AllowedReason(reason), null))) return;
        Audit(message.Seq, message.Text.Length, Math.Max(0, _clock.Milliseconds - _inFlightAt), status, reason);
        _inFlight = false;
        _inFlightMessage = null;
        ResetOperations();
        TargetEpoch = null; _target = null;
        State = status is "unknown" or "partial" ? RealtimeState.UNKNOWN : RealtimeState.PAUSED;
        if (State == RealtimeState.UNKNOWN) { _authorized = false; _stopped = true; _picker.Stop(); }
        else _picker.Start();
        Publish(reason);
    }

    public void Pause(string reason = "released")
    {
        lock (_gate)
        {
            RevokeLocal(reason, true);
            if (State != RealtimeState.UNKNOWN) State = RealtimeState.PAUSED;
            Publish(reason);
        }
    }
    public void NotifyPhysicalInput() => Pause("physical_input");
    public void NotifyLocked() { lock (_gate) RevokeLocal("locked", true); }
    public void EmergencyStop() { lock (_gate) RevokeLocal("emergency_stop", true); }
    public void Disconnected() { lock (_gate) RevokeLocal("native_disconnected", false); }

    private void ResetOperations()
    {
        _generation++;
        _bindingInProgress = false;
        _operation.Cancel();
        _operation.Dispose();
        _operation = new();
    }

    private void RevokeLocal(string reason, bool notify)
    {
        var hadEnter = _enterInFlight is not null;
        if (hadEnter)
        {
            // Clear stale outbound work before enqueueing the terminal Enter result, never after it.
            _link.ClearPending();
            CompleteEnter(_enterInFlight!, "unknown", reason);
        }
        var request = RequestId;
        var wasInFlight = _inFlight;
        _authorized = false; _stopped = true; TargetEpoch = null; _target = null; _inFlight = false; _inFlightMessage = null;
        ResetOperations();
        if (!hadEnter) _link.ClearPending();
        _picker.Stop();
        _ui.CloseApproval();
        State = wasInFlight ? RealtimeState.UNKNOWN : RealtimeState.REVOKED;
        if (notify) _link.TrySend(new RevokeCommand(request, AuditLog.AllowedReason(reason)!));
        Audit(0, 0, 0, "revoked", reason);
        Publish(reason);
    }

    private bool Send(NativeCommand command)
    {
        if (_link.TrySend(command)) return true;
        RevokeLocal("native_disconnected", false);
        return false;
    }

    private void Audit(long seq, int count, long latency, string result, string? reason)
    {
        try { _audit.Write(new(RequestId, null, _program, _adapterName, seq, count, latency, result, reason)); }
        catch (Exception) { /* Audit persistence failure must never throw external content into logs. */ }
    }
    private void Publish(string? reason = null) => _ui.ShowState(new(State, _program,
        _authorized ? Math.Max(0, 300000 - (_clock.Milliseconds - _grantedAt)) : 0, reason, _target));

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            _picker.TargetPicked -= TargetPicked;
            RevokeLocal("released", true);
        }
    }
}
