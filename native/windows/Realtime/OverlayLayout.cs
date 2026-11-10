namespace FastInput.Windows.Realtime;

internal static class OverlayLayout
{
    public const int ExtendedStyles = 0x08000000 | 0x00000008 | 0x00000080;
    public const int MouseActivateMessage = 0x21;
    public const int NoActivateResult = 3;

    public static LocalRect? PlaceOutside(LocalRect target, LocalRect work, int width, int height)
    {
        if (width > work.Width || height > work.Height || width <= 0 || height <= 0) return null;
        int x = Math.Clamp(target.X, work.X, work.X + work.Width - width);
        int y = Math.Clamp(target.Y, work.Y, work.Y + work.Height - height);
        LocalRect[] candidates = [new(x, target.Y + target.Height + 6, width, height),
            new(x, target.Y - height - 6, width, height), new(target.X + target.Width + 6, y, width, height),
            new(target.X - width - 6, y, width, height)];
        return candidates.FirstOrDefault(r => r.X >= work.X && r.Y >= work.Y && r.X + r.Width <= work.X + work.Width
            && r.Y + r.Height <= work.Y + work.Height && (r.X + r.Width <= target.X || r.X >= target.X + target.Width
                || r.Y + r.Height <= target.Y || r.Y >= target.Y + target.Height));
    }

    // Coordinates come exclusively from the local picker. Work-area coordinates may be negative.
    public static LocalRect Place(LocalRect target, LocalRect work, int width, int height)
    {
        width = Math.Clamp(width, 1, Math.Max(1, work.Width));
        height = Math.Clamp(height, 1, Math.Max(1, work.Height));
        var x = Math.Clamp(target.X, work.X, work.X + Math.Max(0, work.Width - width));
        var below = target.Y + target.Height + 6;
        var y = below + height <= work.Y + work.Height ? below : target.Y - height - 6;
        y = Math.Clamp(y, work.Y, work.Y + Math.Max(0, work.Height - height));
        return new(x, y, width, height);
    }
}
