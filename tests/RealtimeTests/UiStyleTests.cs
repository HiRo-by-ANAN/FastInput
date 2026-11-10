using System.Reflection;
using FastInput.Windows.Realtime;

namespace FastInput.RealtimeTests;

internal static class UiStyleTests
{
    public static void Register(TestSuite suite)
    {
        suite.Add("ui.overlay_styles_no_window_shown", () =>
        {
            using var overlay = new RealtimeOverlay();
            Check.Equal(OverlayLayout.ExtendedStyles,
                overlay.ExtendedStyleForTest & OverlayLayout.ExtendedStyles, "overlay style flags missing");
            Check.True(!overlay.PauseButton.IsSelectableForTest && !overlay.StopButton.IsSelectableForTest,
                "overlay buttons can select focus");
            var message = System.Windows.Forms.Message.Create(0, OverlayLayout.MouseActivateMessage, 0, 0);
            object[] arguments = [message];
            typeof(RealtimeOverlay).GetMethod("WndProc", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(overlay, arguments);
            message = (System.Windows.Forms.Message)arguments[0];
            Check.Equal((nint)OverlayLayout.NoActivateResult, message.Result);
            Check.True(!overlay.Visible && !overlay.IsHandleCreated, "style unit test created a native window");
        });
        suite.Add("ui.overlay_work_area_geometry", () =>
        {
            var work = new LocalRect(-1920, -200, 1920, 1080);
            LocalRect[] targets = [new(-1800, 100, 500, 40), new(-100, 800, 80, 30), new(-2000, -300, 200, 20)];
            foreach (var target in targets)
            {
                var position = OverlayLayout.Place(target, work, 400, 160);
                Check.True(position.X >= work.X && position.Y >= work.Y
                    && position.X + position.Width <= work.X + work.Width
                    && position.Y + position.Height <= work.Y + work.Height, "overlay escaped working area");
            }
            var below = OverlayLayout.Place(targets[0], work, 400, 160);
            Check.True(below.Y >= targets[0].Y + targets[0].Height, "overlay overlaps ordinary target");
        });
        suite.Add("ui.hotkey_conflict_1409_fake_api", () =>
        {
            var api = new FakeHotKeyApi();
            using var hotkey = new EmergencyHotKey(api);
            Check.True(!hotkey.Register(0));
            Check.True(!hotkey.Available && hotkey.FailureReason.Contains("1409"));
            Check.Equal(5, api.Attempts.Count);
            Check.Equal(0x4003u, api.Attempts[0].Modifiers);
            Check.Equal(0x7Bu, api.Attempts[0].Key);
            api.Success = true;
            Check.True(hotkey.Register(0));
            hotkey.Dispose();
            Check.Equal(1, api.Unregistered);
        });
    }

    private sealed class FakeHotKeyApi : IHotKeyApi
    {
        public bool Success { get; set; }
        public List<(uint Modifiers, uint Key)> Attempts { get; } = [];
        public int Unregistered { get; private set; }
        public int LastError => 1409;
        public bool Register(nint window, int id, uint modifiers, uint key)
        {
            Attempts.Add((modifiers, key));
            return Success;
        }
        public void Unregister(nint window, int id) => Unregistered++;
    }
}
