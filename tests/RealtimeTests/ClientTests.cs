using System.Diagnostics;
using System.Text;
using FastInput.Windows.Realtime;

namespace FastInput.RealtimeTests;

internal static class ClientTests
{
    private const string Id = "0123456789abcdef0123456789abcdef";
    private static readonly byte[] Hello = FrameCodec.EncodeJson("{\"type\":\"hello\",\"protocol_version\":1,\"server_boot_id\":\"" + Id
        + "\",\"desktop_url\":\"http://127.0.0.1:8765/desktop?key=" + new string('a', 64) + "\"}");

    public static void Register(TestSuite suite)
    {
        suite.Add("client.request_before_hello_invalidates", () => RejectAsync(FrameCodec.EncodeJson(
            "{\"type\":\"request\",\"request_id\":\"" + Id + "\",\"check_code\":\"07\",\"device_label\":\"phone\"}")));
        suite.Add("client.duplicate_hello_invalidates", () => RejectAsync(Hello.Concat(Hello).ToArray()));
        suite.Add("client.malformed_channel_never_resumes", () => RejectAsync(
            FrameCodec.EncodeJson("{\"type\":\"private-marker-should-never-be-logged\"}").Concat(Hello).ToArray()));
        suite.Add("client.applied_requires_verified_readback", () => Check.Throws(() => FrameCodec.Encode(
            new ResultCommand(Id, "epoch", 1, "applied"))));
        suite.Add("frame.maximum_payload_boundary", () =>
        {
            var request = "{\"type\":\"request\",\"request_id\":\"" + Id
                + "\",\"check_code\":\"07\",\"device_label\":\"phone\"}";
            var padded = request + new string(' ', FrameCodec.MaximumPayloadBytes - Encoding.UTF8.GetByteCount(request));
            var frame = FrameCodec.EncodeJson(padded);
            Check.Equal(65_540, frame.Length);
            Check.Equal(1, new FrameDecoder().Feed(frame).Count);
            Check.Throws(() => FrameCodec.EncodeJson(padded + " "));
        });
        suite.Add("client.one_second_heartbeat", async () =>
        {
            using var stdout = new OpenInput(Hello);
            using var stdin = new RecordingOutput();
            using var client = new NativeBridgeClient(stdout, stdin, leaveOpen: true);
            client.Start();
            await client.Hello;
            await Check.EventuallyAsync(() => stdin.Writes >= 1, "heartbeat was not sent", 2200);
            var expected = FrameCodec.Encode(new HeartbeatCommand());
            Check.True(stdin.Snapshot().AsSpan(0, expected.Length).SequenceEqual(expected), "heartbeat framing differs");
            Check.True(client.IsHealthy);
        });
        suite.Add("client.concurrent_senders_have_one_serial_writer", async () =>
        {
            using var stdout = new OpenInput(Hello);
            using var stdin = new RecordingOutput();
            using var client = new NativeBridgeClient(stdout, stdin, leaveOpen: true);
            client.Start();
            await client.Hello;
            var senders = Enumerable.Range(0, 32).Select(_ => Task.Run(() => client.TrySend(new GrantCommand(Id))));
            Check.True((await Task.WhenAll(senders)).All(sent => sent));
            await Check.EventuallyAsync(() => stdin.GrantWrites == 32, "writer did not drain queued grants");
            client.Dispose();
            await client.Completion;
            var frames = stdin.Snapshot();
            var position = 0;
            var grants = 0;
            while (position < frames.Length)
            {
                Check.True(frames.Length - position >= 4, "partial write prefix");
                var length = System.Buffers.Binary.BinaryPrimitives.ReadInt32LittleEndian(frames.AsSpan(position));
                Check.True(length > 0 && length <= frames.Length - position - 4, "interleaved write length");
                using var json = System.Text.Json.JsonDocument.Parse(frames.AsMemory(position + 4, length));
                var type = json.RootElement.GetProperty("type").GetString();
                Check.True(type is "grant" or "heartbeat");
                if (type == "grant") grants++;
                position += length + 4;
            }
            Check.Equal(32, grants);
            Check.Equal(1, stdin.MaximumConcurrentWrites);
        });
        suite.Add("client.bounded_writer_returns_without_waiting_for_pipe", async () =>
        {
            using var stdout = new OpenInput(Hello);
            using var stdin = new BlockedOutput();
            using var client = new NativeBridgeClient(stdout, stdin, leaveOpen: true);
            client.Start();
            await client.Hello;
            Check.True(client.TrySend(new HeartbeatCommand()));
            await stdin.Entered.Task.WaitAsync(TimeSpan.FromSeconds(2));
            var elapsed = Stopwatch.StartNew();
            for (var index = 0; index < 64; index++) Check.True(client.TrySend(new HeartbeatCommand()));
            Check.True(!client.TrySend(new HeartbeatCommand()), "full bounded queue accepted another frame");
            Check.True(elapsed.ElapsedMilliseconds < 500, "enqueue waited for blocked pipe");
            Check.True(!client.IsHealthy);
            await client.Completion.WaitAsync(TimeSpan.FromSeconds(2));
        });
    }

    private static async Task RejectAsync(byte[] frames)
    {
        using var input = new MemoryStream(frames);
        using var output = new MemoryStream();
        using var client = new NativeBridgeClient(input, output, leaveOpen: true);
        var failed = 0;
        client.ChannelFailed += () => Interlocked.Increment(ref failed);
        client.Start();
        await client.Completion.WaitAsync(TimeSpan.FromSeconds(2));
        try { await client.Hello; }
        catch (IOException error) { Check.True(!error.Message.Contains("private-marker", StringComparison.Ordinal)); }
        Check.Equal(1, failed);
        Check.True(!client.IsHealthy && !client.TrySend(new HeartbeatCommand()));
    }

    private sealed class OpenInput(byte[] initial) : MemoryStream(initial)
    {
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (Position < Length) return await base.ReadAsync(buffer, cancellationToken);
            await Task.Delay(Timeout.Infinite, cancellationToken);
            return 0;
        }
    }

    private sealed class BlockedOutput : MemoryStream
    {
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            Entered.TrySetResult();
            await Task.Delay(Timeout.Infinite, cancellationToken);
        }
    }

    private sealed class RecordingOutput : MemoryStream
    {
        private readonly object _gate = new();
        private int _active, _writes, _maximum, _grants;
        public int Writes => Volatile.Read(ref _writes);
        public int GrantWrites => Volatile.Read(ref _grants);
        public int MaximumConcurrentWrites => Volatile.Read(ref _maximum);
        public byte[] Snapshot() { lock (_gate) return ToArray(); }
        public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            var concurrent = Interlocked.Increment(ref _active);
            lock (_gate) _maximum = Math.Max(_maximum, concurrent);
            try
            {
                await Task.Delay(1, cancellationToken);
                lock (_gate) Write(buffer.Span);
                using var json = System.Text.Json.JsonDocument.Parse(buffer[4..]);
                if (json.RootElement.GetProperty("type").GetString() == "grant") Interlocked.Increment(ref _grants);
                Interlocked.Increment(ref _writes);
            }
            finally { Interlocked.Decrement(ref _active); }
        }
    }
}
