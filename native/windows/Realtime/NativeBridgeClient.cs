namespace FastInput.Windows.Realtime;

/// <summary>Private streams of the one owned backend. No UI thread performs pipe I/O.</summary>
public sealed class NativeBridgeClient : INativeLink, IDisposable
{
    private const int QueueCapacity = 64;
    private readonly Stream _stdout;
    private readonly Stream _stdin;
    private readonly bool _leaveOpen;
    private readonly Queue<byte[]> _pending = new();
    private readonly object _gate = new();
    private readonly SemaphoreSlim _ready = new(0);
    private readonly CancellationTokenSource _lifetime = new();
    private readonly TaskCompletionSource<HelloMessage> _hello = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private Task _completion = Task.CompletedTask;
    private bool _started, _failed, _disposed;

    public NativeBridgeClient(Stream stdout, Stream stdin, bool leaveOpen = false)
    {
        _stdout = stdout;
        _stdin = stdin;
        _leaveOpen = leaveOpen;
    }

    public Task<HelloMessage> Hello => _hello.Task;
    public Task Completion => _completion;
    public event Action<NativeMessage>? MessageReceived;
    // Raised synchronously when trust is lost, before disposal or network notification.
    public event Action? ChannelFailed;
    public bool IsHealthy { get { lock (_gate) return _started && !_failed && !_disposed; } }

    public void Start()
    {
        lock (_gate)
        {
            if (_started || _disposed) throw new InvalidOperationException("Native client already started or disposed.");
            _started = true;
            _completion = Task.WhenAll(Task.Run(ReadLoopAsync), Task.Run(WriteLoopAsync), Task.Run(HeartbeatLoopAsync));
        }
    }

    public bool TrySend(NativeCommand command)
    {
        byte[] frame;
        try { frame = FrameCodec.Encode(command); }
        catch (InvalidDataException) { Fail(); return false; }
        lock (_gate)
        {
            if (!_started || _failed || _disposed) return false;
            if (_pending.Count < QueueCapacity)
            {
                _pending.Enqueue(frame);
                _ready.Release();
                return true;
            }
        }
        Fail();
        return false;
    }

    public void ClearPending()
    {
        lock (_gate)
        {
            while (_pending.TryDequeue(out var frame)) Array.Clear(frame);
        }
    }

    private async Task ReadLoopAsync()
    {
        var decoder = new FrameDecoder();
        var buffer = new byte[8192];
        var receivedHello = false;
        try
        {
            while (!_lifetime.IsCancellationRequested)
            {
                var count = await _stdout.ReadAsync(buffer, _lifetime.Token).ConfigureAwait(false);
                if (count == 0) decoder.Complete();
                foreach (var message in decoder.Feed(buffer.AsSpan(0, count)))
                {
                    if (!receivedHello)
                    {
                        if (message is not HelloMessage hello) throw FrameCodec.InvalidFrame();
                        receivedHello = true;
                        _hello.TrySetResult(hello);
                    }
                    else if (message is HelloMessage) throw FrameCodec.InvalidFrame();
                    if (!IsHealthy) return;
                    MessageReceived?.Invoke(message);
                }
            }
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { }
        catch (Exception) { Fail(); }
        finally { Array.Clear(buffer); }
    }

    private async Task WriteLoopAsync()
    {
        try
        {
            while (!_lifetime.IsCancellationRequested)
            {
                await _ready.WaitAsync(_lifetime.Token).ConfigureAwait(false);
                byte[]? frame = null;
                lock (_gate)
                {
                    if (_failed || _disposed) return;
                    if (_pending.TryDequeue(out var next)) frame = next;
                }
                if (frame is null) continue;
                try
                {
                    await _stdin.WriteAsync(frame, _lifetime.Token).ConfigureAwait(false);
                    await _stdin.FlushAsync(_lifetime.Token).ConfigureAwait(false);
                }
                finally { Array.Clear(frame); }
            }
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { }
        catch (Exception) { Fail(); }
    }

    private async Task HeartbeatLoopAsync()
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1));
        try
        {
            while (await timer.WaitForNextTickAsync(_lifetime.Token).ConfigureAwait(false))
                if (!TrySend(new HeartbeatCommand())) return;
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { }
        catch (Exception) { Fail(); }
    }

    private void Fail()
    {
        lock (_gate)
        {
            if (_failed || _disposed) return;
            _failed = true;
            while (_pending.TryDequeue(out var frame)) Array.Clear(frame);
        }
        _hello.TrySetException(new IOException("Native channel unavailable."));
        try { ChannelFailed?.Invoke(); }
        catch (Exception) { /* The channel is already irrevocably closed. */ }
        finally
        {
            // Revoke local authority before cancellation callbacks or stream cleanup.
            _lifetime.Cancel();
            // Closing the owned stdin also tells Rust to revoke immediately.
            if (!_leaveOpen)
            {
                try { _stdin.Dispose(); } catch (IOException) { }
                try { _stdout.Dispose(); } catch (IOException) { }
            }
        }
    }

    public void Dispose()
    {
        Fail();
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
        }
        // Keep synchronization objects valid while canceled I/O is unwinding.
        _ = _completion.ContinueWith(_ =>
        {
            _ready.Dispose();
            _lifetime.Dispose();
        }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
    }
}
