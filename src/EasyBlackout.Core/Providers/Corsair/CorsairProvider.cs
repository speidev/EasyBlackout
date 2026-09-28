using System.Diagnostics;
using System.Runtime.InteropServices;
using EasyBlackout.Core.Logging;
using EasyBlackout.Core.Peripherals;
using static EasyBlackout.Core.Providers.Corsair.CorsairNative;

namespace EasyBlackout.Core.Providers.Corsair;

/// <summary>
/// Corsair iCUE via SDK v4. Idle: connected in shared mode without ever setting a color (zero effect on lighting).
/// Blackout: exclusive lighting control + all LEDs black. Restore: LEDs made transparent, control released,
/// so iCUE's own profile shows again.
/// </summary>
public sealed class CorsairProvider : BlackoutProviderBase
{
    private static SessionStateChangedHandler? _handler; // must outlive the native session
    private static volatile SessionState _session = SessionState.Closed;
    private static SessionDetails _details;

    private readonly Dictionary<string, uint[]> _controlled = new();
    private bool? _libraryAvailable;
    private bool _connectCalled;

    public override string Id => "corsair";
    public override string DisplayName => "Corsair iCUE";

    private static bool IsIcueRunning() => Process.GetProcessesByName("iCUE").Length > 0;

    protected override async Task RefreshCoreAsync(PeripheralSnapshot peripherals, CancellationToken cancellationToken)
    {
        _libraryAvailable ??= NativeLibrary.TryLoad(Path.Combine(AppContext.BaseDirectory, DllName), out _);
        var scanned = peripherals.ForVendor(KnownVendors.Corsair).ToList();
        if (_libraryAvailable != true)
        {
            SetState(ProviderState.Unavailable, "The iCUE SDK library is missing. Reinstall EasyBlackout.");
            SetDevices(DevicesFromScan(scanned, Id, controllable: false, "Not controllable"));
            return;
        }

        if (!_connectCalled)
        {
            _handler = CreateHandler();
            var err = CorsairConnect(_handler, IntPtr.Zero);
            _connectCalled = err == Error.Success;
            if (!_connectCalled) Log.Warn($"[corsair] CorsairConnect returned {err}");
        }

        // The SDK connects asynchronously; give it a moment on first contact.
        for (var i = 0; i < 20 && _session is SessionState.Closed or SessionState.Connecting; i++)
            await Task.Delay(100, cancellationToken).ConfigureAwait(false);

        if (_session != SessionState.Connected)
        {
            var controllableLater = false;
            if (!IsIcueRunning())
                SetState(ProviderState.NotRunning, "iCUE isn't running. Start iCUE to control Corsair devices.");
            else
                SetState(ProviderState.NeedsSetup,
                    "iCUE is running but refused the SDK connection. In iCUE, open Settings and turn on \"Enable SDK\" (third-party integrations).");
            SetDevices(DevicesFromScan(scanned, Id, controllableLater, "Waiting for iCUE"));
            return;
        }

        var devices = GetDevices()
            .Select(d => new DeviceInfo(
                KeyFor(d),
                string.IsNullOrWhiteSpace(d.Model) ? "Corsair device" : d.Model,
                MapCategory(d.Type),
                d.LedCount == 1 ? "1 LED" : $"{d.LedCount} LEDs"))
            .ToList();
        SetDevices(devices);
        var v = _details.ServerHostVersion;
        SetState(ProviderState.Ready, v.Major > 0 ? $"iCUE {v.Major}.{v.Minor}.{v.Patch}" : "Connected");
    }

    protected override unsafe Task BlackoutCoreAsync(BlackoutContext context, CancellationToken cancellationToken)
    {
        if (_session != SessionState.Connected)
        {
            if (IsIcueRunning()) throw new InvalidOperationException("iCUE SDK is not connected");
            return Task.CompletedTask; // iCUE not running: nothing to black out
        }

        var failures = new List<string>();
        foreach (var device in GetDevices())
        {
            if (!context.IsIncluded(KeyFor(device)) || device.LedCount == 0) continue;
            var id = ToNative(device.Id);
            fixed (byte* pId = id)
            {
                var err = CorsairRequestControl(pId, AccessLevel.ExclusiveLightingControl);
                if (err != Error.Success)
                {
                    failures.Add($"{device.Model}: {err}");
                    continue;
                }

                var positions = new LedPosition[DeviceLedCountMax];
                err = CorsairGetLedPositions(pId, positions.Length, positions, out var count);
                var luids = err == Error.Success ? positions.Take(count).Select(p => p.Id).ToArray() : [];
                lock (_controlled) _controlled[device.Id] = luids;

                if (luids.Length > 0)
                {
                    err = CorsairSetLedColors(pId, luids.Length, luids.Select(l => new LedColor { Id = l, A = 255 }).ToArray());
                    if (err != Error.Success) failures.Add($"{device.Model}: {err}");
                }
            }
        }

        if (failures.Count > 0) throw new InvalidOperationException(string.Join("; ", failures));
        return Task.CompletedTask;
    }

    protected override unsafe Task RestoreCoreAsync(CancellationToken cancellationToken)
    {
        KeyValuePair<string, uint[]>[] controlled;
        lock (_controlled)
        {
            controlled = [.. _controlled];
            _controlled.Clear();
        }

        foreach (var (deviceId, luids) in controlled)
        {
            var id = ToNative(deviceId);
            fixed (byte* pId = id)
            {
                // Our layer would otherwise keep showing black above iCUE's (shared clients sit at priority 128).
                if (luids.Length > 0)
                    CorsairSetLedColors(pId, luids.Length, luids.Select(l => new LedColor { Id = l, A = 0 }).ToArray());
                var err = CorsairReleaseControl(pId);
                if (err != Error.Success) Log.Warn($"[corsair] ReleaseControl({deviceId}) returned {err}");
            }
        }
        return Task.CompletedTask;
    }

    protected override void EmergencyRestoreCore() => RestoreCoreAsync(CancellationToken.None).GetAwaiter().GetResult();

    protected override ValueTask DisposeCoreAsync()
    {
        if (_connectCalled)
        {
            CorsairDisconnect();
            _connectCalled = false;
        }
        return ValueTask.CompletedTask;
    }

    private static unsafe SessionStateChangedHandler CreateHandler() => OnSessionStateChanged;

    private static unsafe void OnSessionStateChanged(IntPtr context, SessionStateChanged* data)
    {
        _session = data->State;
        _details = data->Details;
        Log.Info($"[corsair] session state: {data->State}");
    }

    private sealed record NativeDevice(string Id, string Serial, string Model, DeviceType Type, int LedCount);

    private static unsafe List<NativeDevice> GetDevices()
    {
        var buffer = new DeviceInfoNative[DeviceCountMax];
        var err = CorsairGetDevices(new DeviceFilter { DeviceTypeMask = DeviceType.All }, buffer.Length, buffer, out var count);
        if (err != Error.Success)
        {
            Log.Warn($"[corsair] GetDevices returned {err}");
            return [];
        }

        var list = new List<NativeDevice>(count);
        for (var i = 0; i < count; i++)
        {
            fixed (DeviceInfoNative* d = &buffer[i])
            {
                list.Add(new NativeDevice(
                    ReadString(d->Id, StringSizeM),
                    ReadString(d->Serial, StringSizeM),
                    ReadString(d->Model, StringSizeM),
                    d->Type,
                    d->LedCount));
            }
        }
        return list;
    }

    private string KeyFor(NativeDevice d) => $"{Id}:{(string.IsNullOrWhiteSpace(d.Serial) ? d.Id : d.Serial)}";

    private static DeviceCategory MapCategory(DeviceType type) => type switch
    {
        DeviceType.Keyboard => DeviceCategory.Keyboard,
        DeviceType.Mouse => DeviceCategory.Mouse,
        DeviceType.Mousemat => DeviceCategory.Mousepad,
        DeviceType.Headset => DeviceCategory.Headset,
        DeviceType.HeadsetStand => DeviceCategory.HeadsetStand,
        DeviceType.FanLedController => DeviceCategory.Fan,
        DeviceType.LedController => DeviceCategory.LedStrip,
        DeviceType.MemoryModule => DeviceCategory.Memory,
        DeviceType.Cooler => DeviceCategory.Cooler,
        DeviceType.Motherboard => DeviceCategory.Motherboard,
        DeviceType.GraphicsCard => DeviceCategory.GraphicsCard,
        DeviceType.GameController => DeviceCategory.Controller,
        _ => DeviceCategory.Other,
    };
}
