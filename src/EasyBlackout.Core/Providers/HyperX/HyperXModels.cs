namespace EasyBlackout.Core.Providers.HyperX;

/// <summary>How to paint one HyperX model directly over HID (only while blacked out).</summary>
public sealed record HyperXModel(
    string Name,
    DeviceCategory Category,
    IReadOnlyList<(ushort Vid, ushort Pid)> Ids,
    int InterfaceNumber,
    ushort UsagePage,
    int FeatureReportLength,
    /// <summary>
    /// The device drops back to its own onboard lighting when direct frames stop for this long, so frames are
    /// re-sent faster than this. That is also what makes restore (and a crash) safe: stop sending and it reverts.
    /// </summary>
    TimeSpan DirectModeTimeout,
    Func<byte, byte, byte, byte[][]> BuildFrame);

public static class HyperXModels
{
    public const ushort KingstonVid = 0x0951;
    public const ushort HpVid = 0x03F0;

    /// <summary>
    /// Pulsefire Haste (wired). Direct mode: a setup feature report followed by a color feature report on the
    /// vendor collection (interface 3, usage page 0xFF90), both 65 bytes with report id 0.
    /// </summary>
    public static readonly HyperXModel PulsefireHaste = new(
        "HyperX Pulsefire Haste",
        DeviceCategory.Mouse,
        [(KingstonVid, 0x1727), (HpVid, 0x0F8F)],
        InterfaceNumber: 3,
        UsagePage: 0xFF90,
        FeatureReportLength: 65,
        DirectModeTimeout: TimeSpan.FromMilliseconds(50),
        BuildFrame: (r, g, b) => [HasteSetupPacket(), HasteColorPacket(r, g, b)]);

    public static readonly IReadOnlyList<HyperXModel> All = [PulsefireHaste];

    public static HyperXModel? Find(ushort vid, ushort pid) =>
        All.FirstOrDefault(m => m.Ids.Contains((vid, pid)));

    internal static byte[] HasteSetupPacket()
    {
        var buf = new byte[65];
        buf[1] = 0x04; // packet id: direct-mode setup
        buf[2] = 0xF2;
        buf[8] = 0x02;
        return buf;
    }

    internal static byte[] HasteColorPacket(byte r, byte g, byte b)
    {
        var buf = new byte[65];
        buf[1] = 0x81; // packet id: direct color
        buf[2] = r;
        buf[3] = g;
        buf[4] = b;
        buf[8] = 0x02;
        return buf;
    }
}
