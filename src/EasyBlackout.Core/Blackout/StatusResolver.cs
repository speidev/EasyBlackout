using EasyBlackout.Core.Providers;

namespace EasyBlackout.Core.Blackout;

/// <summary>Single source of truth for the status shown next to each device.</summary>
public static class StatusResolver
{
    public static DeviceStatus Resolve(IBlackoutProvider provider, DeviceInfo device, bool providerEnabled, bool deviceIncluded)
    {
        if (!providerEnabled || (device.SupportsSelection && !deviceIncluded)) return DeviceStatus.Excluded;

        switch (provider.State)
        {
            case ProviderState.Checking: return DeviceStatus.Checking;
            case ProviderState.NeedsSetup: return DeviceStatus.NeedsSetup;
            case ProviderState.NotRunning: return device.Controllable ? DeviceStatus.NotRunning : DeviceStatus.NotControllable;
            case ProviderState.Unavailable: return DeviceStatus.Unavailable;
            case ProviderState.Error: return DeviceStatus.Error;
        }

        if (!device.Controllable) return DeviceStatus.NotControllable;
        if (provider.LastError is not null) return DeviceStatus.Error;
        return provider.IsBlackedOut ? DeviceStatus.BlackedOut : DeviceStatus.Ready;
    }

    public static string Describe(DeviceStatus status) => status switch
    {
        DeviceStatus.Ready => "Ready",
        DeviceStatus.BlackedOut => "Blacked out",
        DeviceStatus.Excluded => "Excluded",
        DeviceStatus.NeedsSetup => "Needs setup",
        DeviceStatus.NotRunning => "Not running",
        DeviceStatus.NotControllable => "Not controllable",
        DeviceStatus.Unavailable => "Unavailable",
        DeviceStatus.Error => "Error",
        DeviceStatus.Checking => "Checking…",
        _ => status.ToString(),
    };

    public static string Describe(ProviderState state) => state switch
    {
        ProviderState.Checking => "Checking…",
        ProviderState.Ready => "Connected",
        ProviderState.NeedsSetup => "Needs setup",
        ProviderState.NotRunning => "Not running",
        ProviderState.Unavailable => "Unavailable",
        ProviderState.Error => "Error",
        _ => state.ToString(),
    };
}
