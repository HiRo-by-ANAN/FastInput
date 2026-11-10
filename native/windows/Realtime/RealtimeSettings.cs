using System.Text.Json;
using System.Text.Json.Serialization;

namespace FastInput.Windows.Realtime;

public sealed class RealtimeSettings
{
    [JsonPropertyName("realtime_enabled")] public bool RealtimeEnabled { get; set; }
    [JsonPropertyName("virtual_enter_enabled")] public bool VirtualEnterEnabled { get; set; }
    private string _language = Loc.DetectLanguage(System.Globalization.CultureInfo.CurrentUICulture.Name);
    [JsonPropertyName("language")]
    public string Language
    {
        get => _language;
        set => _language = Loc.IsSupported(value) ? value : Loc.DetectLanguage(System.Globalization.CultureInfo.CurrentUICulture.Name);
    }
    public static string DefaultPath => Environment.GetEnvironmentVariable("FASTINPUT_REALTIME_SETTINGS_PATH")
        ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "FastInput", "realtime-settings.json");

    public static RealtimeSettings Load(string? path = null)
    {
        try { return JsonSerializer.Deserialize<RealtimeSettings>(File.ReadAllText(path ?? DefaultPath)) ?? new(); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException) { return new(); }
    }

    public void Save(string? path = null)
    {
        var destination = Path.GetFullPath(path ?? DefaultPath);
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        var temporary = destination + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllText(temporary, JsonSerializer.Serialize(this));
            File.Move(temporary, destination, true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
}
