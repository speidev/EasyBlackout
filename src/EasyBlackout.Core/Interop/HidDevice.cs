using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace EasyBlackout.Core.Interop;

/// <summary>A HID top-level collection as seen by Windows (one per interface / collection).</summary>
public sealed record HidInterfaceInfo(
    string Path,
    ushort Vid,
    ushort Pid,
    int? InterfaceNumber,
    ushort UsagePage,
    ushort Usage,
    int FeatureReportLength);

/// <summary>Thin wrapper over hid.dll: enumerate HID collections and send feature reports.</summary>
public sealed class HidDevice : IDisposable
{
    private readonly SafeFileHandle _handle;

    private HidDevice(SafeFileHandle handle, HidInterfaceInfo info)
    {
        _handle = handle;
        Info = info;
    }

    public HidInterfaceInfo Info { get; }

    public static HidDevice? Open(HidInterfaceInfo info)
    {
        // Read/write access is needed for feature reports; share everything so the vendor app keeps working.
        var handle = CreateFileW(info.Path, GENERIC_READ | GENERIC_WRITE, FILE_SHARE_READ | FILE_SHARE_WRITE,
            IntPtr.Zero, OPEN_EXISTING, 0, IntPtr.Zero);
        if (handle.IsInvalid)
        {
            handle.Dispose();
            return null;
        }
        return new HidDevice(handle, info);
    }

    public bool SetFeature(byte[] report) => HidD_SetFeature(_handle, report, report.Length);

    public void Dispose() => _handle.Dispose();

    /// <summary>Lists present HID collections for the given vendor ids (capabilities read without exclusive access).</summary>
    public static List<HidInterfaceInfo> Enumerate(IReadOnlySet<ushort> vendorIds)
    {
        var result = new List<HidInterfaceInfo>();
        HidD_GetHidGuid(out var hidGuid);
        var set = SetupDiGetClassDevsW(ref hidGuid, IntPtr.Zero, IntPtr.Zero, DIGCF_PRESENT | DIGCF_DEVICEINTERFACE);
        if (set == INVALID_HANDLE_VALUE) return result;
        try
        {
            var iface = new SP_DEVICE_INTERFACE_DATA { cbSize = Marshal.SizeOf<SP_DEVICE_INTERFACE_DATA>() };
            for (uint i = 0; SetupDiEnumDeviceInterfaces(set, IntPtr.Zero, ref hidGuid, i, ref iface); i++)
            {
                var path = GetInterfacePath(set, ref iface);
                if (path is null || !TryParseIds(path, out var vid, out var pid, out var mi) || !vendorIds.Contains(vid)) continue;
                var caps = ReadCaps(path);
                if (caps is null) continue;
                result.Add(new HidInterfaceInfo(path, vid, pid, mi, caps.Value.UsagePage, caps.Value.Usage, caps.Value.FeatureReportByteLength));
            }
        }
        finally
        {
            SetupDiDestroyDeviceInfoList(set);
        }
        return result;
    }

    /// <summary>Parses "...vid_03f0&amp;pid_0f8f&amp;mi_03..." from a device interface path.</summary>
    public static bool TryParseIds(string path, out ushort vid, out ushort pid, out int? interfaceNumber)
    {
        vid = pid = 0;
        interfaceNumber = null;
        var lower = path.ToLowerInvariant();
        var v = lower.IndexOf("vid_", StringComparison.Ordinal);
        var p = lower.IndexOf("pid_", StringComparison.Ordinal);
        if (v < 0 || p < 0 || v + 8 > lower.Length || p + 8 > lower.Length) return false;
        if (!ushort.TryParse(lower.AsSpan(v + 4, 4), System.Globalization.NumberStyles.HexNumber, null, out vid)) return false;
        if (!ushort.TryParse(lower.AsSpan(p + 4, 4), System.Globalization.NumberStyles.HexNumber, null, out pid)) return false;
        var m = lower.IndexOf("&mi_", StringComparison.Ordinal);
        if (m >= 0 && m + 6 <= lower.Length &&
            int.TryParse(lower.AsSpan(m + 4, 2), System.Globalization.NumberStyles.HexNumber, null, out var mi))
            interfaceNumber = mi;
        return true;
    }

    private static HIDP_CAPS? ReadCaps(string path)
    {
        // Access 0 is enough to query capabilities, even for collections Windows holds open (mouse, keyboard).
        using var handle = CreateFileW(path, 0, FILE_SHARE_READ | FILE_SHARE_WRITE, IntPtr.Zero, OPEN_EXISTING, 0, IntPtr.Zero);
        if (handle.IsInvalid || !HidD_GetPreparsedData(handle, out var preparsed)) return null;
        try
        {
            return HidP_GetCaps(preparsed, out var caps) == HIDP_STATUS_SUCCESS ? caps : null;
        }
        finally
        {
            HidD_FreePreparsedData(preparsed);
        }
    }

    private static string? GetInterfacePath(IntPtr set, ref SP_DEVICE_INTERFACE_DATA iface)
    {
        SetupDiGetDeviceInterfaceDetailW(set, ref iface, IntPtr.Zero, 0, out var required, IntPtr.Zero);
        if (required == 0) return null;
        var buffer = Marshal.AllocHGlobal((int)required);
        try
        {
            // cbSize of SP_DEVICE_INTERFACE_DETAIL_DATA_W is 8 on x64 (DWORD + WCHAR[1], padded).
            Marshal.WriteInt32(buffer, IntPtr.Size == 8 ? 8 : 6);
            return SetupDiGetDeviceInterfaceDetailW(set, ref iface, buffer, required, out _, IntPtr.Zero)
                ? Marshal.PtrToStringUni(buffer + 4)
                : null;
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    #region Interop

    private const uint GENERIC_READ = 0x80000000;
    private const uint GENERIC_WRITE = 0x40000000;
    private const uint FILE_SHARE_READ = 1;
    private const uint FILE_SHARE_WRITE = 2;
    private const uint OPEN_EXISTING = 3;
    private const uint DIGCF_PRESENT = 0x02;
    private const uint DIGCF_DEVICEINTERFACE = 0x10;
    private const int HIDP_STATUS_SUCCESS = 0x00110000;
    private static readonly IntPtr INVALID_HANDLE_VALUE = new(-1);

    [StructLayout(LayoutKind.Sequential)]
    private struct SP_DEVICE_INTERFACE_DATA
    {
        public int cbSize;
        public Guid InterfaceClassGuid;
        public int Flags;
        public IntPtr Reserved;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct HIDP_CAPS
    {
        public ushort Usage;
        public ushort UsagePage;
        public ushort InputReportByteLength;
        public ushort OutputReportByteLength;
        public ushort FeatureReportByteLength;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 17)] public ushort[] Reserved;
        public ushort NumberLinkCollectionNodes;
        public ushort NumberInputButtonCaps;
        public ushort NumberInputValueCaps;
        public ushort NumberInputDataIndices;
        public ushort NumberOutputButtonCaps;
        public ushort NumberOutputValueCaps;
        public ushort NumberOutputDataIndices;
        public ushort NumberFeatureButtonCaps;
        public ushort NumberFeatureValueCaps;
        public ushort NumberFeatureDataIndices;
    }

    [DllImport("hid.dll")]
    private static extern void HidD_GetHidGuid(out Guid guid);

    [DllImport("hid.dll", SetLastError = true)]
    private static extern bool HidD_GetPreparsedData(SafeFileHandle device, out IntPtr preparsed);

    [DllImport("hid.dll")]
    private static extern bool HidD_FreePreparsedData(IntPtr preparsed);

    [DllImport("hid.dll")]
    private static extern int HidP_GetCaps(IntPtr preparsed, out HIDP_CAPS caps);

    [DllImport("hid.dll", SetLastError = true)]
    private static extern bool HidD_SetFeature(SafeFileHandle device, byte[] report, int length);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFileW(string name, uint access, uint share, IntPtr security,
        uint creation, uint flags, IntPtr template);

    [DllImport("setupapi.dll", SetLastError = true)]
    private static extern IntPtr SetupDiGetClassDevsW(ref Guid classGuid, IntPtr enumerator, IntPtr hwndParent, uint flags);

    [DllImport("setupapi.dll", SetLastError = true)]
    private static extern bool SetupDiEnumDeviceInterfaces(IntPtr set, IntPtr devInfo, ref Guid classGuid, uint index,
        ref SP_DEVICE_INTERFACE_DATA data);

    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool SetupDiGetDeviceInterfaceDetailW(IntPtr set, ref SP_DEVICE_INTERFACE_DATA data,
        IntPtr detail, uint detailSize, out uint requiredSize, IntPtr devInfo);

    [DllImport("setupapi.dll", SetLastError = true)]
    private static extern bool SetupDiDestroyDeviceInfoList(IntPtr set);

    #endregion
}
