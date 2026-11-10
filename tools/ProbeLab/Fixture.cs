using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Windows.Forms;
using System.Drawing;

namespace ProbeLab;

/// <summary>The only fixture GUI. It never accepts an external HWND or PID.</summary>
public static class Fixture
{
    public static void Run(string log, string commands, string state)
    {
        log = Path.GetFullPath(log);
        commands = Path.GetFullPath(commands);
        state = Path.GetFullPath(state);
        var directory = Path.GetDirectoryName(log)!;
        if (!string.Equals(directory, Path.GetDirectoryName(commands), StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(directory, Path.GetDirectoryName(state), StringComparison.OrdinalIgnoreCase) ||
            new[] { log, commands, state }.Distinct(StringComparer.OrdinalIgnoreCase).Count() != 3)
            throw new ArgumentException("Fixture log、commands、state 必須是同一資料夾內三個不同檔案。");
        Directory.CreateDirectory(directory);
        if (File.Exists(log) || File.Exists(state) || (File.Exists(commands) && new FileInfo(commands).Length != 0))
            throw new IOException("Fixture 拒絕使用既有 log/state 或非空白 commands。");
        for (var dir = new DirectoryInfo(directory); dir != null; dir = dir.Parent)
            if ((dir.Attributes & FileAttributes.ReparsePoint) != 0)
                throw new IOException("Fixture 拒絕使用 reparse-point 資料夾。");
        if (File.Exists(commands) && (File.GetAttributes(commands) & FileAttributes.ReparsePoint) != 0)
            throw new IOException("Fixture commands 不可為連結。");
        if (!File.Exists(commands))
            using (new FileStream(commands, FileMode.CreateNew, FileAccess.Write, FileShare.ReadWrite)) { }

        Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        using var output = new StreamWriter(new FileStream(log, FileMode.CreateNew, FileAccess.Write,
            FileShare.ReadWrite), new UTF8Encoding(false)) { AutoFlush = true };
        using var main = new FixtureWindow(output, commands, state);
        Application.Run(main);
    }

    private sealed class FixtureWindow : Form
    {
        private readonly StreamWriter output;
        private readonly string commands;
        private readonly string state;
        // Keep the original command file open without FileShare.Delete. Windows will
        // reject replacement/rename while the fixture is alive; reads use this handle.
        private readonly FileStream commandInput;
        private readonly Dictionary<string, TextBoxBase> controls = new();
        private readonly Dictionary<string, (int Start, int Length)> selection = new();
        private readonly System.Windows.Forms.Timer timer = new() { Interval = 15 };
        private readonly Form decoy;
        private Form? overlay;
        private readonly Dictionary<string, int> overlayClicks = new() { ["pause"] = 0, ["stop"] = 0 };
        private long lastCommandSeq;
        private object? lastCommandError;
        private int consumedCharacters;
        private bool closing;
        private bool initialized;

        public FixtureWindow(StreamWriter output, string commands, string state)
        {
            this.output = output;
            this.commands = commands;
            this.state = state;
            commandInput = new FileStream(commands, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            ValidateCommandPath();
            Text = $"ProbeLab fixture — PID {Environment.ProcessId}";
            Name = "ProbeLabFixture";
            StartPosition = FormStartPosition.CenterScreen;
            Size = new Size(650, 620);
            var panel = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 10,
                Padding = new Padding(12), AutoScroll = true };
            Controls.Add(panel);
            AddField(panel, "single", "單行 TextBox", false, false, false);
            AddField(panel, "multi", "多行 TextBox（AcceptsReturn/AcceptsTab）", true, false, false);
            AddField(panel, "rich", "RichTextBox", true, false, true);
            AddField(panel, "password", "PasswordChar TextBox（僅 fixture 測試字串）", false, false, false);
            AddField(panel, "readonly", "唯讀 TextBox", false, true, false);
            ((TextBox)controls["password"]).PasswordChar = '●';
            decoy = new Form { Text = $"ProbeLab decoy — PID {Environment.ProcessId}",
                Name = "ProbeLabDecoy", Size = new Size(410, 150), StartPosition = FormStartPosition.Manual,
                Location = new Point(20, 20) };
            var bait = new LoggedTextBox { Name = "decoy", Dock = DockStyle.Fill, Multiline = true,
                Text = "DECOY_UNCHANGED" };
            decoy.Controls.Add(bait);
            Register("decoy", bait);
            Activated += (_, _) => Log("window-activated", null, new { window = "main" });
            decoy.Activated += (_, _) => Log("window-activated", null, new { window = "decoy" });
            Shown += (_, _) =>
            {
                decoy.Show(this);
                Activate();
                controls["single"].Focus();
                initialized = true;
                Log("ready", null, new { frequency = Stopwatch.Frequency, commandFile = commands });
                Snapshot();
                timer.Start();
            };
            timer.Tick += (_, _) => Tick();
            FormClosing += (_, _) => { closing = true; timer.Stop(); };
            FormClosed += (_, _) =>
            {
                Log("closed", null);
                if (!decoy.IsDisposed) decoy.Close();
                if (overlay is { IsDisposed: false }) overlay.Close();
                commandInput.Dispose();
                timer.Dispose();
            };
        }

        private void AddField(TableLayoutPanel panel, string id, string label, bool multiline, bool readOnly, bool rich)
        {
            panel.Controls.Add(new Label { Text = label, AutoSize = true, Margin = new Padding(0, 8, 0, 2) });
            TextBoxBase box = rich
                ? new LoggedRichTextBox { Multiline = true, AcceptsTab = true }
                : new LoggedTextBox { Multiline = multiline, AcceptsReturn = multiline, AcceptsTab = multiline };
            box.Name = id;
            box.ReadOnly = readOnly;
            box.Dock = DockStyle.Top;
            box.Size = new Size(590, multiline ? 90 : 28);
            panel.Controls.Add(box);
            Register(id, box);
        }

        private void Register(string id, TextBoxBase box)
        {
            controls.Add(id, box);
            selection[id] = (-1, -1);
            Action<string, object?> message = (name, raw) =>
            {
                Log(name, id, raw);
                if (initialized && !closing) Snapshot();
            };
            if (box is LoggedTextBox text) text.Trace = message;
            if (box is LoggedRichTextBox rich)
            {
                rich.Trace = message;
                rich.SelectionChanged += (_, _) => message("SelectionChanged", null);
            }
            box.TextChanged += (_, _) => message("TextChanged", null);
            box.KeyDown += (_, e) => message("KeyDown", new { keyCode = (int)e.KeyCode, modifiers = (int)e.Modifiers });
            box.KeyUp += (_, e) => message("KeyUp", new { keyCode = (int)e.KeyCode, modifiers = (int)e.Modifiers });
            box.KeyPress += (_, e) => message("KeyPress", new { utf16 = (int)e.KeyChar });
            box.Enter += (_, _) => message("Enter", null);
            box.Leave += (_, _) => message("Leave", null);
        }

        private void Log(string type, string? id, object? raw = null)
        {
            TextBoxBase? box = id == null ? null : controls.GetValueOrDefault(id);
            output.WriteLine(JsonSerializer.Serialize(new
            {
                utc = DateTimeOffset.UtcNow, ticks = Stopwatch.GetTimestamp(), frequency = Stopwatch.Frequency,
                pid = Environment.ProcessId, type, control = id,
                text = box is { IsDisposed: false } ? box.Text : null,
                start = box is { IsDisposed: false } ? box.SelectionStart : (int?)null,
                length = box is { IsDisposed: false } ? box.SelectionLength : (int?)null,
                raw
            }));
        }

        private void Snapshot()
        {
            if (closing || !initialized) return;
            var values = controls.ToDictionary(pair => pair.Key, pair => (object)CreateControlState(pair.Value));
            if (overlay is { IsDisposed: false })
            {
                values["overlay"] = CreateControlState(overlay);
                foreach (var id in new[] { "pause", "stop" })
                    if (overlay.Controls.Find(id, true).FirstOrDefault() is { } button)
                        values[id] = CreateControlState(button);
            }
            var json = JsonSerializer.Serialize(new { pid = Environment.ProcessId, main = Handle.ToInt64(),
                decoy = decoy.Handle.ToInt64(), lastCommandSeq, lastCommandError, controls = values, overlayClicks,
                utc = DateTimeOffset.UtcNow, ticks = Stopwatch.GetTimestamp() });
            var temporary = state + ".writing";
            File.WriteAllText(temporary, json, new UTF8Encoding(false));
            // 主程式會頻繁讀取 state；Windows 在目標被開啟時改名覆蓋可能回 Access denied，需短暫重試。
            for (var attempt = 0; ; attempt++)
            {
                try { File.Move(temporary, state, true); break; }
                catch (Exception ex) when ((ex is IOException || ex is UnauthorizedAccessException) && attempt < 40)
                { System.Threading.Thread.Sleep(5); }
            }
        }

        private static object CreateControlState(Control control)
        {
            var rect = control.RectangleToScreen(control.ClientRectangle);
            var box = control as TextBoxBase;
            return new { hwnd = control.Handle.ToInt64(), text = box?.Text ?? "",
                start = box?.SelectionStart ?? 0, length = box?.SelectionLength ?? 0,
                canUndo = box?.CanUndo ?? false, focused = control.Focused,
                rect = new { x = rect.X, y = rect.Y, width = rect.Width, height = rect.Height } };
        }

        private void Tick()
        {
            try
            {
                foreach (var pair in controls)
                {
                    if (pair.Value.IsDisposed) continue;
                    var current = (pair.Value.SelectionStart, pair.Value.SelectionLength);
                    if (selection[pair.Key] != current)
                    {
                        selection[pair.Key] = current;
                        Log("SelectionChanged-polled", pair.Key);
                    }
                }
                ConsumeOneCommand();
                Snapshot();
            }
            catch (Exception ex) { Log("fixture-error", null, new { error = ex.Message }); }
        }

        private void ConsumeOneCommand()
        {
            ValidateCommandPath();
            if (commandInput.Length > 4 * 1024 * 1024) throw new IOException("Fixture command file exceeds 4 MiB.");
            commandInput.Seek(0, SeekOrigin.Begin);
            using var reader = new StreamReader(commandInput, Encoding.UTF8, true, 4096, leaveOpen: true);
            var all = reader.ReadToEnd();
            if (all.Length < consumedCharacters) throw new IOException("Fixture command file must be append-only.");
            var end = all.IndexOf('\n', consumedCharacters);
            if (end < 0) return; // Do not consume a partially written JSON line.
            var line = all[consumedCharacters..end].Trim();
            consumedCharacters = end + 1;
            if (line.Length == 0) return;
            long? commandSeq = null;
            try
            {
                using var parsed = JsonDocument.Parse(line);
                var command = parsed.RootElement;
                var seq = command.GetProperty("seq").GetInt64();
                commandSeq = seq;
                if (seq <= lastCommandSeq) { Log("command-duplicate", null, new { seq }); return; }
                var op = command.GetProperty("op").GetString();
                var id = command.TryGetProperty("id", out var name) ? name.GetString()! : "single";
                if (!controls.TryGetValue(id, out var box)) throw new ArgumentException("Unknown fixture control ID.");
                lastCommandError = null;
                if(op is "text" or "select")
                {
                    Native.GetWindowThreadProcessId(Native.GetForegroundWindow(),out var foregroundPid);
                    if(foregroundPid!=Environment.ProcessId)
                        throw new InvalidOperationException("安全拒絕fixture控制指令：前景PID不是本fixture PID。");
                }
                switch (op)
                {
                    case "focus":
                        (id == "decoy" ? decoy : this).Activate();
                        box.Focus();
                        break;
                    case "text": box.Text = command.GetProperty("text").GetString() ?? ""; break;
                    case "select":
                        var start = command.GetProperty("start").GetInt32();
                        var length = command.GetProperty("length").GetInt32();
                        if (start < 0 || length < 0 || start > box.TextLength || length > box.TextLength - start)
                            throw new ArgumentOutOfRangeException("Invalid fixture selection.");
                        box.Select(start, length);
                        break;
                    case "block":
                        var ms = command.GetProperty("ms").GetInt32();
                        if (ms is < 0 or > 10000) throw new ArgumentOutOfRangeException("block ms must be 0..10000.");
                        Log("block-enter", id, new { seq, ms });
                        lastCommandSeq = seq; // This command intentionally acknowledges entry before blocking.
                        Snapshot();
                        Thread.Sleep(ms);
                        Log("block-exit", id, new { seq, ms });
                        break;
                    case "snapshot": break;
                    case "overlay":
                        if (overlay is { IsDisposed: false }) overlay.Close();
                        var bounds = new Rectangle(command.GetProperty("left").GetInt32(),
                            command.GetProperty("top").GetInt32(), command.GetProperty("width").GetInt32(),
                            command.GetProperty("height").GetInt32());
                        if (bounds.Width is < 40 or > 1200 || bounds.Height is < 20 or > 500)
                            throw new ArgumentOutOfRangeException("Invalid overlay size.");
                        overlayClicks["pause"] = overlayClicks["stop"] = 0;
                        overlay = DesktopProbes.CreateOverlay(bounds, raw =>
                        {
                            var data = JsonSerializer.SerializeToElement(raw);
                            if (data.TryGetProperty("eventType", out var eventType) && eventType.GetString() == "overlay-click" &&
                                data.TryGetProperty("button", out var button) && button.GetString() is { } buttonId &&
                                overlayClicks.ContainsKey(buttonId))
                                overlayClicks[buttonId]++;
                            Log("overlay-event", null, raw);
                            Snapshot();
                        });
                        overlay.Show(this);
                        break;
                    case "close":
                        lastCommandSeq = seq;
                        Log("command-complete", id, new { seq, op }); Snapshot(); Close(); return;
                    default: throw new ArgumentException("Unknown fixture command.");
                }
                lastCommandSeq = seq;
                Log("command-complete", id, new { seq, op });
                Snapshot();
            }
            catch (Exception ex)
            {
                lastCommandError = new { seq = commandSeq, error = ex.Message };
                if (commandSeq is { } failedSeq) lastCommandSeq = Math.Max(lastCommandSeq, failedSeq);
                Log("command-error", null, lastCommandError);
                Snapshot();
            }
        }

        private void ValidateCommandPath()
        {
            if (!File.Exists(commands) || (File.GetAttributes(commands) & FileAttributes.ReparsePoint) != 0)
                throw new IOException("Fixture commands 路徑已消失或變成連結；拒絕讀取。");
            for (var dir = new DirectoryInfo(Path.GetDirectoryName(commands)!); dir != null; dir = dir.Parent)
                if ((dir.Attributes & FileAttributes.ReparsePoint) != 0)
                    throw new IOException("Fixture commands 資料夾已變成 reparse point；拒絕讀取。");
        }
    }

    private static string? MessageName(int msg) => msg switch
    {
        0x00B1 => "EM_SETSEL", 0x00C2 => "EM_REPLACESEL", 0x000C => "WM_SETTEXT",
        0x0102 => "WM_CHAR", 0x0100 => "WM_KEYDOWN", 0x0101 => "WM_KEYUP",
        0x0104 => "WM_SYSKEYDOWN", 0x0105 => "WM_SYSKEYUP", 0x00C7 => "EM_UNDO",
        _ => null
    };

    private sealed class LoggedTextBox : TextBox
    {
        public Action<string, object?>? Trace { get; set; }
        private bool checkingSelection;
        private (int Start, int Length) lastSelection = (-1, -1);
        protected override void WndProc(ref Message m)
        {
            var name = MessageName(m.Msg);
            if (name != null) Trace?.Invoke(name + "-before", new { message = m.Msg,
                wParam = m.WParam.ToInt64(), lParam = m.LParam.ToInt64() });
            base.WndProc(ref m);
            if (name != null) Trace?.Invoke(name + "-after", new { message = m.Msg, result = m.Result.ToInt64() });
            // TextBox has no public SelectionChanged event. Observe each completed
            // window message, with a guard because reading selection sends EM_GETSEL.
            if (Trace != null && !checkingSelection && IsHandleCreated && !IsDisposed && !Disposing)
            {
                checkingSelection = true;
                try
                {
                    var current = (SelectionStart, SelectionLength);
                    if (current != lastSelection)
                    {
                        lastSelection = current;
                        Trace("SelectionChanged-message", new { message = m.Msg });
                    }
                }
                finally { checkingSelection = false; }
            }
        }
    }

    private sealed class LoggedRichTextBox : RichTextBox
    {
        public Action<string, object?>? Trace { get; set; }
        protected override void WndProc(ref Message m)
        {
            var name = MessageName(m.Msg);
            if (name != null) Trace?.Invoke(name + "-before", new { message = m.Msg,
                wParam = m.WParam.ToInt64(), lParam = m.LParam.ToInt64() });
            base.WndProc(ref m);
            if (name != null) Trace?.Invoke(name + "-after", new { message = m.Msg, result = m.Result.ToInt64() });
        }
    }
}
