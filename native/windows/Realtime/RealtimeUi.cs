using Timer = System.Windows.Forms.Timer;

namespace FastInput.Windows.Realtime;

internal sealed class NoFocusButton : Button
{
    public NoFocusButton() { SetStyle(ControlStyles.Selectable, false); TabStop = false; }
    internal bool IsSelectableForTest => GetStyle(ControlStyles.Selectable);
}

internal sealed class RealtimeOverlay : Form
{
    private readonly Label _status = new() { AutoSize = false, Bounds = new Rectangle(10, 8, 460, 118) };
    internal readonly NoFocusButton PauseButton = new() { Text = Loc.T("realtimeui.pause"), Bounds = new Rectangle(10, 139, 100, 30) };
    internal readonly NoFocusButton StopButton = new() { Text = Loc.T("realtimeui.stop"), Bounds = new Rectangle(120, 139, 100, 30) };
    public event Action? PauseRequested;
    public event Action? StopRequested;

    public RealtimeOverlay()
    {
        Text = Loc.T("realtimeui.fastinput.real.time.typing");
        FormBorderStyle = FormBorderStyle.FixedToolWindow;
        StartPosition = FormStartPosition.Manual;
        ShowInTaskbar = false;
        TopMost = true;
        AutoScaleMode = AutoScaleMode.Dpi;
        ClientSize = new Size(480, 180);
        Controls.AddRange([_status, PauseButton, StopButton]);
        PauseButton.Click += (_, _) => PauseRequested?.Invoke();
        StopButton.Click += (_, _) => StopRequested?.Invoke();
    }

    internal int ExtendedStyleForTest => CreateParams.ExStyle;
    protected override bool ShowWithoutActivation => true;
    protected override CreateParams CreateParams
    {
        get { var parameters = base.CreateParams; parameters.ExStyle |= OverlayLayout.ExtendedStyles; return parameters; }
    }
    protected override void WndProc(ref Message message)
    {
        if (message.Msg == OverlayLayout.MouseActivateMessage)
        {
            message.Result = OverlayLayout.NoActivateResult;
            return;
        }
        base.WndProc(ref message);
    }

    private RealtimeView? _lastView;
    internal void ApplyLanguage()
    {
        Text = Loc.T("realtimeui.fastinput.real.time.typing");
        PauseButton.Text = Loc.T("realtimeui.pause");
        StopButton.Text = Loc.T("realtimeui.stop");
        var view = _lastView;
        if (view is null) return;
        var waiting = view.State == RealtimeState.ARMED;
        _status.Text = Loc.T("realtimeui.phone.is.typing", view.ProgramName)
            + (waiting ? Loc.T("realtimeui.click.the.field.you.want.to.type.in") : Loc.T("state." + view.State) + "　" + Loc.Reason(view.Reason))
            + Loc.T("realtimeui.seconds.left.emergency.stop", Math.Max(0, view.RemainingMilliseconds) / 1000, EmergencyHotKey.CurrentLabel);
    }

    public void UpdateState(RealtimeView view, LocalRect fallbackWorkArea)
    {
        _lastView = view;
        ApplyLanguage();
        var target = view.Target?.Bounds ?? new LocalRect(fallbackWorkArea.X + 20, fallbackWorkArea.Y + 20, 0, 0);
        var work = view.Target?.WorkingArea ?? fallbackWorkArea;
        var location = OverlayLayout.PlaceOutside(target, work, Width, Height);
        // A full-work-area field leaves no legal outside position. Keep the emergency hotkey, never cover the field.
        if (location is null) { Hide(); return; }
        Bounds = new Rectangle(location.X, location.Y, location.Width, location.Height);
        if (!Visible) Show();
    }
}

internal sealed class RealtimeUi : IApprovalUi, ILocalTargetFeedback, IDisposable
{
    private readonly Form owner;
    private Action? _refreshApproval;
    public RealtimeUi(Form owner)
    {
        this.owner = owner;
        Loc.LanguageChanged += LanguageChanged;
    }
    private void LanguageChanged() => Post(() => { _overlay?.ApplyLanguage(); _refreshApproval?.Invoke(); });
    private readonly SynchronizationContext _context = SynchronizationContext.Current
        ?? throw new InvalidOperationException(Loc.T("realtimeui.real.time.typing.ui.must.be.created.on.the"));
    private RealtimeOverlay? _overlay;
    private Form? _approval;
    private bool _disposed;
    public event Action? PauseRequested;
    public event Action? StopRequested;

    private void Post(Action action) => _context.Post(_ => { if (!_disposed && !owner.IsDisposed) action(); }, null);

    public Task<bool> RequestApprovalAsync(RequestMessage request, CancellationToken cancellationToken)
    {
        var answer = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var cancellation = cancellationToken.Register(() => { answer.TrySetResult(false); CloseApproval(); });
        Post(() =>
        {
            if (answer.Task.IsCompleted) { cancellation.Dispose(); return; }
            var form = new Form
            {
                Text = Loc.T("realtimeui.approve.phone.real.time.typing"), ClientSize = new Size(540, 235),
                StartPosition = FormStartPosition.CenterParent, FormBorderStyle = FormBorderStyle.FixedDialog,
                MaximizeBox = false, MinimizeBox = false, ShowInTaskbar = false
            };
            var label = new Label { Text = Loc.T("realtimeui.phone.verification.code.after.approval.claim.permission.on.your", request.DeviceLabel, request.CheckCode), Bounds = new Rectangle(18, 16, 504, 140) };
            var approve = new Button { Text = Loc.T("realtimeui.approve"), Bounds = new Rectangle(306, 183, 100, 32) };
            var reject = new Button { Text = Loc.T("realtimeui.reject"), Bounds = new Rectangle(416, 183, 100, 32) };
            _refreshApproval = () =>
            {
                form.Text = Loc.T("realtimeui.approve.phone.real.time.typing");
                label.Text = Loc.T("realtimeui.phone.verification.code.after.approval.claim.permission.on.your", request.DeviceLabel, request.CheckCode);
                approve.Text = Loc.T("realtimeui.approve");
                reject.Text = Loc.T("realtimeui.reject");
            };
            var timer = new Timer { Interval = 30_000 };
            approve.Click += (_, _) => { answer.TrySetResult(true); form.Close(); };
            reject.Click += (_, _) => form.Close();
            timer.Tick += (_, _) => form.Close();
            form.FormClosed += (_, _) =>
            {
                answer.TrySetResult(false);
                timer.Dispose(); cancellation.Dispose();
                if (ReferenceEquals(_approval, form)) { _approval = null; _refreshApproval = null; }
                form.Dispose();
            };
            form.Controls.AddRange([label, approve, reject]);
            form.CancelButton = reject;
            // A normal explicit approval dialog may take focus. Default action is rejection.
            form.AcceptButton = reject;
            _approval = form;
            timer.Start();
            form.Show(owner);
        });
        return answer.Task;
    }

    public void CloseApproval() => Post(() => _approval?.Close());

    public void ShowLocalReason(string reason) => Post(() => MessageBox.Show(owner, Loc.Localize(reason), Loc.T("realtimeui.real.time.typing.target"),
        MessageBoxButtons.OK, MessageBoxIcon.Information));
    public void ShowTargetFailure(string reason) => ShowLocalReason(reason);

    public void ShowState(RealtimeView view) => Post(() =>
    {
        if (view.State is RealtimeState.DISABLED or RealtimeState.REQUESTED or RealtimeState.REVOKED)
        {
            _overlay?.Hide();
            return;
        }
        if (_overlay is null)
        {
            _overlay = new RealtimeOverlay();
            _overlay.PauseRequested += () => PauseRequested?.Invoke();
            _overlay.StopRequested += () => StopRequested?.Invoke();
        }
        var rectangle = Screen.FromControl(owner).WorkingArea;
        _overlay.UpdateState(view, new LocalRect(rectangle.X, rectangle.Y, rectangle.Width, rectangle.Height));
    });

    public void Dispose()
    {
        Loc.LanguageChanged -= LanguageChanged;
        _disposed = true;
        _approval?.Close();
        _overlay?.Dispose();
    }
}
