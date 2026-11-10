using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Windows.Automation;
using System.Windows.Automation.Text;

namespace ProbeLab;

internal static class UiaProbe
{
    // Names and text are never persisted for non-fixture UI. Password contents are never requested.
    public static object Inspect(nint hwnd, bool fixture = false)
    {
        try
        {
            var root = AutomationElement.FromHandle(hwnd);
            return Walk(root, fixture, 0, new Counter(), readText: fixture);
        }
        catch (Exception e) { return new { error = e.GetType().Name }; }
    }

    private sealed class Counter { public int Count; }
    private static object Walk(AutomationElement element, bool fixture, int depth, Counter budget, bool readText = true)
    {
        if (++budget.Count > 1000 || depth > 18) return new { truncated = true };
        var entry = Element(element, fixture, readText);
        var children = new List<object>();
        try
        {
            var walker = TreeWalker.RawViewWalker;
            for (var child = walker.GetFirstChild(element); child != null && budget.Count < 1000; child = walker.GetNextSibling(child))
                children.Add(Walk(child, fixture, depth + 1, budget, readText));
        }
        catch (Exception e) { children.Add(new { error = e.GetType().Name }); }
        return new { element = entry, children };
    }

    public static object Element(AutomationElement e, bool fixture, bool readText = true)
    {
        try
        {
            var c = e.Current;
            var patterns = e.GetSupportedPatterns().Select(p => p.ProgrammaticName).ToArray();
            bool? readOnly = null;
            object? value = null, text = null, selection = null;
            if (e.TryGetCurrentPattern(ValuePattern.Pattern, out var vp))
            {
                var v = (ValuePattern)vp;
                readOnly = v.Current.IsReadOnly;
                if (!c.IsPassword && readText)
                {
                    try { var s = v.Current.Value; value = new { length = s.Length, fixtureText = fixture ? s : null }; }
                    catch (Exception ex) { value = new { error = ex.GetType().Name }; }
                }
            }
            if (!c.IsPassword && readText && e.TryGetCurrentPattern(TextPattern.Pattern, out var tp))
            {
                var p = (TextPattern)tp;
                try
                {
                    var s = p.DocumentRange.GetText(-1);
                    text = new { fullTextReadable = true, length = s.Length, fixtureText = fixture ? s : null };
                    selection = p.GetSelection().Select(r =>
                    {
                        var v = r.GetText(-1);
                        var prefix = p.DocumentRange.Clone();
                        prefix.MoveEndpointByRange(TextPatternRangeEndpoint.End, r, TextPatternRangeEndpoint.Start);
                        var start = prefix.GetText(-1).Length;
                        return new { length = v.Length, startUtf16 = start, endUtf16 = start + v.Length };
                    }).ToArray();
                }
                catch (Exception ex) { text = new { fullTextReadable = false, error = ex.GetType().Name }; }
            }
            return new
            {
                hwnd = c.NativeWindowHandle, className = c.ClassName, controlType = c.ControlType.ProgrammaticName,
                automationId = c.AutomationId, patterns, isPassword = c.IsPassword,
                editable = readOnly.HasValue ? !readOnly.Value : (bool?)null, readOnly, textReadAttempted = readText && !c.IsPassword, value, text, selection
            };
        }
        catch (Exception ex) { return new { error = ex.GetType().Name }; }
    }

    // 瀏覽器網址列（Omnibox）探查。以 ControlType.Edit 且 ClassName 含 Omnibox／名稱含 Address／網址 辨識。
    private static AutomationElement? FindOmnibox(nint hwnd)
    {
        var root = AutomationElement.FromHandle(hwnd);
        var edits = root.FindAll(TreeScope.Descendants, new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Edit));
        foreach (AutomationElement e in edits)
        {
            var cls = e.Current.ClassName ?? ""; var name = e.Current.Name ?? "";
            if (cls.Contains("Omnibox", StringComparison.OrdinalIgnoreCase) || name.Contains("Address", StringComparison.OrdinalIgnoreCase)
                || name.Contains("網址") || name.Contains("搜尋列")) return e;
        }
        return null;
    }

    public static object OmniboxInfo(nint hwnd, OwnedTarget target, out int cx, out int cy)
    {
        cx = cy = 0;
        try
        {
            if (!target.Guard(hwnd, "UIA omnibox info")) return new { found = false, safetyDenied = true };
            var e = FindOmnibox(hwnd);
            if (e == null) return new { found = false };
            var rect = e.Current.BoundingRectangle;
            cx = (int)(rect.Left + Math.Min(rect.Width / 2, 200)); cy = (int)(rect.Top + rect.Height / 2);
            var patterns = e.GetSupportedPatterns().Select(p => p.ProgrammaticName).ToArray();
            return new { found = true, className = e.Current.ClassName, name = e.Current.Name, rect = new { rect.Left, rect.Top, rect.Width, rect.Height }, patterns, isPassword = e.Current.IsPassword };
        }
        catch (Exception ex) { return new { found = false, error = ex.GetType().Name, ex.Message }; }
    }

    public static object OmniboxRead(nint hwnd, OwnedTarget target)
    {
        try
        {
            if (!target.Guard(hwnd, "UIA omnibox read")) return new { ok = false, safetyDenied = true };
            var e = FindOmnibox(hwnd);
            if (e == null) return new { ok = false, reason = "not found" };
            string? value = null; string? selected = null;
            if (e.TryGetCurrentPattern(ValuePattern.Pattern, out var vp)) value = ((ValuePattern)vp).Current.Value;
            if (e.TryGetCurrentPattern(TextPattern.Pattern, out var tpo))
            {
                var ranges = ((TextPattern)tpo).GetSelection();
                selected = ranges.Length > 0 ? ranges[0].GetText(-1) : null;
            }
            return new { ok = true, value, selected };
        }
        catch (Exception ex) { return new { ok = false, error = ex.GetType().Name, ex.Message }; }
    }

    // start<0：選取整個文件；否則選取 [start, start+length)。
    public static object OmniboxSelect(nint hwnd, int start, int length, OwnedTarget target)
    {
        try
        {
            if (!target.Guard(hwnd, "UIA omnibox select")) return new { ok = false, safetyDenied = true };
            var e = FindOmnibox(hwnd);
            if (e == null) return new { ok = false, reason = "not found" };
            if (!e.TryGetCurrentPattern(TextPattern.Pattern, out var pattern)) return new { ok = false, reason = "no TextPattern" };
            var tp = (TextPattern)pattern;
            var range = tp.DocumentRange.Clone();
            if (start >= 0)
            {
                range.MoveEndpointByRange(TextPatternRangeEndpoint.End, range, TextPatternRangeEndpoint.Start);
                range.Move(TextUnit.Character, start);
                range.MoveEndpointByUnit(TextPatternRangeEndpoint.End, TextUnit.Character, length);
            }
            range.Select();
            return new { ok = true, selectedText = range.GetText(-1) };
        }
        catch (Exception ex) { return new { ok = false, error = ex.GetType().Name, ex.Message }; }
    }

    // 以 UIA TextPattern 把選取範圍設為 [start, start+length)（不使用 JS），回報實際結果。
    public static object SelectRange(nint hwnd, string id, int start, int length, OwnedTarget target)
    {
        try
        {
            if (!target.Guard(hwnd, "UIA select " + id)) return new { ok = false, safetyDenied = true };
            var root = AutomationElement.FromHandle(hwnd);
            var field = root.FindFirst(TreeScope.Descendants, new PropertyCondition(AutomationElement.NameProperty, "ProbeLab-" + id));
            if (field == null) return new { ok = false, reason = "field not found" };
            if (!field.TryGetCurrentPattern(TextPattern.Pattern, out var pattern)) return new { ok = false, reason = "no TextPattern" };
            var tp = (TextPattern)pattern;
            var range = tp.DocumentRange.Clone();
            range.MoveEndpointByRange(TextPatternRangeEndpoint.End, range, TextPatternRangeEndpoint.Start); // 收合到開頭
            var moved = range.Move(TextUnit.Character, start);
            var grown = range.MoveEndpointByUnit(TextPatternRangeEndpoint.End, TextUnit.Character, length);
            range.Select();
            return new { ok = true, moved, grown, selectedLength = range.GetText(-1).Length };
        }
        catch (Exception e) { return new { ok = false, error = e.GetType().Name, e.Message }; }
    }

    public static object BrowserField(nint hwnd, string id,OwnedTarget target)
    {
        try
        {
            if(!target.Guard(hwnd,"UIA fixture read "+id))return new{found=false,safetyDenied=true};
            var root = AutomationElement.FromHandle(hwnd);
            var field = root.FindFirst(TreeScope.Descendants, new PropertyCondition(AutomationElement.NameProperty, "ProbeLab-" + id));
            return field == null ? new { found = false } : new { found = true, data = Element(field, true) };
        }
        catch (Exception e) { return new { error = e.GetType().Name, e.Message }; }
    }

    private static List<AutomationElement> NotepadWindows()
    {
        var result = new List<AutomationElement>();
        var pids = Process.GetProcessesByName("notepad").Select(p => { using (p) return p.Id; }).ToHashSet();
        var roots = AutomationElement.RootElement.FindAll(TreeScope.Children, Condition.TrueCondition);
        foreach (AutomationElement root in roots)
        {
            try { if (pids.Contains(root.Current.ProcessId)) result.Add(root); } catch (ElementNotAvailableException) { }
        }
        return result;
    }

    public static void Notepad(Report r, string file)
    {
        // CreateNew is the proof that this command did not accept an existing user file.
        var full = Path.GetFullPath(file);
        var temp = Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        if (!full.StartsWith(temp, StringComparison.OrdinalIgnoreCase)) throw new ArgumentException("記事本檔案必須位於系統暫存資料夾內。");
        var directory = new DirectoryInfo(Path.GetDirectoryName(full)!);
        if (!directory.Exists) throw new ArgumentException("請先建立檔案所在的暫存資料夾；檔案本身必須尚不存在。");
        for (var p = directory; p != null; p = p.Parent)
            if ((p.Attributes & FileAttributes.ReparsePoint) != 0) throw new ArgumentException("不接受 reparse point 暫存路徑。");
        using (var created = new FileStream(full, FileMode.CreateNew, FileAccess.Write, FileShare.None)) { }
        r.Add("建立全新暫存空白檔", new { file = full, bytes = new FileInfo(full).Length }, true, "CreateNew 成功且檔案長度為 0");
        var before = NotepadWindows().Select(e => string.Join(",", e.GetRuntimeId())).ToHashSet();
        using var process = Launcher.Start(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "System32", "notepad.exe"), new[] { full });
        var deadline = Stopwatch.StartNew();
        List<AutomationElement> added = new();
        while (deadline.ElapsedMilliseconds < 10000)
        {
            added = NotepadWindows().Where(e => !before.Contains(string.Join(",", e.GetRuntimeId()))).ToList();
            if (added.Count != 0) break;
            Thread.Sleep(200);
        }
        r.Add("記事本唯讀辨識", new { launcherPid = process.Id, newWindowCount = added.Count, elapsedMs = deadline.ElapsedMilliseconds }, added.Count == 1 ? true : null,
            "只探查可辨識的新記事本視窗；若被既有視窗接收成分頁則不掃描既有視窗");
        if(added.Count==0)r.Add("記事本階層未執行",new{reason="未辨識新增頂層視窗；可能合併至既有分頁，故不掃描使用者視窗"},null,"辨識不明時只保留Pattern量測未執行狀態");
        foreach (var root in added)
        {
            // A newly appearing Notepad root could restore old user tabs. Its tree is metadata-only.
            r.Add("新記事本視窗 UIA 階層（不呼叫任何文字讀取）", Walk(root, false, 0, new Counter(), readText: false), null,
                "僅類別、ControlType、AutomationId、Pattern、可編輯性與密碼旗標；新視窗也可能含 session restore 分頁");
            var tabs = root.FindAll(TreeScope.Descendants, new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.TabItem));
            var edits = root.FindAll(TreeScope.Descendants, new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Edit));
            bool ownClassic = added.Count == 1 && !process.HasExited && root.Current.ProcessId == process.Id && tabs.Count == 0 && edits.Count == 1
                && root.Current.Name.Contains(Path.GetFileNameWithoutExtension(full), StringComparison.OrdinalIgnoreCase)
                && edits[0].Current.ClassName.Equals("Edit", StringComparison.OrdinalIgnoreCase)
                && Automation.Compare(edits[0], AutomationElement.FocusedElement);
            if (ownClassic)
                r.Add("獨立傳統記事本欄位的全文與選取長度", Element(edits[0], false), null,
                    "只在啟動PID相同、新視窗、檔名相符、沒有分頁且恰有一個傳統Edit時讀取；只保留長度");
            else
                r.Add("全文與選取長度未執行", new { length = (int?)null, selectionLength = (int?)null, tabs = tabs.Count, edits = edits.Count, reason = "無法證明 UIA 文件只屬於全新空白暫存檔，拒絕 GetText/GetValue。" }, null,
                    "辨識不明時不讀使用者文件；Pattern能力仍列於metadata階層");
        }
        r.Add("結束處理", new { closedWindows = 0, reason = "唯讀探查不送關閉訊息；新版記事本可能合併分頁，無法證明其後沒有使用者分頁，保留新視窗與暫存檔供人工關閉。", file = full }, true, "不得關閉任何使用者既有視窗或分頁");
    }
}
