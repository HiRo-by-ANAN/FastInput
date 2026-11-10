using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace FastInput.Windows.Realtime;

// Intentionally has no fields for text, URL, title, token, capability or external error messages.
public sealed record AuditEntry(string? SessionId, string? CredentialId, string ProgramName,
    string Adapter, long Sequence, int CharacterCount, long LatencyMilliseconds, string Result, string? Reason,
    string Event = "text");

public sealed class AuditLog : IAuditLog
{
    private readonly object _gate = new();
    private readonly string _directory;
    private readonly long _maximumBytes;
    private readonly int _retainedFiles;
    public static string DefaultDirectory => Environment.GetEnvironmentVariable("FASTINPUT_REALTIME_AUDIT_PATH")
        ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "FastInput", "audit");

    public AuditLog(string? directory = null, long maximumBytes = 262144, int retainedFiles = 4)
    {
        if (maximumBytes < 1024 || retainedFiles < 1) throw new ArgumentOutOfRangeException(nameof(maximumBytes));
        _directory = directory ?? DefaultDirectory;
        _maximumBytes = maximumBytes;
        _retainedFiles = retainedFiles;
    }

    public void Write(AuditEntry entry)
    {
        var json = JsonSerializer.Serialize(new
        {
            time = DateTimeOffset.UtcNow, session = Hash(entry.SessionId), credential = Hash(entry.CredentialId),
            @event = entry.Event == "enter" ? "enter" : "text",
            program = entry.Event == "enter" && FrameCodec.IsAppLabel(entry.ProgramName) ? entry.ProgramName : SafetyPolicy.ProgramAlias(entry.ProgramName),
            adapter = entry.Adapter is "fake" or "win32" or "chromium" ? entry.Adapter : "other",
            seq = Math.Clamp(entry.Sequence, 0, 10000), characters = Math.Clamp(entry.CharacterCount, 0, 4096),
            latency_ms = Math.Max(0, entry.LatencyMilliseconds),
            result = AllowedResult(entry.Result), reason = AllowedReason(entry.Reason)
        }) + Environment.NewLine;
        lock (_gate)
        {
            Directory.CreateDirectory(_directory);
            var path = Path.Combine(_directory, "realtime.jsonl");
            if (File.Exists(path) && new FileInfo(path).Length + Encoding.UTF8.GetByteCount(json) > _maximumBytes)
            {
                var last = path + "." + _retainedFiles;
                if (File.Exists(last)) File.Delete(last);
                for (var i = _retainedFiles - 1; i >= 1; i--)
                    if (File.Exists(path + "." + i)) File.Move(path + "." + i, path + "." + (i + 1), true);
                File.Move(path, path + ".1", true);
            }
            File.AppendAllText(path, json, new UTF8Encoding(false));
        }
    }

    private static string? Hash(string? value) => value is null ? null
        : Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)))[..16].ToLowerInvariant();
    private static string AllowedResult(string result) => result is "applied" or "cancelled" or "rejected" or "partial" or "unknown"
        or "granted" or "revoked" or "binding" or "active" ? result : "rejected";
    public static string? AllowedReason(string? reason) => reason is null ? null : NativeReasons.IsValid(reason) ? reason : "rejected";
}
