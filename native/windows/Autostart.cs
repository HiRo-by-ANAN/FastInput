using Microsoft.Win32;

namespace FastInput.Windows;

internal static class Autostart
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private static string ValueName => Environment.GetEnvironmentVariable("FASTINPUT_AUTOSTART_VALUE_NAME") is { Length: > 0 } name
        ? name : "FastInput";
    private static string Command => $"\"{Environment.ProcessPath ?? Application.ExecutablePath}\" --minimized";

    public static bool IsEnabled()
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKey);
        return string.Equals(key?.GetValue(ValueName) as string, Command, StringComparison.OrdinalIgnoreCase);
    }

    public static void SetEnabled(bool enabled)
    {
        using var key = Registry.CurrentUser.CreateSubKey(RunKey, writable: true);
        if (enabled) key.SetValue(ValueName, Command, RegistryValueKind.String);
        else key.DeleteValue(ValueName, throwOnMissingValue: false);
    }
}
