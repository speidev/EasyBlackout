using EasyBlackout.Core.Blackout;
using EasyBlackout.Core.Peripherals;
using EasyBlackout.Core.Providers;

namespace EasyBlackout.Tests;

public class StatusResolverTests
{
    private sealed class StubProvider : BlackoutProviderBase
    {
        public override string Id => "stub";
        public override string DisplayName => "Stub";
        public void Set(ProviderState state, bool blackedOut = false, string? error = null)
        {
            SetState(state);
            IsBlackedOut = blackedOut;
            if (error is not null) ReportError(error);
        }
        protected override Task RefreshCoreAsync(PeripheralSnapshot p, CancellationToken ct) => Task.CompletedTask;
        protected override Task BlackoutCoreAsync(BlackoutContext c, CancellationToken ct) => Task.CompletedTask;
        protected override Task RestoreCoreAsync(CancellationToken ct) => Task.CompletedTask;
    }

    private static readonly DeviceInfo Device = new("stub:1", "Keyboard", DeviceCategory.Keyboard);
    private static readonly DeviceInfo Unreachable = Device with { Controllable = false };

    [Theory]
    [InlineData(ProviderState.Ready, false, DeviceStatus.Ready)]
    [InlineData(ProviderState.Ready, true, DeviceStatus.BlackedOut)]
    [InlineData(ProviderState.NeedsSetup, false, DeviceStatus.NeedsSetup)]
    [InlineData(ProviderState.NotRunning, false, DeviceStatus.NotRunning)]
    [InlineData(ProviderState.Unavailable, false, DeviceStatus.Unavailable)]
    [InlineData(ProviderState.Checking, false, DeviceStatus.Checking)]
    public void FollowsProviderState(ProviderState state, bool blackedOut, DeviceStatus expected)
    {
        var p = new StubProvider();
        p.Set(state, blackedOut);
        Assert.Equal(expected, StatusResolver.Resolve(p, Device, providerEnabled: true, deviceIncluded: true));
    }

    [Fact]
    public void ExclusionWins()
    {
        var p = new StubProvider();
        p.Set(ProviderState.Ready);
        Assert.Equal(DeviceStatus.Excluded, StatusResolver.Resolve(p, Device, providerEnabled: false, deviceIncluded: true));
        Assert.Equal(DeviceStatus.Excluded, StatusResolver.Resolve(p, Device, providerEnabled: true, deviceIncluded: false));
    }

    [Fact]
    public void UnselectableDevicesIgnorePerDeviceExclusion()
    {
        var p = new StubProvider();
        p.Set(ProviderState.Ready);
        var d = Device with { SupportsSelection = false };
        Assert.Equal(DeviceStatus.Ready, StatusResolver.Resolve(p, d, providerEnabled: true, deviceIncluded: false));
    }

    [Fact]
    public void UncontrollableDevicesAreNeverReportedAsDark()
    {
        var p = new StubProvider();
        p.Set(ProviderState.Ready, blackedOut: true);
        Assert.Equal(DeviceStatus.NotControllable, StatusResolver.Resolve(p, Unreachable, true, true));
        p.Set(ProviderState.NotRunning);
        Assert.Equal(DeviceStatus.NotControllable, StatusResolver.Resolve(p, Unreachable, true, true));
    }

    [Fact]
    public void LastErrorShowsAsError()
    {
        var p = new StubProvider();
        p.Set(ProviderState.Ready, blackedOut: true, error: "boom");
        Assert.Equal(DeviceStatus.Error, StatusResolver.Resolve(p, Device, true, true));
    }

    [Theory]
    [InlineData("Razer BlackWidow V3", DeviceCategory.Keyboard)]
    [InlineData("Razer DeathAdder V2", DeviceCategory.Mouse)]
    [InlineData("Razer Firefly V2", DeviceCategory.Mousepad)]
    [InlineData("SteelSeries Arctis 7", DeviceCategory.Headset)]
    [InlineData("Razer Tartarus Pro", DeviceCategory.Keypad)]
    [InlineData("G502 HERO Gaming Mouse", DeviceCategory.Mouse)]
    [InlineData("Something else", DeviceCategory.Other)]
    public void GuessesCategoriesFromNames(string name, DeviceCategory expected) =>
        Assert.Equal(expected, PeripheralScanner.GuessCategory(name));
}
