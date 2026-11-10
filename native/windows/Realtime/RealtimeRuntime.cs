namespace FastInput.Windows.Realtime;

internal sealed class RealtimeRuntime : IDisposable
{
    private readonly NativeBridgeClient _bridge;
    private readonly RealtimeSettings _settings;
    private readonly RealtimeUi _ui;
    private readonly RealtimeController _controller;
    private readonly EmergencyHotKey _hotkey = new(new WindowsHotKeyApi());
    private readonly System.Threading.Timer _timer;
    private readonly Form _owner;
    private readonly RealTargetPicker _picker = new();
    private readonly LocalPhysicalInputMonitor _physical = new();
    private int _ticking;
    private bool _disposed;
    public bool Enabled => _settings.RealtimeEnabled;
    public bool VirtualEnterEnabled => _settings.VirtualEnterEnabled;
    private string? _unavailableReason;
    public string? UnavailableReason => _unavailableReason == "hotkey" ? _hotkey.FailureReason : Loc.Localize(_unavailableReason);

    public RealtimeRuntime(Form owner, NativeBridgeClient bridge, RealtimeSettings settings)
    {
        _owner = owner;
        _bridge = bridge;
        _settings = settings;
        _ui = new RealtimeUi(owner);
        _picker.TargetPicked += BeginMonitor;
        _picker.PickingStarted += _physical.Stop;
        _picker.PickRejected += ShowPickerReason;
        _controller = new RealtimeController(bridge, new RealtimeClock(), _ui, _picker,
            new TargetAdapterRouter(), new WindowsSafetyProbe(t => _physical.HasPendingInput ? new(PhysicalInputPending: true)
                : WindowsSafetyProbe.ReadWindowFacts(t)), _settings, new AuditLog());
        _physical.PhysicalInput += PhysicalInput;
        _controller.SetHotkeyAvailable(false);
        _ui.PauseRequested += () => _controller.Pause("released");
        _ui.StopRequested += EmergencyStop;
        _bridge.MessageReceived += MessageReceived;
        _bridge.ChannelFailed += Disconnected;
        if (_settings.RealtimeEnabled)
        {
            if (!PrepareEnable())
            {
                _controller.SetEnabled(false);
                owner.BeginInvoke(new Action(() => MessageBox.Show(owner, UnavailableReason, Loc.T("realtimeruntime.real.time.typing.is.disabled"),
                    MessageBoxButtons.OK, MessageBoxIcon.Warning)));
            }
        }
        _timer = new System.Threading.Timer(_ => Tick(), null, 25, 25);
    }

    private void ShowPickerReason(string reason) => _ui.ShowLocalReason(reason);
    private void BeginMonitor(TargetCandidate target)
    {
        try { _physical.Start(); }
        catch { _controller.Pause("unsupported"); ShowPickerReason(Loc.T("realtimeruntime.cannot.install.local.input.monitoring.hooks.permission.was.revoked")); }
    }
    private void PhysicalInput()
    {
        if (_controller.IsMonitoringTarget) _controller.NotifyPhysicalInput();
    }
    private void Tick()
    {
        if (_disposed || Interlocked.Exchange(ref _ticking, 1) != 0) return;
        try
        {
            if (_picker.Failed || _physical.Failed) _controller.Pause("physical_input");
            _controller.Tick();
            if (!_controller.IsMonitoringTarget) _physical.Stop();
        }
        catch { _controller.Pause("unsupported"); }
        finally { Volatile.Write(ref _ticking, 0); }
    }

    private void MessageReceived(NativeMessage message) => _ = ProcessAsync(message);
    private async Task ProcessAsync(NativeMessage message)
    {
        try { await _controller.HandleAsync(message); }
        catch (Exception) { _controller.Disconnected(); }
    }
    private void Disconnected() => _controller.Disconnected();

    private bool PrepareEnable()
    {
        bool elevated;
        try { elevated = WindowsSafetyProbe.ReadHostElevated(); }
        catch (Exception)
        {
            _unavailableReason = Loc.T("realtimeruntime.cannot.verify.fastinput.permissions.real.time.typing.cannot.be");
            return false;
        }
        if (elevated)
        {
            _unavailableReason = Loc.T("realtimeruntime.fastinput.is.running.as.administrator.real.time.typing.cannot");
            return false;
        }
        if (!_hotkey.Register(_owner.Handle))
        {
            _unavailableReason = "hotkey";
            return false;
        }
        _controller.SetHotkeyAvailable(true);
        _unavailableReason = null;
        return true;
    }

    public bool SetEnabled(bool enabled)
    {
        if (enabled && !PrepareEnable()) return false;
        var previous = _settings.RealtimeEnabled;
        _settings.RealtimeEnabled = enabled;
        try { _settings.Save(); }
        catch
        {
            _settings.RealtimeEnabled = previous;
            if (!previous) { _controller.SetHotkeyAvailable(false); _hotkey.Dispose(); }
            throw;
        }
        _controller.SetEnabled(enabled);
        if (!enabled) { _controller.SetHotkeyAvailable(false); _hotkey.Dispose(); }
        return true;
    }

    public void EmergencyStop() => _controller.EmergencyStop();
    // Called only by the native main-window menu after its confirmation dialog.
    public void SetVirtualEnterEnabled(bool enabled)
    {
        if (!enabled)
        {
            _controller.SetVirtualEnterEnabled(false);
            _settings.Save(); // A persistence failure must never restore live Enter permission.
            return;
        }
        var previous = _settings.VirtualEnterEnabled;
        _settings.VirtualEnterEnabled = true;
        try { _settings.Save(); }
        catch { _settings.VirtualEnterEnabled = previous; throw; }
        _controller.SetVirtualEnterEnabled(true);
    }
    public void NotifyLocked() => _controller.NotifyLocked();
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _controller.EmergencyStop();
        _timer.Dispose();
        _bridge.MessageReceived -= MessageReceived;
        _bridge.ChannelFailed -= Disconnected;
        _controller.Dispose();
        _picker.Dispose();
        _physical.Dispose();
        _hotkey.Dispose();
        _ui.Dispose();
    }
}
