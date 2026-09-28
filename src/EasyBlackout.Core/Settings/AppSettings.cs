namespace EasyBlackout.Core.Settings;

public sealed class AppSettings
{
    public string Hotkey { get; set; } = Hotkeys.HotkeyBinding.Default.ToString();

    /// <summary>Provider ids the user switched off entirely.</summary>
    public HashSet<string> DisabledProviders { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Device keys the user excluded from blackouts.</summary>
    public HashSet<string> ExcludedDevices { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Monitor keys for which DDC/CI hardware power-off is enabled (opt-in).</summary>
    public HashSet<string> DdcPowerOffMonitors { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    public bool ShowNotifications { get; set; } = true;

    public bool HasShownTrayHint { get; set; }

    public AppSettings Clone() => new()
    {
        Hotkey = Hotkey,
        DisabledProviders = new(DisabledProviders ?? [], StringComparer.OrdinalIgnoreCase),
        ExcludedDevices = new(ExcludedDevices ?? [], StringComparer.OrdinalIgnoreCase),
        DdcPowerOffMonitors = new(DdcPowerOffMonitors ?? [], StringComparer.OrdinalIgnoreCase),
        ShowNotifications = ShowNotifications,
        HasShownTrayHint = HasShownTrayHint,
    };
}
