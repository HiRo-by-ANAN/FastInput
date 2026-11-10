using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using FastInput.Windows.Realtime;

namespace FastInput.RealtimeTests;

internal static class I18nTests
{
    public static void Register(TestSuite suite)
    {
        suite.Add("i18n.dictionary_keys_and_values", () =>
        {
            var keys = Loc.Dictionaries["zh-Hant"].Keys.Order().ToArray();
            foreach (var dictionary in Loc.Dictionaries.Values)
            {
                Check.True(keys.SequenceEqual(dictionary.Keys.Order()), "dictionary keys differ");
                Check.True(dictionary.Values.All(value => !string.IsNullOrWhiteSpace(value)), "empty translation");
            }
        });
        suite.Add("i18n.source_keys_and_no_cjk_literals", () =>
        {
            var directory = new DirectoryInfo(AppContext.BaseDirectory);
            while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Cargo.toml"))) directory = directory.Parent;
            Check.True(directory is not null, "workspace missing");
            foreach (var file in Directory.EnumerateFiles(Path.Combine(directory!.FullName, "native", "windows"), "*.cs", SearchOption.AllDirectories))
            {
                if (Path.GetFileName(file) == "Loc.cs" || file.Contains(Path.DirectorySeparatorChar + "obj" + Path.DirectorySeparatorChar)) continue;
                var source = File.ReadAllText(file);
                foreach (Match match in Regex.Matches(source, "Loc\\.T\\(\"([^\"]+)\""))
                {
                    var key = match.Groups[1].Value;
                    if (key is "state." or "reason.") continue;
                    Check.True(Loc.Dictionaries["zh-Hant"].ContainsKey(key), "translation key missing " + key);
                }
                // Match string literals before comments so URLs and escaped quotes are preserved.
                var tokens = Regex.Matches(source, "@?\"(?:\\\\.|\"\"|[^\"\\\\])*\"|//[^\\r\\n]*|/\\*[\\s\\S]*?\\*/");
                foreach (Match token in tokens)
                    if (!token.Value.StartsWith("//", StringComparison.Ordinal) && !token.Value.StartsWith("/*", StringComparison.Ordinal))
                        Check.True(!Regex.IsMatch(token.Value, @"[\u3040-\u30ff\u3400-\u9fff]"), "hardcoded CJK literal in " + Path.GetFileName(file));
            }
        });
        suite.Add("i18n.placeholders_match", () =>
        {
            static string Parameters(string value) => string.Join(",", Regex.Matches(value, @"\{(\w+)\}")
                .Select(match => match.Groups[1].Value).Order());
            foreach (var key in Loc.Dictionaries["zh-Hant"].Keys)
                foreach (var dictionary in Loc.Dictionaries.Values)
                    Check.Equal(Parameters(Loc.Dictionaries["zh-Hant"][key]), Parameters(dictionary[key]), key);
        });
        suite.Add("i18n.language_detection", () =>
        {
            foreach (var (culture, expected) in new[] { ("ja", "ja"), ("ja-JP", "ja"), ("JA-jp", "ja"),
                ("zh-TW", "zh-Hant"), ("zh-CN", "zh-Hant"), ("zh", "zh-Hant"), ("en", "en"),
                ("en-US", "en"), ("fr-FR", "zh-Hant"), ("", "zh-Hant") })
                Check.Equal(expected, Loc.DetectLanguage(culture));
            Check.Equal("zh-Hant", Loc.DetectLanguage(null));
        });
        suite.Add("i18n.formatting_and_language_change", () =>
        {
            var previous = Loc.Language;
            var changes = 0;
            void Changed() => changes++;
            Loc.LanguageChanged += Changed;
            try
            {
                Loc.SetLanguage("zh-Hant");
                changes = 0;
                Check.True(Loc.SetLanguage("en"));
                Check.Equal("Approve", Loc.T("realtimeui.approve"));
                Check.Equal("en", Loc.Language);
                Check.True(Loc.T("realtimeui.seconds.left.emergency.stop", 1234, "Ctrl+Alt+F12").Contains("1,234"));
                Check.True(Loc.T("mainform.the.web.rendering.process.failed", DayOfWeek.Monday).Contains("Monday"));
                Loc.SetLanguage("en");
                Check.True(!Loc.SetLanguage("fr"));
                Check.Equal(1, changes);
                Loc.SetLanguage("ja");
                Check.Equal("承認", Loc.T("realtimeui.approve"));
                Check.Equal("承認", Loc.Localize("Approve"));
                Check.Equal("unknown.key", Loc.T("unknown.key"));
                foreach (var state in Enum.GetNames<RealtimeState>()) Check.True(Loc.Dictionaries[Loc.Language].ContainsKey("state." + state));
            }
            finally { Loc.LanguageChanged -= Changed; Loc.SetLanguage(previous); }
        });
        suite.Add("i18n.settings_roundtrip_and_legacy", () =>
        {
            var directory = Path.Combine(Path.GetTempPath(), "fastinput-i18n-" + Guid.NewGuid().ToString("N"));
            var file = Path.Combine(directory, "realtime-settings.json");
            var previousCulture = CultureInfo.CurrentUICulture;
            try
            {
                CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo("ja-JP");
                Check.Equal("ja", RealtimeSettings.Load(file).Language);
                Directory.CreateDirectory(directory);
                File.WriteAllText(file, "{\"realtime_enabled\":true,\"virtual_enter_enabled\":true}");
                var settings = RealtimeSettings.Load(file);
                Check.Equal("ja", settings.Language);
                foreach (var language in Loc.Languages)
                {
                    settings.Language = language;
                    settings.Save(file);
                    var loaded = RealtimeSettings.Load(file);
                    Check.Equal(language, loaded.Language);
                    Check.True(loaded.RealtimeEnabled && loaded.VirtualEnterEnabled);
                }
                File.WriteAllText(file, "{\"language\":\"fr\"}");
                Check.Equal("ja", RealtimeSettings.Load(file).Language);
                File.WriteAllText(file, "{\"language\":null}");
                Check.Equal("ja", RealtimeSettings.Load(file).Language);
                File.WriteAllText(file, "invalid JSON");
                Check.Equal("ja", RealtimeSettings.Load(file).Language);
                Check.Equal(1, Directory.GetFiles(directory).Length);
            }
            finally
            {
                CultureInfo.CurrentUICulture = previousCulture;
                if (Directory.Exists(directory)) Directory.Delete(directory, true);
            }
        });
    }
}
