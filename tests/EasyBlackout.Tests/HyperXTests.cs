using EasyBlackout.Core.Interop;
using EasyBlackout.Core.Peripherals;
using EasyBlackout.Core.Providers;
using EasyBlackout.Core.Providers.HyperX;

namespace EasyBlackout.Tests;

public class HyperXTests
{
    [Fact]
    public void HasteFrameIsSetupThenColor()
    {
        var frame = HyperXModels.PulsefireHaste.BuildFrame(0x11, 0x22, 0x33);
        Assert.Equal(2, frame.Length);

        var setup = frame[0];
        Assert.Equal(65, setup.Length);
        Assert.Equal(0x00, setup[0]); // report id
        Assert.Equal(0x04, setup[1]);
        Assert.Equal(0xF2, setup[2]);
        Assert.Equal(0x02, setup[8]);

        var color = frame[1];
        Assert.Equal(65, color.Length);
        Assert.Equal(0x00, color[0]);
        Assert.Equal(0x81, color[1]);
        Assert.Equal(new byte[] { 0x11, 0x22, 0x33 }, color[2..5]);
        Assert.Equal(0x02, color[8]);
        Assert.All(color[9..], b => Assert.Equal(0, b));
    }

    [Fact]
    public void BlackFrameIsAllZeroColor()
    {
        var color = HyperXModels.PulsefireHaste.BuildFrame(0, 0, 0)[1];
        Assert.Equal(new byte[] { 0, 0, 0 }, color[2..5]);
    }

    [Theory]
    [InlineData(0x03F0, 0x0F8F, true)]  // HP-era Haste
    [InlineData(0x0951, 0x1727, true)]  // Kingston-era Haste
    [InlineData(0x03F0, 0x0490, false)] // Pulsefire Surge: not native (yet)
    public void FindsSupportedModels(int vid, int pid, bool supported) =>
        Assert.Equal(supported, HyperXModels.Find((ushort)vid, (ushort)pid) is not null);

    [Fact]
    public void ParsesHidInterfacePaths()
    {
        const string path = @"\\?\hid#vid_03f0&pid_0f8f&mi_03#a&2bca6927&0&0000#{4d1e55b2-f16f-11cf-88cb-001111000030}";
        Assert.True(HidDevice.TryParseIds(path, out var vid, out var pid, out var mi));
        Assert.Equal(0x03F0, vid);
        Assert.Equal(0x0F8F, pid);
        Assert.Equal(3, mi);

        Assert.True(HidDevice.TryParseIds(@"\\?\hid#vid_0951&pid_1727#7&1&0&0000#{guid}", out _, out _, out var none));
        Assert.Null(none);
        Assert.False(HidDevice.TryParseIds(@"\\?\hid#something_else", out _, out _, out _));
    }

    [Theory]
    [InlineData(0x03F0, "HyperX Pulsefire Haste", true)]
    [InlineData(0x0951, "HyperX Alloy Origins", true)]
    [InlineData(0x03F0, "HP LaserJet Pro M404", false)]     // HP printer
    [InlineData(0x0951, "DataTraveler 3.0", false)]         // Kingston flash drive
    [InlineData(0x1532, "Razer DeathAdder V2", true)]       // other vendors unaffected
    public void OnlyHyperXBrandedDevicesFromSharedVendorIds(int vid, string name, bool relevant) =>
        Assert.Equal(relevant, KnownVendors.IsRelevant((ushort)vid, name));

    [Theory]
    [InlineData("HyperX Pulsefire Haste", DeviceCategory.Mouse)]
    [InlineData("HyperX Alloy Origins Core", DeviceCategory.Keyboard)]
    [InlineData("HyperX Cloud Alpha Wireless", DeviceCategory.Headset)]
    public void CategorisesHyperXNames(string name, DeviceCategory category) =>
        Assert.Equal(category, PeripheralScanner.GuessCategory(name));
}
