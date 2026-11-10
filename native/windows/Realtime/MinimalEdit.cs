namespace FastInput.Windows.Realtime;

public sealed record MinimalEdit(int Start, int DeleteLength, string InsertText)
{
    public bool IsEmpty => DeleteLength == 0 && InsertText.Length == 0;
}

public sealed record EditCalculation(MinimalEdit? Edit, string? Reason)
{
    public bool Allowed => Edit is not null;
}

public static class MinimalEditCalculator
{
    public static EditCalculation Calculate(string previous, string next, string prefix = "", string suffix = "")
    {
        if (!TextSafety.IsAllowed(previous) || !TextSafety.IsAllowed(next)) return new(null, "invalid_state");
        if (HasSurrogate(prefix) || HasSurrogate(suffix)) return new(null, "unsupported");
        // UIA's non-BMP position mapping is unverified. Only appending preserves existing offsets.
        if ((HasSurrogate(previous) || HasSurrogate(next)) && !next.StartsWith(previous, StringComparison.Ordinal))
            return new(null, "unsupported");
        var start = 0;
        while (start < previous.Length && start < next.Length && previous[start] == next[start]) start++;
        var end = 0;
        while (end < previous.Length - start && end < next.Length - start
            && previous[previous.Length - end - 1] == next[next.Length - end - 1]) end++;
        var removed = previous.AsSpan(start, previous.Length - start - end);
        var inserted = next.AsSpan(start, next.Length - start - end);
        if (HasMultipleRegions(removed, inserted)) return new(null, "unsupported");
        return new(new(prefix.Length + start, removed.Length, inserted.ToString()), null);
    }

    public static bool HasSurrogate(string text) => text.Any(char.IsSurrogate);

    private static bool HasMultipleRegions(ReadOnlySpan<char> previous, ReadOnlySpan<char> next)
    {
        if (previous.IsEmpty || next.IsEmpty) return false;
        // Minimum unit-cost UTF-16 edit script. An unchanged interior run separates edit regions.
        var width = next.Length + 1;
        var distance = new ushort[(previous.Length + 1) * width];
        for (var i = 0; i <= previous.Length; i++) distance[i * width] = (ushort)i;
        for (var j = 0; j <= next.Length; j++) distance[j] = (ushort)j;
        for (var i = 1; i <= previous.Length; i++)
            for (var j = 1; j <= next.Length; j++)
                distance[i * width + j] = (ushort)Math.Min(distance[(i - 1) * width + j - 1] + (previous[i - 1] == next[j - 1] ? 0 : 1),
                    Math.Min(distance[(i - 1) * width + j] + 1, distance[i * width + j - 1] + 1));
        var x = previous.Length;
        var y = next.Length;
        var regions = 0;
        var editing = false;
        while (x > 0 || y > 0)
        {
            if (x > 0 && y > 0 && previous[x - 1] == next[y - 1]
                && distance[x * width + y] == distance[(x - 1) * width + y - 1])
            { x--; y--; editing = false; continue; }
            if (!editing) { regions++; editing = true; }
            if (regions > 1) return true;
            var current = distance[x * width + y];
            if (x > 0 && y > 0 && current == distance[(x - 1) * width + y - 1] + 1) { x--; y--; }
            else if (x > 0 && current == distance[(x - 1) * width + y] + 1) x--;
            else y--;
        }
        return false;
    }
}
