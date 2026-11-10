using System.Buffers.Binary;
using System.Text;
using System.Text.Json;

namespace FastInput.Windows.Realtime;

public static class FrameCodec
{
    public const int MaximumPayloadBytes = 65_536;
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    public static byte[] Encode(NativeCommand command)
    {
        object payload = command switch
        {
            GrantCommand grant when IsId(grant.RequestId) => new { type = "grant", request_id = grant.RequestId },
            TargetBoundCommand bound when IsId(bound.RequestId) && IsEpoch(bound.TargetEpoch)
                && bound.Phase is "binding" or "active" && (bound.AppLabel is null || IsAppLabel(bound.AppLabel)) => new
                { type = "target_bound", request_id = bound.RequestId, target_epoch = bound.TargetEpoch, phase = bound.Phase,
                    app_label = bound.AppLabel, enter_allowed = bound.EnterAllowed },
            CapabilitiesCommand capabilities => new { type = "capabilities", virtual_enter = capabilities.VirtualEnter },
            EnterResultCommand enter when IsId(enter.RequestId) && IsEpoch(enter.TargetEpoch) && IsEnterId(enter.EnterId)
                && enter.Status is "applied" or "rejected" or "unknown" && (enter.Reason is null || NativeReasons.IsValid(enter.Reason)) => new
                { type = "enter_result", request_id = enter.RequestId, target_epoch = enter.TargetEpoch,
                    enter_id = enter.EnterId, status = enter.Status, reason = enter.Reason },
            ResultCommand result when ValidResult(result) => new
                { type = "result", request_id = result.RequestId, target_epoch = result.TargetEpoch,
                    seq = result.Seq, status = result.Status, reason = result.Reason, proof = result.Proof },
            RevokeCommand revoke when (revoke.RequestId is null || IsId(revoke.RequestId))
                && NativeReasons.IsValid(revoke.Reason) => new
                { type = "revoke", request_id = revoke.RequestId, reason = revoke.Reason },
            HeartbeatCommand => new { type = "heartbeat" },
            _ => throw InvalidFrame()
        };
        return EncodeJson(JsonSerializer.Serialize(payload));
    }

    // Also useful for protocol conformance tests which deliberately send invalid JSON.
    public static byte[] EncodeJson(string json)
    {
        byte[] bytes;
        try { bytes = StrictUtf8.GetBytes(json); }
        catch (EncoderFallbackException) { throw InvalidFrame(); }
        if (bytes.Length is < 1 or > MaximumPayloadBytes) throw InvalidFrame();
        var frame = new byte[bytes.Length + 4];
        BinaryPrimitives.WriteInt32LittleEndian(frame, bytes.Length);
        bytes.CopyTo(frame, 4);
        return frame;
    }

    internal static NativeMessage Decode(ReadOnlySpan<byte> bytes)
    {
        try
        {
            var json = StrictUtf8.GetString(bytes);
            using var document = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = 8 });
            var root = document.RootElement;
            var type = RequiredString(root, "type");
            switch (type)
            {
                case "hello":
                    Fields(root, "type", "protocol_version", "server_boot_id", "desktop_url");
                    var version = RequiredInt(root, "protocol_version");
                    var bootId = RequiredString(root, "server_boot_id");
                    var url = RequiredString(root, "desktop_url");
                    if (version != 1 || !IsId(bootId) || !ValidDesktopUrl(url)) throw InvalidFrame();
                    return new HelloMessage(version, bootId, url);
                case "request":
                    Fields(root, "type", "request_id", "check_code", "device_label");
                    var code = RequiredString(root, "check_code");
                    var label = RequiredString(root, "device_label");
                    if (code.Length != 2 || code.Any(c => c is < '0' or > '9')
                        || label.EnumerateRunes().Count() > 80 || label.Any(char.IsControl)) throw InvalidFrame();
                    return new RequestMessage(RequestId(root), code, label);
                case "confirm_empty":
                    Fields(root, "type", "request_id", "target_epoch");
                    return new ConfirmEmptyMessage(RequestId(root), Epoch(root));
                case "press_enter":
                    Fields(root, "type", "request_id", "target_epoch", "enter_id", "after_seq");
                    var enterId = RequiredString(root, "enter_id");
                    var afterSeq = RequiredLong(root, "after_seq");
                    if (!IsEnterId(enterId) || afterSeq is < 0 or > 10000) throw InvalidFrame();
                    return new PressEnterMessage(RequestId(root), Epoch(root), enterId, afterSeq);
                case "apply":
                    Fields(root, "type", "request_id", "target_epoch", "seq", "prev_seq", "text", "selection", "composition");
                    var sequence = RequiredLong(root, "seq");
                    var previous = RequiredLong(root, "prev_seq");
                    var text = RequiredString(root, "text");
                    var selection = root.GetProperty("selection");
                    Fields(selection, "start", "end", "direction");
                    var start = RequiredInt(selection, "start");
                    var end = RequiredInt(selection, "end");
                    var direction = RequiredString(selection, "direction");
                    var composition = RequiredString(root, "composition");
                    if (sequence is < 1 or > 10_000 || previous != sequence - 1
                        || text.Length > 4096 || StrictUtf8.GetByteCount(text) > 16_384
                        || start < 0 || end < start || end > text.Length
                        || !IsBoundary(text, start) || !IsBoundary(text, end)
                        || direction is not ("none" or "forward" or "backward") || composition != "committed")
                        throw InvalidFrame();
                    return new ApplyMessage(RequestId(root), Epoch(root), sequence, previous,
                        text, new TextSelection(start, end, direction), composition);
                case "revoke":
                    Fields(root, "type", "request_id", "reason");
                    var request = root.GetProperty("request_id");
                    var id = request.ValueKind == JsonValueKind.Null ? null : RequiredString(root, "request_id");
                    var reason = RequiredString(root, "reason");
                    if ((id is not null && !IsId(id)) || !NativeReasons.IsValid(reason)) throw InvalidFrame();
                    return new RevokeMessage(id, reason);
                default: throw InvalidFrame();
            }
        }
        catch (Exception error) when (error is JsonException or DecoderFallbackException
            or InvalidOperationException or KeyNotFoundException or FormatException or OverflowException)
        {
            // Exceptions never include peer data, including the private desktop URL.
            throw InvalidFrame();
        }
    }

    public static bool ValidDesktopUrl(string value)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) || uri.Scheme != "http"
            || uri.Host != "127.0.0.1" || uri.AbsolutePath != "/desktop" || uri.UserInfo.Length != 0
            || uri.Fragment.Length != 0) return false;
        return uri.Query.Length == 69 && uri.Query.StartsWith("?key=", StringComparison.Ordinal)
            && uri.Query.AsSpan(5).IndexOfAnyExcept("0123456789abcdefABCDEF") < 0;
    }

    private static bool ValidResult(ResultCommand result) => IsId(result.RequestId) && IsEpoch(result.TargetEpoch)
        && result.Seq is >= 1 and <= 10_000
        && result.Status is "applied" or "cancelled" or "rejected" or "partial" or "unknown"
        && (result.Reason is null || NativeReasons.IsValid(result.Reason))
        && (result.Proof is null or "verified_readback")
        && (result.Status != "applied" || result.Proof == "verified_readback");

    private static string RequestId(JsonElement value)
    {
        var id = RequiredString(value, "request_id");
        return IsId(id) ? id : throw InvalidFrame();
    }

    private static string Epoch(JsonElement value)
    {
        var epoch = RequiredString(value, "target_epoch");
        return IsEpoch(epoch) ? epoch : throw InvalidFrame();
    }

    internal static bool IsId(string value) => value.Length == 32
        && value.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f');
    internal static bool IsEnterId(string value) => value.Length == 32
        && value.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f' or >= 'A' and <= 'F');
    internal static bool IsEpoch(string value) => value.Length is >= 1 and <= 128
        && value.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'z' or >= 'A' and <= 'Z' or '-' or '_');
    private static bool IsBoundary(string value, int position) => position == 0 || position == value.Length
        || !char.IsHighSurrogate(value[position - 1]) || !char.IsLowSurrogate(value[position]);

    private static string RequiredString(JsonElement root, string name)
    {
        var value = root.GetProperty(name);
        if (value.ValueKind != JsonValueKind.String) throw InvalidFrame();
        var text = value.GetString()!;
        for (var i = 0; i < text.Length; i++)
        {
            if (char.IsHighSurrogate(text[i]))
            {
                if (++i >= text.Length || !char.IsLowSurrogate(text[i])) throw InvalidFrame();
            }
            else if (char.IsLowSurrogate(text[i])) throw InvalidFrame();
        }
        return text;
    }

    private static int RequiredInt(JsonElement root, string name) => root.GetProperty(name).GetInt32();
    private static long RequiredLong(JsonElement root, string name) => root.GetProperty(name).GetInt64();

    private static void Fields(JsonElement root, params string[] expected)
    {
        if (root.ValueKind != JsonValueKind.Object) throw InvalidFrame();
        var remaining = new HashSet<string>(expected, StringComparer.Ordinal);
        foreach (var property in root.EnumerateObject())
            if (!remaining.Remove(property.Name)) throw InvalidFrame();
        if (remaining.Count != 0) throw InvalidFrame();
    }

    internal static InvalidDataException InvalidFrame() => new("Invalid native frame.");
    internal static bool IsAppLabel(string value) => value.EnumerateRunes().Count() <= 32 && value.EnumerateRunes().All(r =>
        r.Value is >= 'a' and <= 'z' or >= 'A' and <= 'Z' or >= '0' and <= '9' or ' ' or '.' or '-' or '_'
        or >= 0x3400 and <= 0x4dbf or >= 0x4e00 and <= 0x9fff or >= 0xf900 and <= 0xfaff
        or >= 0x3041 and <= 0x3096 or >= 0x30a1 and <= 0x30fa or >= 0x31f0 and <= 0x31ff
        or >= 0x1100 and <= 0x11ff or >= 0x3131 and <= 0x318e or >= 0xac00 and <= 0xd7a3
        or >= 0x20000 and <= 0x2fa1f or >= 0x30000 and <= 0x323af);
}

public sealed class FrameDecoder
{
    private readonly byte[] _header = new byte[4];
    private byte[]? _body;
    private int _headerLength, _bodyLength;
    private bool _failed;

    public IReadOnlyList<NativeMessage> Feed(ReadOnlySpan<byte> bytes)
    {
        if (_failed) throw FrameCodec.InvalidFrame();
        var messages = new List<NativeMessage>();
        try
        {
            while (!bytes.IsEmpty)
            {
                if (_body is null)
                {
                    var take = Math.Min(4 - _headerLength, bytes.Length);
                    bytes[..take].CopyTo(_header.AsSpan(_headerLength));
                    _headerLength += take;
                    bytes = bytes[take..];
                    if (_headerLength != 4) break;
                    var length = BinaryPrimitives.ReadUInt32LittleEndian(_header);
                    if (length is < 1 or > FrameCodec.MaximumPayloadBytes) throw FrameCodec.InvalidFrame();
                    _body = new byte[(int)length];
                }
                var count = Math.Min(_body.Length - _bodyLength, bytes.Length);
                bytes[..count].CopyTo(_body.AsSpan(_bodyLength));
                _bodyLength += count;
                bytes = bytes[count..];
                if (_bodyLength != _body.Length) continue;
                messages.Add(FrameCodec.Decode(_body));
                Array.Clear(_body);
                _body = null;
                _bodyLength = _headerLength = 0;
            }
            return messages;
        }
        catch
        {
            Invalidate();
            throw;
        }
    }

    // Every EOF terminates the private channel, including an EOF exactly between frames.
    public void Complete()
    {
        Invalidate();
        throw new EndOfStreamException("Native channel disconnected.");
    }

    private void Invalidate()
    {
        _failed = true;
        if (_body is not null) Array.Clear(_body);
        _body = null;
        _bodyLength = _headerLength = 0;
    }
}
