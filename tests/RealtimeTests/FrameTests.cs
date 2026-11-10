using System.Buffers.Binary;
using System.Text;
using FastInput.Windows.Realtime;

namespace FastInput.RealtimeTests;

internal static class FrameTests
{
    private const string Hello = "{\"type\":\"hello\",\"protocol_version\":1,\"server_boot_id\":\"0123456789abcdef0123456789abcdef\",\"desktop_url\":\"http://127.0.0.1:8765/desktop?key=aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa\"}";
    private const string Request = "{\"type\":\"request\",\"request_id\":\"0123456789abcdef0123456789abcdef\",\"check_code\":\"07\",\"device_label\":\"測試手機\"}";

    public static void Register(TestSuite suite)
    {
        suite.Add("frame.byte_by_byte", () =>
        {
            var decoder = new FrameDecoder();
            var messages = new List<NativeMessage>();
            foreach (var value in Raw(Hello)) messages.AddRange(decoder.Feed([value]));
            Check.Equal(1, messages.Count);
            Check.True(messages[0] is HelloMessage);
        });
        suite.Add("frame.every_split_boundary", () =>
        {
            var raw = Raw(Hello);
            for (var split = 1; split < raw.Length; split++)
            {
                var decoder = new FrameDecoder();
                var first = decoder.Feed(raw.AsSpan(0, split));
                var second = decoder.Feed(raw.AsSpan(split));
                Check.Equal(1, first.Count + second.Count);
            }
        });
        suite.Add("frame.concatenated_and_utf8", () =>
        {
            var messages = new FrameDecoder().Feed(Raw(Hello).Concat(Raw(Request)).Concat(Raw(Request)).ToArray());
            Check.Equal(3, messages.Count);
            Check.Equal("測試手機", ((RequestMessage)messages[1]).DeviceLabel);
        });
        suite.Add("frame.exact_65536_payload", () =>
        {
            var payload = Encoding.UTF8.GetBytes(Hello.PadRight(FrameCodec.MaximumPayloadBytes));
            Check.Equal(65_536, payload.Length);
            Check.Equal(1, new FrameDecoder().Feed(Raw(payload)).Count);
        });
        suite.Add("frame.outbound_schema", () =>
        {
            NativeCommand[] commands = [new GrantCommand("0123456789abcdef0123456789abcdef"),
                new TargetBoundCommand("0123456789abcdef0123456789abcdef", "epoch_1", "binding"),
                new TargetBoundCommand("0123456789abcdef0123456789abcdef", "epoch_1", "active"),
                new ResultCommand("0123456789abcdef0123456789abcdef", "epoch_1", 1, "applied", null, "verified_readback"),
                new RevokeCommand(null, "emergency_stop"), new HeartbeatCommand()];
            foreach (var command in commands)
            {
                var frame = FrameCodec.Encode(command);
                Check.Equal(frame.Length - 4, (int)BinaryPrimitives.ReadUInt32LittleEndian(frame));
                using var json = System.Text.Json.JsonDocument.Parse(frame.AsMemory(4));
                Check.True(json.RootElement.GetProperty("type").GetString() is not null);
            }
        });
        var invalid = new Dictionary<string, byte[]>
        {
            ["zero_length"] = [0, 0, 0, 0],
            ["oversize"] = [1, 0, 1, 0],
            ["invalid_utf8"] = Raw([0xC3, 0x28]),
            ["non_json"] = Raw("no JSON"),
            ["bom"] = Raw(Encoding.UTF8.GetPreamble().Concat(Encoding.UTF8.GetBytes(Hello)).ToArray()),
            ["unknown_type"] = Raw("{\"type\":\"remote_key\"}"),
            ["unknown_field"] = Raw(Request[..^1] + ",\"hwnd\":123}"),
            ["duplicate_field"] = Raw(Request[..^1] + ",\"check_code\":\"08\"}"),
            ["wrong_type"] = Raw(Request.Replace("\"07\"", "7")),
            ["missing_field"] = Raw("{\"type\":\"request\"}"),
            ["unknown_nested"] = Raw("{\"type\":\"apply\",\"request_id\":\"0123456789abcdef0123456789abcdef\",\"target_epoch\":\"e\",\"seq\":1,\"prev_seq\":0,\"text\":\"x\",\"selection\":{\"start\":1,\"end\":1,\"direction\":\"none\",\"key\":1},\"composition\":\"committed\"}"),
            ["unpaired_surrogate"] = Raw(Request.Replace("測試手機", "\\uD800")),
            ["unknown_reason"] = Raw("{\"type\":\"revoke\",\"request_id\":null,\"reason\":\"arbitrary_private_text\"}")
        };
        foreach (var (name, raw) in invalid)
        {
            suite.Add("frame.reject_" + name, () =>
            {
                var decoder = new FrameDecoder();
                Check.Throws(() => decoder.Feed(raw));
                Check.Throws(() => decoder.Feed(Raw(Hello)), "failed channel resumed trust");
            });
        }
        suite.Add("frame.eof_full_or_partial_is_fatal", () =>
        {
            for (var count = 0; count <= 8; count++)
            {
                var decoder = new FrameDecoder();
                decoder.Feed(Raw(Hello).AsSpan(0, count));
                Check.Throws(decoder.Complete);
                Check.Throws(() => decoder.Feed(Raw(Hello)));
            }
        });
    }

    private static byte[] Raw(string value) => Raw(Encoding.UTF8.GetBytes(value));
    private static byte[] Raw(byte[] payload)
    {
        var frame = new byte[payload.Length + 4];
        BinaryPrimitives.WriteUInt32LittleEndian(frame, (uint)payload.Length);
        payload.CopyTo(frame, 4);
        return frame;
    }
}

