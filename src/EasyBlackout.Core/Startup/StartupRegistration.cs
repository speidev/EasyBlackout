using Microsoft.Win32;

namespace EasyBlackout.Core.Startup;

/// <summary>Manages the per-user "Start with Windows" Run key entry.</summary>
public static class StartupRegistration
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "EasyBlackout";
    public const string TrayArgument = "--tray";

    public static bool IsEnabled()
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKey);
        return key?.GetValue(ValueName) is string value && !string.IsNullOrWhiteSpace(value);
    }

    public static void SetEnabled(bool enabled, string executablePath)
    {
        using var key = Registry.CurrentUser.CreateSubKey(RunKey, writable: true);
        if (enabled) key.SetValue(ValueName, $"\"{executablePath}\" {TrayArgument}");
        else key.DeleteValue(ValueName, throwOnMissingValue: false);
    }

    /// <summary>Keeps an existing entry pointing at the current exe (e.g. after the app was moved or updated).</summary>
    public static void RepairPath(string executablePath)
    {
        if (IsEnabled()) SetEnabled(true, executablePath);
    }
}
