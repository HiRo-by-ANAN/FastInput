using System.Diagnostics;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Security.Principal;
using Microsoft.Win32.SafeHandles;

namespace FastInput.Windows;

internal sealed class SingleInstance : IDisposable
{
    private static readonly string Identity = $"FastInput-{WindowsIdentity.GetCurrent().User!.Value}-{Process.GetCurrentProcess().SessionId}";
    private readonly Mutex _mutex = new(false, @"Local\" + Identity);
    private readonly CancellationTokenSource _stop = new();
    private Task? _listener;
    private bool _ownsMutex;
    public SingleInstance()
    {
        try { _ownsMutex = _mutex.WaitOne(0); }
        catch (AbandonedMutexException) { _ownsMutex = true; }
    }
    public bool IsPrimary => _ownsMutex;

    public bool ActivateExisting()
    {
        try
        {
            using var pipe = new NamedPipeClientStream(".", Identity, PipeDirection.Out, PipeOptions.CurrentUserOnly);
            pipe.Connect(5000);
            if (GetNamedPipeServerProcessId(pipe.SafePipeHandle, out var processId))
                _ = AllowSetForegroundWindow(processId);
            pipe.WriteByte(1);
            pipe.Flush();
            return true;
        }
        catch (Exception error) when (error is IOException or TimeoutException or UnauthorizedAccessException) { return false; }
    }

    public void Listen(MainForm form)
    {
        _ = form.Handle;
        _listener = Task.Run(async () =>
        {
            while (!_stop.IsCancellationRequested)
            {
                try
                {
                    using var pipe = new NamedPipeServerStream(Identity, PipeDirection.In, 1,
                        PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                    await pipe.WaitForConnectionAsync(_stop.Token);
                    var buffer = new byte[1];
                    using var timeout = CancellationTokenSource.CreateLinkedTokenSource(_stop.Token);
                    timeout.CancelAfter(TimeSpan.FromSeconds(2));
                    if (await pipe.ReadAsync(buffer, timeout.Token) == 1 && buffer[0] == 1)
                        form.BeginInvoke(new Action(form.BringToForeground));
                }
                catch (OperationCanceledException) { }
                catch (Exception error) when (error is IOException or InvalidOperationException or UnauthorizedAccessException)
                {
                    try { await Task.Delay(250, _stop.Token); }
                    catch (OperationCanceledException) { }
                }
            }
        });
    }

    public void Dispose()
    {
        _stop.Cancel();
        _listener?.GetAwaiter().GetResult();
        _stop.Dispose();
        if (_ownsMutex) { _mutex.ReleaseMutex(); _ownsMutex = false; }
        _mutex.Dispose();
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetNamedPipeServerProcessId(SafePipeHandle pipe, out uint serverProcessId);
    [DllImport("user32.dll")] private static extern bool AllowSetForegroundWindow(uint processId);
}
