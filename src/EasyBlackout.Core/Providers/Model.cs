namespace EasyBlackout.Core.Providers;

public enum ProviderKind
{
    Monitors,
    Lighting,
}

/// <summary>Health of a provider's connection to its underlying API / vendor software.</summary>
public enum ProviderState
{
    Checking,
    Ready,
    /// <summary>The vendor software is installed but something must be switched on by the user.</summary>
    NeedsSetup,
    /// <summary>The vendor software / service isn't running (or isn't installed).</summary>
    NotRunning,
    /// <summary>The API isn't available on this system at all.</summary>
    Unavailable,
    Error,
}

public enum DeviceCategory
{
    Monitor,
    Keyboard,
    Mouse,
    Mousepad,
    Headset,
    HeadsetStand,
    Speaker,
    Controller,
    Keypad,
    Fan,
    LedStrip,
    Cooler,
    Memory,
    Motherboard,
    GraphicsCard,
    Case,
    Other,
}

/// <summary>A single physical device a provider can (or would like to) black out.</summary>
/// <param name="Key">Stable, provider-qualified identifier used for persistence (e.g. "corsair:ABC123").</param>
/// <param name="Controllable">False when the device was detected but no API can currently reach it.</param>
public sealed record DeviceInfo(
    string Key,
    string Name,
    DeviceCategory Category,
    string? Detail = null,
    bool Controllable = true,
    bool SupportsSelection = true);

/// <summary>What the UI shows for a device row.</summary>
public enum DeviceStatus
{
    Ready,
    BlackedOut,
    Excluded,
    NeedsSetup,
    NotRunning,
    NotControllable,
    Unavailable,
    Error,
    Checking,
}

/// <summary>Per-blackout context handed to providers.</summary>
public sealed class BlackoutContext
{
    private readonly IReadOnlySet<string> _excluded;

    public BlackoutContext(IReadOnlySet<string> excludedDevices, IReadOnlySet<string> ddcPowerOffMonitors)
    {
        _excluded = excludedDevices;
        DdcPowerOffMonitors = ddcPowerOffMonitors;
    }

    public IReadOnlySet<string> DdcPowerOffMonitors { get; }

    public bool IsIncluded(string deviceKey) => !_excluded.Contains(deviceKey);

    public static BlackoutContext Everything { get; } =
        new(new HashSet<string>(), new HashSet<string>());
}
