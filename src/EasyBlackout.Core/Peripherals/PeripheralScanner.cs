using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using EasyBlackout.Core.Logging;
using EasyBlackout.Core.Providers;

namespace EasyBlackout.Core.Peripherals;

/// <summary>A present USB device from a known RGB vendor.</summary>
public sealed record UsbPeripheral(ushort Vid, ushort Pid, string Name, DeviceCategory Category, string InstanceId);

public sealed class PeripheralSnapshot
{
    public static readonly PeripheralSnapshot Empty = new([]);

    public PeripheralSnapshot(IReadOnlyList<UsbPeripheral> devices) => Devices = devices;

    public IReadOnlyList<UsbPeripheral> Devices { get; }

    public IEnumerable<UsbPeripheral> ForVendor(ushort vid) => Devices.Where(d => d.Vid == vid);
}

public static class KnownVendors
{
    public const ushort Corsair = 0x1B1C;
    public const ushort Razer = 0x1532;
    public const ushort Logitech = 0x046D;
    public const ushort SteelSeries = 0x1038;
    /// <summary>HyperX ships under Kingston's id (older gear) and HP's id (since HP bought HyperX).</summary>
    public const ushort HyperXKingston = 0x0951;
    public const ushort HyperXHp = 0x03F0;

    public static readonly IReadOnlySet<ushort> All = new HashSet<ushort>
        { Corsair, Razer, Logitech, SteelSeries, HyperXKingston, HyperXHp };

    /// <summary>Kingston and HP ids also cover flash drives, printers and webcams; only HyperX-branded devices count.</summary>
    public static bool IsHyperX(ushort vid, string name) =>
        vid is HyperXKingston or HyperXHp && name.Contains("HyperX", StringComparison.OrdinalIgnoreCase);

    /// <summary>Whether a USB device from a known vendor is worth listing.</summary>
    public static bool IsRelevant(ushort vid, string name) =>
        vid is HyperXKingston or HyperXHp ? IsHyperX(vid, name) : All.Contains(vid);
}

/// <summary>
/// Enumerates present USB devices (SetupAPI) from vendors whose lighting SDKs can't list hardware themselves,
/// so the dashboard can show real device names.
/// </summary>
public static partial class PeripheralScanner
{
    // Devices from RGB vendors that have no lighting (webcams, receivers for non-RGB gear, audio interfaces…).
    [GeneratedRegex(@"webcam|camera|brio|streamcam|c9\d\d|c270|c310|c615|c920|c922|litra|blue yeti|unifying|bolt receiver|presenter|spotlight|dfu|bootloader|updater", RegexOptions.IgnoreCase)]
    private static partial Regex NonLightingNames();

    [GeneratedRegex(@"^USB\\VID_([0-9A-F]{4})&PID_([0-9A-F]{4})\\", RegexOptions.IgnoreCase)]
    private static partial Regex UsbInstanceId();

    public static PeripheralSnapshot Scan()
    {
        try
        {
            return new PeripheralSnapshot(EnumerateUsb().ToList());
        }
        catch (Exception ex)
        {
            Log.Error("USB peripheral scan failed", ex);
            return PeripheralSnapshot.Empty;
        }
    }

    public static DeviceCategory GuessCategory(string name)
    {
        var n = name.ToLowerInvariant();
        if (n.Contains("keypad") || n.Contains("tartarus")) return DeviceCategory.Keypad;
        if (n.Contains("keyboard")) return DeviceCategory.Keyboard;
        if (n.Contains("mouse mat") || n.Contains("mousemat") || n.Contains("mouse pad") || n.Contains("mousepad") ||
            n.Contains("firefly") || n.Contains("goliathus") || n.Contains("powerplay") || n.Contains("qck")) return DeviceCategory.Mousepad;
        if (n.Contains("headset stand") || n.Contains("base station")) return DeviceCategory.HeadsetStand;
        if (n.Contains("headset") || n.Contains("cloud") || n.Contains("kraken") || n.Contains("arctis") || n.Contains("barracuda") || n.Contains("blackshark")) return DeviceCategory.Headset;
        if (n.Contains("mouse") || n.Contains("pulsefire") || n.Contains("deathadder") || n.Contains("viper") || n.Contains("basilisk") ||
            n.Contains("naga") || n.Contains("rival") || n.Contains("aerox") || n.Contains("prime") || n.Contains("g pro")) return DeviceCategory.Mouse;
        if (n.Contains("speaker") || n.Contains("nommo") || n.Contains("leviathan")) return DeviceCategory.Speaker;
        if (n.Contains("controller") || n.Contains("gamepad") || n.Contains("wolverine")) return DeviceCategory.Controller;
        if (n.Contains("blackwidow") || n.Contains("huntsman") || n.Contains("ornata") || n.Contains("cynosa") ||
            n.Contains("apex") || n.Contains("alloy") || n.Contains("g915") || n.Contains("g815") || n.Contains("g513") || n.Contains("g213")) return DeviceCategory.Keyboard;
        if (n.Contains("chroma") || n.Contains("strip") || n.Contains("light")) return DeviceCategory.LedStrip;
        return DeviceCategory.Other;
    }

    private static IEnumerable<UsbPeripheral> EnumerateUsb()
    {
        var set = SetupDiGetClassDevsW(IntPtr.Zero, "USB", IntPtr.Zero, DIGCF_PRESENT | DIGCF_ALLCLASSES);
        if (set == INVALID_HANDLE_VALUE) yield break;
        try
        {
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var data = new SP_DEVINFO_DATA { cbSize = (uint)Marshal.SizeOf<SP_DEVINFO_DATA>() };
            for (uint i = 0; SetupDiEnumDeviceInfo(set, i, ref data); i++)
            {
                var instanceId = GetInstanceId(set, ref data);
                if (instanceId is null || instanceId.Contains("&MI_", StringComparison.OrdinalIgnoreCase)) continue;

                var match = UsbInstanceId().Match(instanceId);
                if (!match.Success) continue;
                var vid = Convert.ToUInt16(match.Groups[1].Value, 16);
                var pid = Convert.ToUInt16(match.Groups[2].Value, 16);
                if (!KnownVendors.All.Contains(vid)) continue;

                var name = GetBusReportedName(set, ref data)
                           ?? GetRegistryString(set, ref data, SPDRP_FRIENDLYNAME)
                           ?? GetRegistryString(set, ref data, SPDRP_DEVICEDESC)
                           ?? $"USB device {vid:X4}:{pid:X4}";
                name = name.Trim();
                if (!KnownVendors.IsRelevant(vid, name) || NonLightingNames().IsMatch(name)) continue;
                if (IsGenericName(name)) name = $"{VendorName(vid)} device ({pid:X4})";
                if (!seen.Add($"{vid:X4}:{pid:X4}:{name}")) continue;

                yield return new UsbPeripheral(vid, pid, name, GuessCategory(name), instanceId);
            }
        }
        finally
        {
            SetupDiDestroyDeviceInfoList(set);
        }
    }

    private static bool IsGenericName(string name) =>
        name.Equals("USB Composite Device", StringComparison.OrdinalIgnoreCase) ||
        name.Equals("USB Input Device", StringComparison.OrdinalIgnoreCase) ||
        name.StartsWith("USB device ", StringComparison.OrdinalIgnoreCase);

    public static string VendorName(ushort vid) => vid switch
    {
        KnownVendors.Corsair => "Corsair",
        KnownVendors.Razer => "Razer",
        KnownVendors.Logitech => "Logitech",
        KnownVendors.SteelSeries => "SteelSeries",
        KnownVendors.HyperXKingston or KnownVendors.HyperXHp => "HyperX",
        _ => $"Vendor {vid:X4}",
    };

    private static string? GetInstanceId(IntPtr set, ref SP_DEVINFO_DATA data)
    {
        var buffer = new char[512];
        return SetupDiGetDeviceInstanceIdW(set, ref data, buffer, buffer.Length, out var required)
            ? new string(buffer, 0, Math.Max(0, (int)required - 1))
            : null;
    }

    private static string? GetBusReportedName(IntPtr set, ref SP_DEVINFO_DATA data)
    {
        var key = new DEVPROPKEY { fmtid = new Guid("540b947e-8b40-45bc-a8a2-6a0b894cbda2"), pid = 4 };
        var buffer = new byte[512];
        if (!SetupDiGetDevicePropertyW(set, ref data, ref key, out var type, buffer, (uint)buffer.Length, out var size, 0) ||
            type != DEVPROP_TYPE_STRING || size < 2)
            return null;
        var s = System.Text.Encoding.Unicode.GetString(buffer, 0, (int)size).TrimEnd('\0');
        return string.IsNullOrWhiteSpace(s) ? null : s;
    }

    private static string? GetRegistryString(IntPtr set, ref SP_DEVINFO_DATA data, uint property)
    {
        var buffer = new byte[512];
        if (!SetupDiGetDeviceRegistryPropertyW(set, ref data, property, out _, buffer, (uint)buffer.Length, out var size) || size < 2)
            return null;
        var s = System.Text.Encoding.Unicode.GetString(buffer, 0, (int)size).TrimEnd('\0');
        return string.IsNullOrWhiteSpace(s) ? null : s;
    }

    private const uint DIGCF_PRESENT = 0x02;
    private const uint DIGCF_ALLCLASSES = 0x04;
    private const uint SPDRP_DEVICEDESC = 0x00;
    private const uint SPDRP_FRIENDLYNAME = 0x0C;
    private const uint DEVPROP_TYPE_STRING = 0x12;
    private static readonly IntPtr INVALID_HANDLE_VALUE = new(-1);

    [StructLayout(LayoutKind.Sequential)]
    private struct SP_DEVINFO_DATA
    {
        public uint cbSize;
        public Guid ClassGuid;
        public uint DevInst;
        public IntPtr Reserved;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DEVPROPKEY
    {
        public Guid fmtid;
        public uint pid;
    }

    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr SetupDiGetClassDevsW(IntPtr classGuid, string enumerator, IntPtr hwndParent, uint flags);

    [DllImport("setupapi.dll", SetLastError = true)]
    private static extern bool SetupDiEnumDeviceInfo(IntPtr set, uint index, ref SP_DEVINFO_DATA data);

    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool SetupDiGetDeviceInstanceIdW(IntPtr set, ref SP_DEVINFO_DATA data, char[] buffer, int size, out uint required);

    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool SetupDiGetDevicePropertyW(IntPtr set, ref SP_DEVINFO_DATA data, ref DEVPROPKEY key,
        out uint propertyType, byte[] buffer, uint bufferSize, out uint requiredSize, uint flags);

    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool SetupDiGetDeviceRegistryPropertyW(IntPtr set, ref SP_DEVINFO_DATA data, uint property,
        out uint regType, byte[] buffer, uint bufferSize, out uint requiredSize);

    [DllImport("setupapi.dll", SetLastError = true)]
    private static extern bool SetupDiDestroyDeviceInfoList(IntPtr set);
}
