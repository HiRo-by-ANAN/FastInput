using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;
using Microsoft.Win32;
using FastInput.Windows.Realtime;
using Timer = System.Windows.Forms.Timer;

namespace FastInput.Windows;

internal sealed class MainForm : Form
{
    private const int AutostartMenuId = 0x1F10;
    private const int RealtimeMenuId = 0x1F20;
    private const int VirtualEnterMenuId = 0x1F30;
    private RealtimeRuntime? _realtime;
    private readonly RealtimeSettings _settings = RealtimeSettings.Load();
    private readonly ComboBox _languageBox = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 115 };
    private bool _applyingLanguage;
    private readonly string[] _serverArguments;
    private readonly bool _smokeTest;
    private readonly BackendHost _host = new();
    private readonly CancellationTokenSource _lifetime = new();
    private readonly WebView2 _web = new() { Dock = DockStyle.Fill, Visible = false };
    private readonly Timer _monitor = new() { Interval = 300 };
    private readonly CheckBox _realtimeBox = new() { Text = Loc.T("mainform.allow.real.time.typing.from.phone"), AutoSize = true, Left = 12, Top = 8 };
    private readonly CheckBox _virtualEnterBox = new() { Text = Loc.T("mainform.allow.virtual.enter.off.by.default"), AutoSize = true, Left = 190, Top = 8 };
    private readonly FlowLayoutPanel _strip = new() { Dock = DockStyle.Bottom, AutoSize = true, WrapContents = true, Padding = new Padding(8, 4, 8, 4) };
    private readonly Timer _smokeTimeout = new() { Interval = 30_000 };
    private readonly Stopwatch _elapsed = Stopwatch.StartNew();
    private Task? _startup;
    private bool _closing, _allowClose, _requestingClose, _pageReady, _smokeFinished, _lastMaximized, _webShutdown;
    public int ExitCode { get; private set; }

    public MainForm(string[] serverArguments, bool minimized, bool smokeTest)
    {
        Loc.SetLanguage(_settings.Language);
        _serverArguments = serverArguments;
        _smokeTest = smokeTest;
        _host.BridgeCreated += bridge =>
        {
            if (!smokeTest) _realtime = new RealtimeRuntime(this, bridge, _settings);
            else bridge.MessageReceived += message =>
            {
                // Smoke checks page startup independently of saved realtime settings or native dialogs.
                if (message is RequestMessage request) bridge.TrySend(new RevokeCommand(request.RequestId, "unsupported"));
            };
        };
        Text = "FastInput";
        AutoScaleDimensions = new SizeF(96, 96);
        AutoScaleMode = AutoScaleMode.Dpi;
        MinimumSize = new Size(700, 510);
        Size = new Size(1120, 740);
        StartPosition = FormStartPosition.CenterScreen;
        if (minimized) WindowState = FormWindowState.Minimized;
        Load += (_, _) =>
        {
            // Restore physical bounds after WinForms has applied the monitor's initial DPI.
            if (!smokeTest) WindowPlacement.Restore(this);
            _lastMaximized = WindowState == FormWindowState.Maximized;
            if (minimized) WindowState = FormWindowState.Minimized;
        };
        try { Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath); }
        catch (Exception) { /* Explorer still uses the embedded executable icon. */ }
        Controls.Add(_web);
        _realtimeBox.AutoCheck = false; _virtualEnterBox.AutoCheck = false;
        _realtimeBox.Click += (_, _) => ChangeRealtimeSetting();
        _virtualEnterBox.Click += (_, _) => ChangeVirtualEnterSetting();
        _strip.Controls.Add(_realtimeBox); _strip.Controls.Add(_virtualEnterBox);
        _languageBox.Items.AddRange([Loc.T("language.zh"), Loc.T("language.ja"), Loc.T("language.en")]);
        _languageBox.SelectedIndexChanged += (_, _) =>
        {
            if (!_applyingLanguage && _languageBox.SelectedIndex >= 0) ChangeLanguage(Loc.Languages[_languageBox.SelectedIndex]);
        };
        _strip.Controls.Add(_languageBox);
        Controls.Add(_strip);
        ApplyLanguage();
        ApplyTheme();
        Shown += (_, _) =>
        {
            if (_closing) return;
            if (_smokeTest) _smokeTimeout.Start();
            _startup = StartAsync();
        };
        _monitor.Tick += (_, _) => { CheckServer(); _realtimeBox.Checked = _realtime?.Enabled == true; _virtualEnterBox.Checked = _realtime?.VirtualEnterEnabled == true; };
        _smokeTimeout.Tick += (_, _) => Fail(Loc.T("mainform.the.desktop.page.did.not.load.within.30.seconds"));
        Resize += (_, _) =>
        {
            if (WindowState != FormWindowState.Minimized)
                _lastMaximized = WindowState == FormWindowState.Maximized;
        };
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        var menu = GetSystemMenu(Handle, false);
        AppendMenu(menu, 0x800, 0, null);
        AppendMenu(menu, 0, AutostartMenuId, Loc.T("mainform.start.with.windows"));
        AppendMenu(menu, 0, RealtimeMenuId, Loc.T("mainform.allow.real.time.typing.from.phone"));
        AppendMenu(menu, 0, VirtualEnterMenuId, Loc.T("mainform.allow.virtual.enter.off.by.default"));
        _ = WTSRegisterSessionNotification(Handle, 0);
        ApplyTheme();
    }

    protected override void WndProc(ref Message m)
    {
        if (m.Msg == EmergencyHotKey.Message && (int)m.WParam == EmergencyHotKey.Id)
        {
            _realtime?.EmergencyStop();
            return;
        }
        if (m.Msg == 0x218 && (int)m.WParam == 4) _realtime?.NotifyLocked(); // PBT_APMSUSPEND
        if (m.Msg == 0x2B1 && (int)m.WParam == 7) _realtime?.NotifyLocked(); // WTS_SESSION_LOCK
        if (m.Msg == 0x116) UpdateAutostartMenu(); // WM_INITMENU
        if (m.Msg == 0x112 && ((long)m.WParam & 0xFFF0) == RealtimeMenuId)
        {
            ChangeRealtimeSetting();
            return;
        }
        if (m.Msg == 0x112 && ((long)m.WParam & 0xFFF0) == VirtualEnterMenuId)
        {
            ChangeVirtualEnterSetting();
            return;
        }
        if (m.Msg == 0x112 && ((long)m.WParam & 0xFFF0) == AutostartMenuId)
        {
            try { Autostart.SetEnabled(!Autostart.IsEnabled()); UpdateAutostartMenu(); }
            catch (Exception error) { ShowError(Loc.T("mainform.cannot.change.startup.setting"), error.Message); }
            return;
        }
        base.WndProc(ref m);
        if (m.Msg is 0x1A or 0x320) ApplyTheme();
    }

    private void UpdateAutostartMenu()
    {
        try { CheckMenuItem(GetSystemMenu(Handle, false), AutostartMenuId, Autostart.IsEnabled() ? 8u : 0u); }
        catch (Exception) { /* Report registry errors when changing the setting. */ }
        CheckMenuItem(GetSystemMenu(Handle, false), RealtimeMenuId, _realtime?.Enabled == true ? 8u : 0u);
        CheckMenuItem(GetSystemMenu(Handle, false), VirtualEnterMenuId, _realtime?.VirtualEnterEnabled == true ? 8u : 0u);
        _realtimeBox.Checked = _realtime?.Enabled == true;
        _virtualEnterBox.Checked = _realtime?.VirtualEnterEnabled == true;
    }


    private void ChangeLanguage(string code)
    {
        if (!Loc.IsSupported(code) || code == _settings.Language) { PostLanguage(); return; }
        var previous = _settings.Language;
        _settings.Language = code;
        try { _settings.Save(); }
        catch (Exception)
        {
            _settings.Language = previous;
            ApplyLanguage();
            PostLanguage();
            ShowError(Loc.T("language.save.failed"), Loc.T("mainform.check.write.permissions.for.the.local.settings.folder"));
            return;
        }
        Loc.SetLanguage(code);
        ApplyLanguage();
        PostLanguage();
    }

    private void PostLanguage()
    {
        if (_pageReady && !_closing)
            _web.CoreWebView2.PostWebMessageAsJson(JsonSerializer.Serialize(new { type = "language.set", lang = Loc.Language }));
    }

    private void ApplyLanguage()
    {
        _applyingLanguage = true;
        try
        {
            _languageBox.SelectedIndex = Array.IndexOf(Loc.Languages.ToArray(), Loc.Language);
            _languageBox.AccessibleName = Loc.T("language");
            _realtimeBox.Text = Loc.T("mainform.allow.real.time.typing.from.phone");
            _virtualEnterBox.Text = Loc.T("mainform.allow.virtual.enter.off.by.default");
            if (!IsHandleCreated) return;
            var menu = GetSystemMenu(Handle, false);
            ModifyMenu(menu, AutostartMenuId, 0, AutostartMenuId, Loc.T("mainform.start.with.windows"));
            ModifyMenu(menu, RealtimeMenuId, 0, RealtimeMenuId, _realtimeBox.Text);
            ModifyMenu(menu, VirtualEnterMenuId, 0, VirtualEnterMenuId, _virtualEnterBox.Text);
            UpdateAutostartMenu();
        }
        finally { _applyingLanguage = false; }
    }

    private void ChangeVirtualEnterSetting()
    {
        if (_realtime is null) { MessageBox.Show(this, Loc.T("mainform.native.real.time.typing.is.not.ready"), "FastInput"); return; }
        var enabled = !_realtime.VirtualEnterEnabled;
        if (enabled && MessageBox.Show(this,
            Loc.T("mainform.this.lets.your.phone.press.enter.on.your.computer"),
            Loc.T("mainform.enable.virtual.enter.from.phone"), MessageBoxButtons.YesNo, MessageBoxIcon.Warning,
            MessageBoxDefaultButton.Button2) != DialogResult.Yes) return;
        try { _realtime.SetVirtualEnterEnabled(enabled); }
        catch (Exception) { ShowError(Loc.T("mainform.cannot.save.enter.setting"), Loc.T("mainform.check.write.permissions.for.the.local.settings.folder")); }
        UpdateAutostartMenu();
    }

    private void ChangeRealtimeSetting()
    {
        if (_realtime is null)
        {
            MessageBox.Show(this, Loc.T("mainform.real.time.typing.is.unavailable.in.legacy.mode.or"), "FastInput");
            return;
        }
        var enabled = !_realtime.Enabled;
        if (enabled && MessageBox.Show(this,
                Loc.T("mainform.each.phone.request.still.needs.approval.on.your.computer"),
                Loc.T("mainform.allow.real.time.typing.from.phone"), MessageBoxButtons.YesNo, MessageBoxIcon.Warning,
                MessageBoxDefaultButton.Button2) != DialogResult.Yes) return;
        try
        {
            if (!_realtime.SetEnabled(enabled))
                MessageBox.Show(this, _realtime.UnavailableReason, Loc.T("mainform.cannot.enable.real.time.typing"), MessageBoxButtons.OK, MessageBoxIcon.Warning);
            UpdateAutostartMenu();
        }
        catch (Exception) { ShowError(Loc.T("mainform.cannot.save.real.time.typing.settings"), Loc.T("mainform.check.write.permissions.for.the.local.settings.folder")); }
    }

    private void ApplyTheme()
    {
        var dark = false;
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            dark = key?.GetValue("AppsUseLightTheme") is int value && value == 0;
        }
        catch (Exception) { }
        BackColor = dark ? Color.FromArgb(18, 29, 26) : Color.FromArgb(243, 245, 241);
        _strip.BackColor = BackColor; _realtimeBox.ForeColor = _virtualEnterBox.ForeColor = dark ? Color.FromArgb(230, 238, 232) : Color.FromArgb(30, 40, 34);
        _web.DefaultBackgroundColor = BackColor;
        if (IsHandleCreated)
        {
            int enabled = dark ? 1 : 0;
            _ = DwmSetWindowAttribute(Handle, 20, ref enabled, sizeof(int));
        }
    }

    private async Task StartAsync()
    {
        try
        {
            await _host.StartAsync(_serverArguments, _lifetime.Token);
            _lifetime.Token.ThrowIfCancellationRequested();
            _monitor.Start();
            var environment = await CoreWebView2Environment.CreateAsync(
                userDataFolder: Path.Combine(WindowPlacement.DataDirectory, "WebView2")).WaitAsync(_lifetime.Token);
            _lifetime.Token.ThrowIfCancellationRequested();
            await _web.EnsureCoreWebView2Async(environment).WaitAsync(_lifetime.Token);
            _lifetime.Token.ThrowIfCancellationRequested();
            var core = _web.CoreWebView2;
            core.Profile.PreferredColorScheme = CoreWebView2PreferredColorScheme.Auto;
            var settings = core.Settings;
#if !DEBUG
            settings.AreDevToolsEnabled = false;
#endif
            settings.IsStatusBarEnabled = false;
            settings.IsGeneralAutofillEnabled = false;
            settings.IsPasswordAutosaveEnabled = false;
            settings.AreDefaultContextMenusEnabled = true;
            settings.AreDefaultScriptDialogsEnabled = false;
            settings.IsWebMessageEnabled = true;
            core.NavigationStarting += NavigationStarting;
            core.FrameNavigationStarting += NavigationStarting;
            core.NewWindowRequested += (_, e) =>
            {
                e.Handled = true;
                if (IsSameOrigin(e.Uri)) core.Navigate(e.Uri);
                else OpenExternal(e.Uri);
            };
            core.NavigationCompleted += NavigationCompleted;
            core.ScriptDialogOpening += ScriptDialogOpening;
            // Top-level event only; never subscribe to CoreWebView2Frame.WebMessageReceived.
            core.WebMessageReceived += WebMessageReceived;
            core.WindowCloseRequested += (_, _) => _ = FinishCloseAsync();
            core.ProcessFailed += (_, e) => Fail(Loc.T("mainform.the.web.rendering.process.failed", e.ProcessFailedKind));
            core.DownloadStarting += (_, e) =>
            {
                // Only our authenticated file endpoint may start a download; keep WebView2's default save UI.
                var source = e.DownloadOperation.Uri;
                e.Cancel = !IsSameOrigin(source)
                    || !System.Text.RegularExpressions.Regex.IsMatch(new Uri(source).AbsolutePath,
                        @"\A/api/files/[0-9a-f]{16}\z");
            };
            core.LaunchingExternalUriScheme += (_, e) => e.Cancel = true;
            core.WebResourceResponseReceived += (_, e) =>
            {
                if (IsSameOrigin(e.Request.Uri) && new Uri(e.Request.Uri).AbsolutePath == "/api/shutdown"
                    && e.Request.Method == "POST" && e.Response.StatusCode == 200) _webShutdown = true;
            };
            core.Navigate(_host.DesktopUrl.AbsoluteUri);
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { }
        catch (WebView2RuntimeNotFoundException)
        {
            var message = Loc.T("mainform.microsoft.edge.webview2.runtime.is.missing.so.fastinput.cannot");
            if (!_closing && !_smokeTest && MessageBox.Show(this, message, Loc.T("mainform.webview2.runtime.required"),
                    MessageBoxButtons.YesNo, MessageBoxIcon.Information) == DialogResult.Yes)
                OpenExternal("https://developer.microsoft.com/microsoft-edge/webview2/");
            Fail(Loc.T("mainform.microsoft.edge.webview2.runtime.was.not.found"), showMessage: false);
        }
        catch (Exception) { if (!_closing) Fail(Loc.T("mainform.cannot.start.fastinput.check.that.the.local.server.and")); }
    }

    private bool IsSameOrigin(string source) => Uri.TryCreate(source, UriKind.Absolute, out var uri)
        && uri.Scheme == Uri.UriSchemeHttp && uri.Host == "127.0.0.1"
        && uri.Port == _host.DesktopUrl.Port && string.IsNullOrEmpty(uri.UserInfo);

    private void NavigationStarting(object? sender, CoreWebView2NavigationStartingEventArgs e)
    {
        if (IsSameOrigin(e.Uri)) return;
        e.Cancel = true;
        OpenExternal(e.Uri);
    }

    private void OpenExternal(string source)
    {
        if (!Uri.TryCreate(source, UriKind.Absolute, out var uri)
            || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps)
            || !string.IsNullOrEmpty(uri.UserInfo)) return;
        try { Process.Start(new ProcessStartInfo(uri.AbsoluteUri) { UseShellExecute = true }); }
        catch (Exception error) { if (!_smokeTest) ShowError(Loc.T("mainform.cannot.open.browser"), error.Message); }
    }

    private void NavigationCompleted(object? sender, CoreWebView2NavigationCompletedEventArgs e)
    {
        if (_closing) return;
        if (!e.IsSuccess || e.HttpStatusCode >= 400)
        {
            if (e.WebErrorStatus != CoreWebView2WebErrorStatus.OperationCanceled)
                Fail(Loc.T("mainform.cannot.load.the.desktop.page.http", e.WebErrorStatus, e.HttpStatusCode));
            return;
        }
        if (!IsSameOrigin(_web.CoreWebView2.Source)) return;
        _pageReady = true;
        PostLanguage();
        _web.Visible = true;
        if (_smokeTest && !_smokeFinished)
        {
            _smokeFinished = true;
            _smokeTimeout.Stop();
            Console.WriteLine($"SMOKE OK {_web.CoreWebView2.DocumentTitle} {_elapsed.ElapsedMilliseconds}");
            _ = FinishCloseAsync();
        }
    }

    private void ScriptDialogOpening(object? sender, CoreWebView2ScriptDialogOpeningEventArgs e)
    {
        // Defer modal UI to avoid reentering WebView2 through the WinForms message loop.
        var deferral = e.GetDeferral();
        BeginInvoke(new Action(() =>
        {
            try
            {
                if (_closing) return;
                if (e.Kind == CoreWebView2ScriptDialogKind.Beforeunload)
                {
                    if (MessageBox.Show(this, Loc.T("mainform.some.text.has.not.synced.yet.leave.anyway"), "FastInput",
                            MessageBoxButtons.YesNo, MessageBoxIcon.Warning,
                            MessageBoxDefaultButton.Button2) == DialogResult.Yes) e.Accept();
                    else _requestingClose = false;
                }
                else if (e.Kind == CoreWebView2ScriptDialogKind.Alert)
                {
                    MessageBox.Show(this, e.Message, "FastInput", MessageBoxButtons.OK, MessageBoxIcon.Information);
                    e.Accept();
                }
                else if (e.Kind == CoreWebView2ScriptDialogKind.Confirm)
                {
                    if (MessageBox.Show(this, e.Message, "FastInput", MessageBoxButtons.YesNo,
                            MessageBoxIcon.Question) == DialogResult.Yes) e.Accept();
                }
                else
                {
                    using var prompt = new Form { Text = "FastInput", Size = new Size(440, 190),
                        StartPosition = FormStartPosition.CenterParent, MinimizeBox = false, MaximizeBox = false };
                    var label = new Label { Text = e.Message, Dock = DockStyle.Top, Height = 55 };
                    var input = new TextBox { Text = e.DefaultText, Dock = DockStyle.Top };
                    var ok = new Button { Text = Loc.T("mainform.ok"), DialogResult = DialogResult.OK, Dock = DockStyle.Bottom };
                    var cancel = new Button { Text = Loc.T("mainform.cancel"), DialogResult = DialogResult.Cancel, Dock = DockStyle.Bottom };
                    prompt.Controls.AddRange([cancel, ok, input, label]);
                    prompt.AcceptButton = ok;
                    prompt.CancelButton = cancel;
                    if (prompt.ShowDialog(this) == DialogResult.OK) { e.ResultText = input.Text; e.Accept(); }
                }
            }
            finally { deferral.Complete(); }
        }));
    }

    private void WebMessageReceived(object? sender, CoreWebView2WebMessageReceivedEventArgs e)
    {
        if (_closing || !IsSameOrigin(e.Source) || !IsSameOrigin(_web.CoreWebView2.Source)) return;
        try
        {
            using var document = JsonDocument.Parse(e.WebMessageAsJson);
            var message = document.RootElement;
            if (message.ValueKind != JsonValueKind.Object || !message.TryGetProperty("type", out var type) || type.ValueKind != JsonValueKind.String) return;
            switch (type.GetString())
            {
                case "language.set":
                    if (!message.TryGetProperty("lang", out var lang) || lang.ValueKind != JsonValueKind.String
                        || !Loc.IsSupported(lang.GetString())) return;
                    ChangeLanguage(lang.GetString()!);
                    return;
                case "autostart.get": break;
                case "autostart.set":
                    if (!message.TryGetProperty("enabled", out var enabled)
                        || enabled.ValueKind is not (JsonValueKind.True or JsonValueKind.False)) return;
                    Autostart.SetEnabled(enabled.GetBoolean());
                    break;
                default: return;
            }
            _web.CoreWebView2.PostWebMessageAsJson(JsonSerializer.Serialize(new
                { type = "autostart.state", enabled = Autostart.IsEnabled() }));
            UpdateAutostartMenu();
        }
        catch (JsonException) { }
        catch (InvalidOperationException) { /* Ignore malformed messages. */ }
        catch (Exception error) { ShowError(Loc.T("mainform.cannot.read.or.write.startup.settings"), error.Message); }
    }

    private void CheckServer()
    {
        if (_closing || _host.IsRunning) return;
        if (_host.ExitCode == 0 && _webShutdown) _ = FinishCloseAsync();
        else Fail(Loc.T("mainform.the.local.server.exited.unexpectedly.exit.code.reopen.fastinput", _host.ExitCode));
    }

    private void Fail(string reason, bool showMessage = true)
    {
        if (_closing || (_smokeTest && _smokeFinished)) return;
        ExitCode = 1;
        if (_smokeTest)
        {
            _smokeFinished = true;
            Console.WriteLine($"SMOKE FAIL {reason.Replace('\r', ' ').Replace('\n', ' ')}");
        }
        else if (showMessage) ShowError(Loc.T("mainform.fastinput.error"), reason);
        _ = FinishCloseAsync();
    }

    private void ShowError(string title, string message) =>
        MessageBox.Show(this, message, title, MessageBoxButtons.OK, MessageBoxIcon.Error);

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        if (!_allowClose)
        {
            e.Cancel = true;
            if (!_closing && !_requestingClose)
            {
                if (_pageReady && !_smokeTest) _ = RequestPageCloseAsync();
                else _ = FinishCloseAsync();
            }
        }
        base.OnFormClosing(e);
    }

    private async Task RequestPageCloseAsync()
    {
        _requestingClose = true;
        try
        {
            // Page.close runs real beforeunload hooks even without user activation.
            // WindowCloseRequested arrives after the user accepts leaving.
            await _web.CoreWebView2.CallDevToolsProtocolMethodAsync("Page.close", "{}");
        }
        catch (Exception error)
        {
            _requestingClose = false;
            if (!_closing && MessageBox.Show(this, Loc.T("mainform.cannot.check.for.unsynced.text.close.anyway", error.Message),
                    "FastInput", MessageBoxButtons.YesNo, MessageBoxIcon.Warning,
                    MessageBoxDefaultButton.Button2) == DialogResult.Yes) await FinishCloseAsync();
        }
    }

    private async Task FinishCloseAsync()
    {
        if (_closing) return;
        _closing = true;
        _monitor.Stop();
        _smokeTimeout.Stop();
        if (!_smokeTest) WindowPlacement.Save(this, _lastMaximized);
        _lifetime.Cancel();
        _realtime?.EmergencyStop();
        Enabled = false;
        // Complete pending startup before disposing the owned process or WebView.
        await Task.Yield();
        if (_startup is not null) await _startup;
        try { await _host.StopAsync(); }
        finally { _allowClose = true; Close(); }
    }

    public void BringToForeground()
    {
        if (_closing) return;
        if (WindowState == FormWindowState.Minimized)
            WindowState = _lastMaximized ? FormWindowState.Maximized : FormWindowState.Normal;
        Show();
        Activate();
        _ = SetForegroundWindow(Handle);
    }

    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        _ = WTSUnRegisterSessionNotification(Handle);
        _monitor.Dispose();
        _smokeTimeout.Dispose();
        _web.Dispose();
        _realtime?.Dispose();
        _host.Dispose();
        _lifetime.Dispose();
        base.OnFormClosed(e);
    }

    [DllImport("user32.dll")] private static extern nint GetSystemMenu(nint window, bool revert);
    [DllImport("wtsapi32.dll")] private static extern bool WTSRegisterSessionNotification(nint window, uint flags);
    [DllImport("wtsapi32.dll")] private static extern bool WTSUnRegisterSessionNotification(nint window);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool AppendMenu(nint menu, uint flags, nuint id, string? text);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool ModifyMenu(nint menu, uint position, uint flags, nuint id, string text);
    [DllImport("user32.dll")] private static extern uint CheckMenuItem(nint menu, uint id, uint flags);
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(nint window);
    [DllImport("dwmapi.dll")] private static extern int DwmSetWindowAttribute(nint window, int attribute, ref int value, int size);
}
