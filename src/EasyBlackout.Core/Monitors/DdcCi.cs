using System.Runtime.InteropServices;
using EasyBlackout.Core.Logging;

namespace EasyBlackout.Core.Monitors;

/// <summary>
/// DDC/CI (VESA MCCS) power control through dxva2. VCP 0xD6 "Power mode": 1 = on, 4 = off (DPM), 5 = off (hard).
/// Every call can take tens to hundreds of milliseconds, so never call these on the UI thread.
/// </summary>
public static class DdcCi
{
    private const byte VcpPowerMode = 0xD6;
    public const uint PowerOn = 0x01;
    public const uint PowerOff = 0x04;

    public enum Support
    {
        Unknown,
        Supported,
        NotSupported,
    }

    /// <summary>Reads VCP 0xD6 to find out whether the monitor answers DDC/CI power commands.</summary>
    public static Support Probe(IntPtr hMonitor)
    {
        var result = Support.NotSupported;
        WithPhysicalMonitors(hMonitor, handle =>
        {
            if (GetVCPFeatureAndVCPFeatureReply(handle, VcpPowerMode, out _, out var current, out _) && current is >= 1 and <= 5)
                result = Support.Supported;
        });
        return result;
    }

    public static bool SetPower(IntPtr hMonitor, uint mode)
    {
        var ok = false;
        WithPhysicalMonitors(hMonitor, handle =>
        {
            // Some monitors ignore the first write while waking up; retry once.
            if (SetVCPFeature(handle, VcpPowerMode, mode) || SetVCPFeature(handle, VcpPowerMode, mode)) ok = true;
        });
        if (!ok) Log.Warn($"DDC/CI power {mode} failed for monitor {hMonitor}");
        return ok;
    }

    private static void WithPhysicalMonitors(IntPtr hMonitor, Action<IntPtr> action)
    {
        if (!GetNumberOfPhysicalMonitorsFromHMONITOR(hMonitor, out var count) || count == 0) return;
        var monitors = new PHYSICAL_MONITOR[count];
        if (!GetPhysicalMonitorsFromHMONITOR(hMonitor, count, monitors)) return;
        try
        {
            foreach (var m in monitors) action(m.hPhysicalMonitor);
        }
        finally
        {
            DestroyPhysicalMonitors(count, monitors);
        }
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct PHYSICAL_MONITOR
    {
        public IntPtr hPhysicalMonitor;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string szPhysicalMonitorDescription;
    }

    [DllImport("dxva2.dll", SetLastError = true)]
    private static extern bool GetNumberOfPhysicalMonitorsFromHMONITOR(IntPtr hMonitor, out uint count);

    [DllImport("dxva2.dll", SetLastError = true)]
    private static extern bool GetPhysicalMonitorsFromHMONITOR(IntPtr hMonitor, uint count, [Out] PHYSICAL_MONITOR[] monitors);

    [DllImport("dxva2.dll", SetLastError = true)]
    private static extern bool DestroyPhysicalMonitors(uint count, PHYSICAL_MONITOR[] monitors);

    [DllImport("dxva2.dll", SetLastError = true)]
    private static extern bool GetVCPFeatureAndVCPFeatureReply(IntPtr monitor, byte code, out uint type, out uint current, out uint max);

    [DllImport("dxva2.dll", SetLastError = true)]
    private static extern bool SetVCPFeature(IntPtr monitor, byte code, uint value);
}
