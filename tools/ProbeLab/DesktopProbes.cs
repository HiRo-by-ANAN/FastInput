using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using System.Drawing;

namespace ProbeLab;

public static class DesktopProbes
{
    // Created inside fixture-winforms, so the overlay and every clickable child
    // share the exact fixture PID checked by OwnedTarget.
    public static Form CreateOverlay(Rectangle bounds, Action<object> log)
        => new NoActivateOverlay(bounds, log);

    private sealed class NoFocusButton : Button
    {
        public NoFocusButton() { SetStyle(ControlStyles.Selectable, false); }
    }

    private sealed class NoActivateOverlay : Form
    {
        private readonly Action<object> log;
        public NoActivateOverlay(Rectangle bounds, Action<object> log)
        {
            this.log = log;
            Name = "overlay";
            Text = "ProbeLab overlay";
            FormBorderStyle = FormBorderStyle.FixedToolWindow;
            StartPosition = FormStartPosition.Manual;
            Bounds = bounds;
            ShowInTaskbar = false;
            TopMost = true;
            // 修正（M0 發現）：標準 Button 被點擊時會 SetFocus 到自己，讓不啟用的小介面變成前景並搶走焦點。
            // 改用 ControlStyles.Selectable=false 的按鈕，點擊仍會觸發 Click 但不取得焦點。
            var pause = new NoFocusButton { Name = "pause", Text = "暫停", Bounds = new Rectangle(8, 6, 82, 30), TabStop = false };
            var stop = new NoFocusButton { Name = "stop", Text = "停止", Bounds = new Rectangle(98, 6, 82, 30), TabStop = false };
            pause.Click += (_, _) => this.log(new { eventType = "overlay-click", button = "pause", ticks = Stopwatch.GetTimestamp(), utc = DateTimeOffset.UtcNow });
            stop.Click += (_, _) => this.log(new { eventType = "overlay-click", button = "stop", ticks = Stopwatch.GetTimestamp(), utc = DateTimeOffset.UtcNow });
            Controls.Add(pause);
            Controls.Add(stop);
        }
        protected override bool ShowWithoutActivation => true;
        protected override CreateParams CreateParams
        {
            get { var p = base.CreateParams; p.ExStyle |= 0x08000000 | 0x00000008 | 0x00000080; return p; }
        }
        protected override void WndProc(ref Message m)
        {
            if (m.Msg == 0x21)
            {
                log(new { eventType = "overlay-mouseactivate", ticks = Stopwatch.GetTimestamp(), result = "MA_NOACTIVATE" });
                m.Result = 3;
                return;
            }
            base.WndProc(ref m);
        }
    }

    public static void Overlay(Report r)
    {
        using var fixture = FixtureSession.Start(r);
        var field = fixture.Handle("single");
        Native.GetWindowRect(field, out var fieldRect);
        var screens = Screen.AllScreens.Select(s => new
        {
            s.DeviceName, bounds = s.Bounds, workArea = s.WorkingArea, s.Primary,
            dpi = ReadScreenDpi(s), dpiSource = "GetDpiForMonitor(MDT_EFFECTIVE_DPI)"
        }).ToArray();
        var work = Screen.FromHandle(field).WorkingArea;
        int x = Math.Clamp(fieldRect.Left, work.Left, Math.Max(work.Left, work.Right - 200));
        int y = Math.Clamp(fieldRect.Bottom + 6, work.Top, Math.Max(work.Top, work.Bottom - 84));
        r.Add("螢幕 DPI 與欄位矩形", new { screens, field = field.ToInt64(), fieldRect = new { fieldRect.Left, fieldRect.Top, fieldRect.Right, fieldRect.Bottom }, overlayRectangle = new { x, y, width = 200, height = 84 } });
        fixture.Send(new { op = "overlay", left = x, top = y, width = 200, height = 84 });
        fixture.Focus("single");
        foreach (var buttonId in new[] { "pause", "stop" })
        {
            if(!fixture.Focus("single")) {r.Add("overlay前置條件未執行",new{buttonId},null,"原始fixture欄位必須先取得焦點");continue;}
            var button = fixture.Handle(buttonId);
            if (button == 0) { r.Add("overlay 控制項存在", new { buttonId, handle = 0 }, false, "fixture 必須公開 overlay 按鈕 HWND"); continue; }
            Native.GetWindowRect(button, out var rect);
            var before = FocusSnapshot();
            int clicksBefore = fixture.Snapshot().GetProperty("overlayClicks").GetProperty(buttonId).GetInt32();
            int clickX = (rect.Left + rect.Right) / 2, clickY = (rect.Top + rect.Bottom) / 2;
            var hit = Native.WindowFromPoint(new Native.POINT { X = clickX, Y = clickY });
            bool exactHit = hit == button;
            // Refuse if a third-party window covers the button. The target and
            // foreground PID checks are repeated inside Input immediately before injection.
            uint sent = 0;
            if (exactHit && fixture.Target.Guard(button, "overlay hit-test before click"))
                sent = fixture.Target.Input(button, Native.MouseClick(clickX, clickY), "overlay mouse click " + buttonId);
            else r.Add("拒絕 overlay 滑鼠注入", new { buttonId, expected = button.ToInt64(), actualHit = hit.ToInt64() }, false, "hit-test 必須精確命中本 fixture 的按鈕");
            Pump(180);
            var after = FocusSnapshot();
            fixture.Send(new { op = "snapshot" });
            int clicksAfter = fixture.Snapshot().GetProperty("overlayClicks").GetProperty(buttonId).GetInt32();
            r.Add("overlay 點擊 " + buttonId, new { before, after, sent, exactHit, clicksBefore, clicksAfter, fixture = fixture.Snapshot(), events = fixture.ReadEvents() }, sent > 0 && before.GuiInfoAvailable && after.GuiInfoAvailable && before.Focus==field.ToInt64() && before == after && clicksAfter == clicksBefore + 1,
                "滑鼠事件送入成功、前景 HWND 與 GUI 焦點 HWND 不變，且該 WinForms 子按鈕 click 計數正好加一");
        }
    }

    private static object ReadScreenDpi(Screen screen)
    {
        var p = new Native.POINT { X = screen.Bounds.Left + 1, Y = screen.Bounds.Top + 1 };
        var monitor = Native.MonitorFromPoint(p, 2);
        try
        {
            int hr = Native.GetDpiForMonitor(monitor, 0, out uint x, out uint y);
            return new { hresult = hr, x, y };
        }
        catch (Exception e) when (e is DllNotFoundException or EntryPointNotFoundException)
        { return new { unavailable = e.GetType().Name }; }
    }

    private sealed record FocusState(long Foreground, long Focus, uint ForegroundPid, uint ThreadId, bool GuiInfoAvailable);
    private static FocusState FocusSnapshot()
    {
        var foreground = Native.GetForegroundWindow();
        uint thread = Native.GetWindowThreadProcessId(foreground, out uint pid);
        var info = new Native.GUITHREADINFO { cbSize = (uint)Marshal.SizeOf<Native.GUITHREADINFO>() };
        bool ok = Native.GetGUIThreadInfo(thread, ref info);
        return new(foreground.ToInt64(), ok ? info.hwndFocus.ToInt64() : 0, pid, thread, ok);
    }

    public static void Hooks(Report r)
    {
        using var fixture = FixtureSession.Start(r);
        fixture.Focus("single");
        var events = new List<HookEvent>();
        int deliberateDelay = 0;
        bool delayUsed = false;
        const ulong marker = Native.Marker;
        double measuredDelayMs = 0;
        Native.HookProc keyboard = (code, message, data) =>
        {
            if (code >= 0)
            {
                var raw = Marshal.PtrToStructure<Native.KBDLLHOOKSTRUCT>(data);
                bool own = raw.dwExtraInfo.ToUInt64() == marker;
                events.Add(new HookEvent("keyboard", message.ToInt64(), (raw.flags & 0x10) != 0, own, Stopwatch.GetTimestamp(), DateTimeOffset.UtcNow));
                if (own && !delayUsed && deliberateDelay > 0)
                {
                    delayUsed = true;
                    long delayStart = Stopwatch.GetTimestamp();
                    Thread.Sleep(deliberateDelay);
                    measuredDelayMs = Stopwatch.GetElapsedTime(delayStart).TotalMilliseconds;
                }
            }
            return Native.CallNextHookEx(0, code, message, data);
        };
        Native.HookProc mouse = (code, message, data) =>
        {
            if (code >= 0)
            {
                var raw = Marshal.PtrToStructure<Native.MSLLHOOKSTRUCT>(data);
                events.Add(new HookEvent("mouse", message.ToInt64(), (raw.flags & 1) != 0, raw.dwExtraInfo.ToUInt64() == marker, Stopwatch.GetTimestamp(), DateTimeOffset.UtcNow));
            }
            return Native.CallNextHookEx(0, code, message, data);
        };
        var module = Native.GetModuleHandle(null);
        var keyboardHook = Native.SetWindowsHookEx(13, keyboard, module, 0);
        int keyboardError = Marshal.GetLastWin32Error();
        var mouseHook = Native.SetWindowsHookEx(14, mouse, module, 0);
        int mouseError = Marshal.GetLastWin32Error();
        r.Add("安裝低階鉤子", new { keyboardHandle = keyboardHook.ToInt64(), keyboardError, mouseHandle = mouseHook.ToInt64(), mouseError }, keyboardHook != 0 && mouseHook != 0,
            "兩種鉤子都成功安裝；不保存 vkCode、scanCode、滑鼠位置或真實鍵值");
        try
        {
            if (keyboardHook != 0 && mouseHook != 0)
            {
                foreach (int delay in new[] { 0, 100, 300, 1000 })
                {
                    // Fresh hooks per phase: an earlier timeout must not invalidate
                    // the timing measurements of the following phase.
                    if (delay != 0)
                    {
                        Native.UnhookWindowsHookEx(keyboardHook);
                        keyboardHook = Native.SetWindowsHookEx(13, keyboard, module, 0);
                    }
                    int phaseHookError = keyboardHook == 0 ? Marshal.GetLastWin32Error() : 0;
                    deliberateDelay = delay;
                    delayUsed = false;
                    measuredDelayMs = 0;
                    int start = events.Count;
                    uint first = SendMarked(fixture, Native.Unicode("x"), marker, "hook first marker");
                    Pump(delay + 250);
                    int afterFirst = events.Count;
                    deliberateDelay = 0;
                    uint followup = SendMarked(fixture, Native.Unicode("y"), marker, "hook follow-up marker");
                    Pump(300);
                    var firstEvents = events.Skip(start).Take(afterFirst - start).ToArray();
                    var laterEvents = events.Skip(afterFirst).ToArray();
                    bool live = laterEvents.Any(e => e.Device == "keyboard" && e.MarkerMatches && e.Injected);
                    r.Add("鉤子 callback 延遲 " + delay + " ms", new
                    {
                        requestedDelayMs = delay, measuredDelayMs, delayUsed, firstSendInput = first, followupSendInput = followup,
                        hookInstalled = keyboardHook != 0, phaseHookError, firstEvents, followupEvents = laterEvents,
                        followupObserved = live,
                        interpretation = live ? "後續 marker 仍有回呼" : "未見後續 marker；可能遭移除，亦可能事件路由或訊息迴圈失敗，不能單憑此結果宣稱系統移除",
                        configuredTimeout = "未讀登錄；本實驗只能量測此主機觀察值"
                    }, first > 0 && followup > 0 && firstEvents.Any(e => e.MarkerMatches && e.Injected),
                        "本工具 marker 必須被辨識為 injected；延遲後的存活狀態另列原始觀察，不以系統版本假設判定");
                }
                deliberateDelay = 0;
                Native.UnhookWindowsHookEx(mouseHook);
                mouseHook = Native.SetWindowsHookEx(14, mouse, module, 0);
                int mouseReinstallError = mouseHook == 0 ? Marshal.GetLastWin32Error() : 0;
                int mouseStart = events.Count;
                var field = fixture.Handle("single");
                Native.GetWindowRect(field, out var rect);
                int px = rect.Left + 12, py = rect.Top + 8;
                var hit = Native.WindowFromPoint(new Native.POINT { X = px, Y = py });
                uint sentMouse = 0;
                if (hit == field && fixture.Target.Guard(field, "hook mouse hit-test"))
                    sentMouse = SendMarked(fixture, Native.MouseClick(px, py), marker, "hook mouse marker");
                Pump(300);
                var mouseEvents = events.Skip(mouseStart).ToArray();
                r.Add("滑鼠 injected 與 dwExtraInfo", new { sentMouse, mouseHookInstalled = mouseHook != 0, mouseReinstallError, exactHit = hit == field, events = mouseEvents }, sentMouse > 0 && mouseEvents.Any(e => e.Device == "mouse" && e.Injected && e.MarkerMatches),
                    "本 fixture 內的注入滑鼠事件必須有 LLMHF_INJECTED 與本工具 marker");
            }
            const int firstId = 0x6711, secondId = 0x6712;
            const uint modifiers = 0x4000 | 1 | 2 | 4; // NOREPEAT + ALT + CTRL + SHIFT
            bool firstHotkey = Native.RegisterHotKey(0, firstId, modifiers, 0x78);
            int firstError = Marshal.GetLastWin32Error();
            bool conflict = false;
            int conflictError = 0;
            try
            {
                if (firstHotkey)
                {
                    conflict = Native.RegisterHotKey(0, secondId, modifiers, 0x78);
                    conflictError = Marshal.GetLastWin32Error();
                }
                r.Add("RegisterHotKey 註冊與衝突", new { chord = "Ctrl+Alt+Shift+F9", firstRegistered = firstHotkey, firstError, secondAttempted = firstHotkey, secondRegistered = conflict, conflictError },
                    firstHotkey ? !conflict : null,
                    "第一筆成功後，同組合第二筆應失敗；若已被其他程式佔用則記錄未能執行自我衝突測試");
            }
            finally
            {
                if (conflict) Native.UnregisterHotKey(0, secondId);
                if (firstHotkey) Native.UnregisterHotKey(0, firstId);
            }
        }
        finally
        {
            if (keyboardHook != 0) Native.UnhookWindowsHookEx(keyboardHook);
            if (mouseHook != 0) Native.UnhookWindowsHookEx(mouseHook);
            GC.KeepAlive(keyboard);
            GC.KeepAlive(mouse);
            r.Add("低階鉤子原始事件（已去除真實內容）", new { frequency = Stopwatch.Frequency, events });
        }
    }

    private sealed record HookEvent(string Device, long EventType, bool Injected, bool MarkerMatches, long Ticks, DateTimeOffset Utc);

    private static uint SendMarked(FixtureSession fixture, Native.INPUT[] inputs, ulong marker, string label)
    {
        for (int i = 0; i < inputs.Length; i++)
        {
            if (inputs[i].type == 1) inputs[i].U.ki.dwExtraInfo = new UIntPtr(marker);
            else if (inputs[i].type == 0) inputs[i].U.mi.dwExtraInfo = new UIntPtr(marker);
        }
        return fixture.Target.Input(fixture.Handle("single"), inputs, label);
    }

    private static void Pump(int milliseconds)
    {
        var watch = Stopwatch.StartNew();
        while (watch.ElapsedMilliseconds < milliseconds)
        {
            Application.DoEvents();
            Thread.Sleep(5);
        }
    }
}
