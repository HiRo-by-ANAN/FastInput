namespace FastInput.Windows.Realtime;

// These contracts carry text intent only. Target handles and coordinates are local objects.
public abstract record NativeMessage;
public sealed record HelloMessage(int ProtocolVersion, string ServerBootId, string DesktopUrl) : NativeMessage;
public sealed record RequestMessage(string RequestId, string CheckCode, string DeviceLabel) : NativeMessage;
public sealed record ConfirmEmptyMessage(string RequestId, string TargetEpoch) : NativeMessage;
public sealed record TextSelection(int Start, int End, string Direction);
public sealed record ApplyMessage(string RequestId, string TargetEpoch, long Seq, long PrevSeq,
    string Text, TextSelection Selection, string Composition) : NativeMessage;
public sealed record RevokeMessage(string? RequestId, string Reason) : NativeMessage;
public sealed record PressEnterMessage(string RequestId, string TargetEpoch, string EnterId, long AfterSeq) : NativeMessage;

public abstract record NativeCommand;
public sealed record GrantCommand(string RequestId) : NativeCommand;
public sealed record TargetBoundCommand(string RequestId, string TargetEpoch, string Phase = "binding",
    string? AppLabel = null, bool EnterAllowed = false) : NativeCommand;
public sealed record CapabilitiesCommand(bool VirtualEnter) : NativeCommand;
public sealed record EnterResultCommand(string RequestId, string TargetEpoch, string EnterId,
    string Status, string? Reason = null) : NativeCommand;
public sealed record ResultCommand(string RequestId, string TargetEpoch, long Seq, string Status,
    string? Reason = null, string? Proof = null) : NativeCommand;
public sealed record RevokeCommand(string? RequestId, string Reason) : NativeCommand;
public sealed record HeartbeatCommand : NativeCommand;

public interface INativeLink
{
    // Must return promptly. A false return means the link can no longer be trusted.
    bool TrySend(NativeCommand command);
    void ClearPending();
}

public static class NativeReasons
{
    private static readonly HashSet<string> Values = new(StringComparer.Ordinal)
    {
        "focus_changed", "content_diverged", "expired", "unsupported", "busy", "released",
        "credential_revoked", "native_disconnected", "native_timeout", "heartbeat_timeout", "idle_timeout",
        "request_expired", "protocol_error", "sequence_conflict", "stale_state", "epoch_mismatch",
        "queue_full", "rate_limited", "epoch_limit", "invalid_state", "target_changed", "physical_input",
        "locked", "suspended", "emergency_stop", "partial", "unknown", "rejected",
        "enter_sent", "enter_disabled", "enter_blocked_target", "text_pending", "enter_busy"
    };

    public static bool IsValid(string reason) => Values.Contains(reason);
}
