using System.Diagnostics;

namespace FastInput.Windows.Realtime;

public enum RealtimeState { DISABLED, REQUESTED, ARMED, BINDING, ACTIVE, PAUSED, UNKNOWN, REVOKED }

public interface IRealtimeClock
{
    long Milliseconds { get; }
    Task DelayAsync(int milliseconds, CancellationToken cancellationToken);
}

public sealed class RealtimeClock : IRealtimeClock
{
    private readonly Stopwatch _watch = Stopwatch.StartNew();
    public long Milliseconds => _watch.ElapsedMilliseconds;
    public Task DelayAsync(int milliseconds, CancellationToken cancellationToken) => Task.Delay(milliseconds, cancellationToken);
}

public sealed record LocalRect(int X, int Y, int Width, int Height);
public sealed record TargetCandidate(string LocalIdentity, int ProcessId, long ProcessStartTimeUtcTicks,
    LocalRect Bounds, LocalRect WorkingArea)
{
    public nint NativeWindow { get; init; }
    public nint TopLevelWindow { get; init; }
    public string ProcessName { get; init; } = "";
    public string AdapterKind { get; init; } = "unsupported";
    public string? UnsupportedReason { get; init; }
    public string? AutomationIdentity { get; init; }
}
public sealed record SafetySnapshot(int ProcessId, long ProcessStartTimeUtcTicks, string ProcessName,
    int SessionId, int HostSessionId, int IntegrityLevel, int HostIntegrityLevel,
    bool HostElevated = false, bool IsHostOrDescendant = false, bool FocusMatches = true,
    bool IsPassword = false, bool IsReadOnly = false, bool IsEnabled = true,
    bool IsLocked = false, bool IsSecureDesktop = false, bool IsRunDialog = false, bool Supported = true,
    bool PhysicalInputPending = false);

public interface ISafetyProbe
{
    SafetySnapshot Inspect(TargetCandidate target);
}

public interface ITargetPicker
{
    event Action<TargetCandidate>? TargetPicked;
    void Start();
    void Stop();
}

public sealed record RealtimeView(RealtimeState State, string ProgramName, long RemainingMilliseconds, string? Reason,
    TargetCandidate? Target);

public interface IApprovalUi
{
    Task<bool> RequestApprovalAsync(RequestMessage request, CancellationToken cancellationToken);
    void CloseApproval();
    void ShowState(RealtimeView view);
}

public interface ILocalTargetFeedback { void ShowTargetFailure(string reason); }
public sealed class TargetAccessException(string reason) : Exception(reason);

public sealed record TargetReadBack(string Text, int SelectionStart, int SelectionEnd);
public sealed record AdapterProbe(bool Supported, string Name = "fake", string? Reason = null);
public enum AdapterOutcome { Completed, Rejected, Partial, Unknown }

/// <summary>All positions are absolute UTF-16 offsets. Implementations must recheck authorization immediately before side effects.</summary>
public interface ITargetAdapter
{
    AdapterProbe Probe(TargetCandidate target);
    Task<TargetReadBack> BindAsync(TargetCandidate target, CancellationToken cancellationToken);
    Task<TargetReadBack> ReadBackAsync(TargetCandidate target, CancellationToken cancellationToken);
    Task<AdapterOutcome> ApplyMinimalEditAsync(TargetCandidate target, MinimalEdit edit,
        TextSelection selection, Func<bool> mayApply, CancellationToken cancellationToken);
}

public interface IAuditLog
{
    void Write(AuditEntry entry);
}

/// <summary>A single Enter intent. Implementations must never retry or read the field after dispatch.</summary>
public interface IVirtualEnterAdapter
{
    Task<AdapterOutcome> PressEnterAsync(TargetCandidate target, Func<bool> mayApply, CancellationToken cancellationToken);
}

public sealed class NullAuditLog : IAuditLog
{
    public void Write(AuditEntry entry) { }
}

/// <summary>M2a picker never queries or clicks the desktop. A test or explicit local demo invokes Pick.</summary>
public sealed class FakeTargetPicker : ITargetPicker
{
    public event Action<TargetCandidate>? TargetPicked;
    public bool IsPicking { get; private set; }
    public void Start() => IsPicking = true;
    public void Stop() => IsPicking = false;
    public void Pick(TargetCandidate target) { if (IsPicking) TargetPicked?.Invoke(target); }
}

public sealed class FakeSafetyProbe : ISafetyProbe
{
    public SafetySnapshot? Snapshot { get; set; }
    public SafetySnapshot Inspect(TargetCandidate target) => Snapshot ?? new(target.ProcessId,
        target.ProcessStartTimeUtcTicks, "fixture", 1, 1, 8192, 8192);
}
