using System.Globalization;
using System.Text;
using System.Text.Json;

namespace ProbeLab.Model;

public record Edit(int Start, int DeleteLength, string Insert, bool Ambiguous);
public record TextSelection(int Start, int Length);
public static class TextModel
{
    public static int[] Graphemes(string s) => StringInfo.ParseCombiningCharacters(s).Append(s.Length).ToArray();
    public static object Describe(string s) => new { text = s, utf16Length = s.Length, graphemeBoundaries = Graphemes(s), scalars = Scalars(s) };
    public static object[] Scalars(string s)
    {
        var rows = new List<object>(); int offset = 0;
        foreach (var r in s.EnumerateRunes()) { rows.Add(new { offset, scalar = $"U+{r.Value:X}", utf16Length = r.Utf16SequenceLength }); offset += r.Utf16SequenceLength; }
        return rows.ToArray();
    }
    // Maximal common grapheme prefix/suffix. A caret hint selects among equally minimal edits.
    public static Edit Diff(string before, string after, int? oldCaret = null)
        => ComputeDiff(before,after,oldCaret,null);
    public static Edit Diff(string before, string after, TextSelection oldSelection)
    {
        if(oldSelection.Start<0 || oldSelection.Length<0 || oldSelection.Start>before.Length-oldSelection.Length)throw new ArgumentOutOfRangeException(nameof(oldSelection));
        return ComputeDiff(before,after,oldSelection.Start+oldSelection.Length,oldSelection);
    }
    static Edit ComputeDiff(string before, string after, int? oldCaret, TextSelection? oldSelection)
    {
        if(before==after)return new Edit(before.Length,0,"",false);
        var a = Graphemes(before); var b = Graphemes(after);
        int ac = a.Length - 1, bc = b.Length - 1, prefix = 0;
        bool Eq(int i, int j) => before.AsSpan(a[i], a[i+1]-a[i]).SequenceEqual(after.AsSpan(b[j], b[j+1]-b[j]));
        while (prefix < Math.Min(ac, bc) && Eq(prefix, prefix)) prefix++;
        var candidates = new List<(int Cost, Edit Edit)>();
        for (int p = 0; p <= prefix; p++)
        {
            int suffix = 0;
            while (suffix < ac-p && suffix < bc-p && Eq(ac-1-suffix, bc-1-suffix)) suffix++;
            int delete = a[ac-suffix] - a[p];
            string insert = after[b[p]..b[bc-suffix]];
            candidates.Add((delete+insert.Length, new Edit(a[p], delete, insert, false)));
        }
        int best = candidates.Min(x => x.Cost);
        var tied = candidates.Where(x => x.Cost == best).Select(x => x.Edit).Distinct().ToArray();
        var chosen = oldSelection is {Length:>0}
            ? tied.OrderBy(x=>Math.Abs(x.Start-oldSelection.Start)+Math.Abs(x.Start+x.DeleteLength-oldSelection.Start-oldSelection.Length)).ThenByDescending(x=>x.Start).First()
            : oldCaret.HasValue ? tied.OrderBy(x => Math.Abs(x.Start+x.DeleteLength-oldCaret.Value)).ThenByDescending(x => x.Start).First() : tied.OrderByDescending(x => x.Start).First();
        return chosen with { Ambiguous = tied.Length > 1 };
    }
    public static IEnumerable<JsonElement> ParseLines(string text)
    {
        using var reader = new StringReader(text); string? line;
        while ((line = reader.ReadLine()) != null)
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            using var doc = JsonDocument.Parse(line);
            yield return doc.RootElement.Clone();
        }
    }
}
