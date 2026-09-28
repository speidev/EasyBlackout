using System.Runtime.InteropServices;
using System.Text;

namespace EasyBlackout.Core.Providers.Corsair;

/// <summary>P/Invoke surface of the Corsair iCUE SDK v4 (iCUESDK.x64_2019.dll, redistributable).</summary>
internal static unsafe class CorsairNative
{
    public const string DllName = "iCUESDK.x64_2019.dll";
    public const int StringSizeM = 128;
    public const int DeviceCountMax = 64;
    public const int DeviceLedCountMax = 512;

    public enum Error
    {
        Success = 0,
        NotConnected = 1,
        NoControl = 2,
        IncompatibleProtocol = 3,
        InvalidArguments = 4,
        InvalidOperation = 5,
        DeviceNotFound = 6,
        NotAllowed = 7,
    }

    public enum SessionState
    {
        Invalid = 0,
        Closed = 1,
        Connecting = 2,
        Timeout = 3,
        ConnectionRefused = 4,
        ConnectionLost = 5,
        Connected = 6,
    }

    [Flags]
    public enum DeviceType : uint
    {
        Unknown = 0x0000,
        Keyboard = 0x0001,
        Mouse = 0x0002,
        Mousemat = 0x0004,
        Headset = 0x0008,
        HeadsetStand = 0x0010,
        FanLedController = 0x0020,
        LedController = 0x0040,
        MemoryModule = 0x0080,
        Cooler = 0x0100,
        Motherboard = 0x0200,
        GraphicsCard = 0x0400,
        Touchbar = 0x0800,
        GameController = 0x1000,
        All = 0xFFFFFFFF,
    }

    public enum AccessLevel
    {
        Shared = 0,
        ExclusiveLightingControl = 1,
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct Version { public int Major, Minor, Patch; }

    [StructLayout(LayoutKind.Sequential)]
    public struct SessionDetails
    {
        public Version ClientVersion;
        public Version ServerVersion;
        public Version ServerHostVersion;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct SessionStateChanged
    {
        public SessionState State;
        public SessionDetails Details;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct DeviceInfoNative
    {
        public DeviceType Type;
        public fixed byte Id[StringSizeM];
        public fixed byte Serial[StringSizeM];
        public fixed byte Model[StringSizeM];
        public int LedCount;
        public int ChannelCount;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct DeviceFilter { public DeviceType DeviceTypeMask; }

    [StructLayout(LayoutKind.Sequential)]
    public struct LedPosition
    {
        public uint Id;
        public double Cx;
        public double Cy;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct LedColor
    {
        public uint Id;
        public byte R, G, B, A;
    }

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    public delegate void SessionStateChangedHandler(IntPtr context, SessionStateChanged* eventData);

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    public static extern Error CorsairConnect(SessionStateChangedHandler onStateChanged, IntPtr context);

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    public static extern Error CorsairDisconnect();

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    public static extern Error CorsairGetSessionDetails(out SessionDetails details);

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    public static extern Error CorsairGetDevices(in DeviceFilter filter, int sizeMax, [Out] DeviceInfoNative[] devices, out int size);

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    public static extern Error CorsairGetLedPositions(byte* deviceId, int sizeMax, [Out] LedPosition[] positions, out int size);

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    public static extern Error CorsairSetLedColors(byte* deviceId, int size, [In] LedColor[] colors);

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    public static extern Error CorsairRequestControl(byte* deviceId, AccessLevel accessLevel);

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    public static extern Error CorsairReleaseControl(byte* deviceId);

    public static string ReadString(byte* buffer, int max)
    {
        var length = 0;
        while (length < max && buffer[length] != 0) length++;
        return Encoding.UTF8.GetString(buffer, length);
    }

    /// <summary>Null-terminated UTF-8 copy of a device id suitable for pinning.</summary>
    public static byte[] ToNative(string id)
    {
        var bytes = new byte[StringSizeM];
        Encoding.UTF8.GetBytes(id, 0, Math.Min(id.Length, StringSizeM - 1), bytes, 0);
        return bytes;
    }
}
