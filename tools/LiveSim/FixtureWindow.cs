using System.Text.Json;
namespace FastInput.LiveSim;
internal sealed class FixtureWindow : Form
{
    private readonly string _state;
    private readonly Dictionary<string, TextBoxBase> _boxes = new();
    private readonly List<object> _events = [];
    private int _enterEvents, _defaultButtonClicks;
    public FixtureWindow(string state)
    {
        _state = state; Text = "FastInput LiveSim fixture"; Size = new Size(760, 600); StartPosition = FormStartPosition.CenterScreen;
        var y = 20;
        foreach (var id in new[] { "single", "multi", "rich", "password", "decoy" })
        {
            Controls.Add(new Label { Text = id, Bounds = new Rectangle(20, y, 100, 22) });
            TextBoxBase box = id == "rich" ? new TracedRichBox() : new TracedBox { Multiline = id == "multi", PasswordChar = id == "password" ? '*' : '\0' };
            box.Name = id; box.Bounds = new Rectangle(130, y, 570, id is "multi" or "rich" ? 80 : 30);
            if (id == "decoy") box.Text = "DECOY_UNCHANGED";
            _boxes.Add(id, box); Controls.Add(box);
            if (box is TracedBox b) b.Trace = (m, w, l) => Trace(id, m, w, l);
            if (box is TracedRichBox r) r.Trace = (m, w, l) => Trace(id, m, w, l);
            box.TextChanged += (_, _) => { _events.Add(new { kind = "text", id, text = box.Text, ticks = System.Diagnostics.Stopwatch.GetTimestamp() }); Save(); };
            y += box.Height + 30;
        }
        var submit = new Button { Text = "Default action (Enter fixture)", Bounds = new Rectangle(130, y, 300, 32) };
        submit.Click += (_, _) => { _defaultButtonClicks++; _events.Add(new { kind = "default_button", ticks = System.Diagnostics.Stopwatch.GetTimestamp() }); Save(); };
        Controls.Add(submit); AcceptButton = submit;
        Shown += (_, _) => { Save(); _ = Task.Run(ReadCommandsAsync); };
    }
    protected override bool ProcessDialogKey(Keys keyData)
    {
        if (keyData == Keys.Return)
        {
            _enterEvents++;
            _events.Add(new { kind = "enter", ticks = System.Diagnostics.Stopwatch.GetTimestamp() });
            Save();
        }
        return base.ProcessDialogKey(keyData);
    }
    private async Task ReadCommandsAsync()
    {
        while (await Console.In.ReadLineAsync() is { } line)
        {
            if (line == "close") { BeginInvoke(new Action(Close)); break; }
        }
    }
    private void Trace(string id, int m, nint w, nint l)
    {
        if (m == 0xC2) { _events.Add(new { kind = "replace", id, ticks = System.Diagnostics.Stopwatch.GetTimestamp() }); }
    }
    private void Save()
    {
        if (!IsHandleCreated) return;
        Program.AtomicJson(_state, new { pid = Environment.ProcessId, window = (long)Handle, enter_events = _enterEvents, default_button_clicks = _defaultButtonClicks, fields = _boxes.ToDictionary(p => p.Key, p => new { hwnd = (long)p.Value.Handle, text = p.Value.Text, rect = p.Value.RectangleToScreen(p.Value.ClientRectangle) }), events = _events });
    }
    private sealed class TracedBox : TextBox
    {
        public Action<int, nint, nint>? Trace;
        protected override void WndProc(ref Message m) { Trace?.Invoke(m.Msg, m.WParam, m.LParam); base.WndProc(ref m); }
    }
    private sealed class TracedRichBox : RichTextBox
    {
        public Action<int, nint, nint>? Trace;
        protected override void WndProc(ref Message m) { Trace?.Invoke(m.Msg, m.WParam, m.LParam); base.WndProc(ref m); }
    }
}
