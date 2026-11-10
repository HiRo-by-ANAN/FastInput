using System.Text.Json;

namespace FastInput.Windows;

internal static class WindowPlacement
{
    public static string DataDirectory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "FastInput");
    private static string FileName => Path.Combine(DataDirectory, "window.json");
    private sealed record Placement(int X, int Y, int Width, int Height, bool Maximized);

    public static void Restore(Form form)
    {
        try
        {
            var saved = JsonSerializer.Deserialize<Placement>(File.ReadAllText(FileName));
            if (saved is null || saved.Width < form.MinimumSize.Width || saved.Height < form.MinimumSize.Height
                || saved.Width > 32_768 || saved.Height > 32_768
                || Math.Abs((long)saved.X) > 1_000_000 || Math.Abs((long)saved.Y) > 1_000_000) return;
            var bounds = new Rectangle(saved.X, saved.Y, saved.Width, saved.Height);
            if (!Screen.AllScreens.Any(screen =>
            {
                var visible = Rectangle.Intersect(screen.WorkingArea, bounds);
                return visible.Width >= Math.Min(400, bounds.Width) && visible.Height >= Math.Min(200, bounds.Height)
                    && saved.Y >= screen.WorkingArea.Top && (long)saved.Y + 40 <= screen.WorkingArea.Bottom;
            })) return;
            form.StartPosition = FormStartPosition.Manual;
            var screen = Screen.FromRectangle(bounds).WorkingArea;
            bounds.Width = Math.Min(bounds.Width, screen.Width);
            bounds.Height = Math.Min(bounds.Height, screen.Height);
            bounds.X = Math.Clamp(bounds.X, screen.Left, Math.Max(screen.Left, screen.Right - bounds.Width));
            bounds.Y = Math.Clamp(bounds.Y, screen.Top, Math.Max(screen.Top, screen.Bottom - bounds.Height));
            form.Bounds = bounds;
            if (saved.Maximized) form.WindowState = FormWindowState.Maximized;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException or ArgumentException) { }
    }

    public static void Save(Form form, bool maximized)
    {
        try
        {
            var bounds = form.WindowState == FormWindowState.Normal ? form.Bounds : form.RestoreBounds;
            Directory.CreateDirectory(DataDirectory);
            File.WriteAllText(FileName + ".tmp", JsonSerializer.Serialize(
                new Placement(bounds.X, bounds.Y, bounds.Width, bounds.Height, maximized)));
            File.Move(FileName + ".tmp", FileName, overwrite: true);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
    }
}
