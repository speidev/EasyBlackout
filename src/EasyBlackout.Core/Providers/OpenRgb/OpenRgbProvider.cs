using System.Net.Sockets;
using EasyBlackout.Core.Logging;
using EasyBlackout.Core.Peripherals;
using OpenRGB.NET;

namespace EasyBlackout.Core.Providers.OpenRgb;

/// <summary>
/// OpenRGB SDK server (optional) — motherboards, RAM, GPUs, fans and anything else OpenRGB supports.
/// Each controller's mode and colors are captured before blackout and written back on restore.
/// </summary>
public sealed class OpenRgbProvider : BlackoutProviderBase
{
    private const string Host = "127.0.0.1";
    private const int Port = 6742;

    private sealed record SavedState(string Key, int ModeIndex, Mode Mode, Color[] Colors);

    private readonly List<SavedState> _saved = [];

    public override string Id => "openrgb";
    public override string DisplayName => "OpenRGB";

    private static bool IsServerListening()
    {
        try
        {
            using var socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
            return socket.BeginConnect(Host, Port, null, null).AsyncWaitHandle.WaitOne(250) && socket.Connected;
        }
        catch (SocketException)
        {
            return false;
        }
    }

    private static OpenRgbClient Connect() => new(Host, Port, "EasyBlackout", autoConnect: true, timeoutMs: 1000);

    private static string KeyFor(Device d) => $"openrgb:{d.Name}|{d.Location}";

    protected override Task RefreshCoreAsync(PeripheralSnapshot peripherals, CancellationToken cancellationToken)
    {
        if (!IsServerListening())
        {
            SetState(ProviderState.NotRunning,
                "Optional. To reach motherboard, RAM, GPU and other RGB, run OpenRGB with its SDK server enabled.");
            SetDevices([]);
            return Task.CompletedTask;
        }

        using var client = Connect();
        var devices = client.GetAllControllerData()
            .Select(d => new DeviceInfo(KeyFor(d), d.Name, MapCategory(d.Type),
                string.Join(" · ", new[] { d.Vendor, d.Leds.Length == 1 ? "1 LED" : $"{d.Leds.Length} LEDs" }
                    .Where(s => !string.IsNullOrWhiteSpace(s)))))
            .ToList();
        SetDevices(devices);
        SetState(ProviderState.Ready, "SDK server on port 6742");
        return Task.CompletedTask;
    }

    protected override Task BlackoutCoreAsync(BlackoutContext context, CancellationToken cancellationToken)
    {
        if (State is not ProviderState.Ready) return Task.CompletedTask; // server absent at last refresh

        using var client = Connect();
        var failures = new List<string>();
        foreach (var device in client.GetAllControllerData())
        {
            var key = KeyFor(device);
            if (!context.IsIncluded(key)) continue;
            try
            {
                lock (_saved) _saved.Add(new SavedState(key, device.ActiveModeIndex, device.Modes[device.ActiveModeIndex], device.Colors.ToArray()));

                if (device.Leds.Length > 0)
                {
                    client.SetCustomMode(device.Index);
                    client.UpdateLeds(device.Index, new Color[device.Leds.Length]);
                }
                else if (Array.FindIndex(device.Modes, m => m.Name.Equals("Off", StringComparison.OrdinalIgnoreCase)) is var off and >= 0)
                {
                    client.UpdateMode(device.Index, off);
                }
            }
            catch (Exception ex)
            {
                failures.Add($"{device.Name}: {ex.Message}");
            }
        }
        if (failures.Count > 0) throw new InvalidOperationException(string.Join("; ", failures));
        return Task.CompletedTask;
    }

    protected override Task RestoreCoreAsync(CancellationToken cancellationToken)
    {
        SavedState[] saved;
        lock (_saved)
        {
            saved = [.. _saved];
            _saved.Clear();
        }
        if (saved.Length == 0 || !IsServerListening()) return Task.CompletedTask;

        using var client = Connect();
        // Controller indexes can shift if OpenRGB rescanned, so match by stable key.
        var byKey = client.GetAllControllerData().ToDictionary(KeyFor, d => d.Index);
        foreach (var s in saved)
        {
            if (!byKey.TryGetValue(s.Key, out var index)) continue;
            try
            {
                var m = s.Mode;
                client.UpdateMode(index, s.ModeIndex,
                    m.SupportsSpeed ? m.Speed : null,
                    m.SupportsDirection ? m.Direction : null,
                    m.Colors.Length > 0 ? m.Colors : null);
                if (m.ColorMode == ColorMode.PerLed && s.Colors.Length > 0)
                    client.UpdateLeds(index, s.Colors);
            }
            catch (Exception ex)
            {
                Log.Warn($"[openrgb] restoring {s.Key} failed: {ex.Message}");
            }
        }
        return Task.CompletedTask;
    }

    protected override void EmergencyRestoreCore() => RestoreCoreAsync(CancellationToken.None).GetAwaiter().GetResult();

    private static DeviceCategory MapCategory(DeviceType type) => type switch
    {
        DeviceType.Motherboard => DeviceCategory.Motherboard,
        DeviceType.Dram => DeviceCategory.Memory,
        DeviceType.Gpu => DeviceCategory.GraphicsCard,
        DeviceType.Cooler => DeviceCategory.Cooler,
        DeviceType.Ledstrip => DeviceCategory.LedStrip,
        DeviceType.Keyboard => DeviceCategory.Keyboard,
        DeviceType.Mouse => DeviceCategory.Mouse,
        DeviceType.Mousemat => DeviceCategory.Mousepad,
        DeviceType.Headset => DeviceCategory.Headset,
        DeviceType.HeadsetStand => DeviceCategory.HeadsetStand,
        DeviceType.Gamepad => DeviceCategory.Controller,
        DeviceType.Speaker => DeviceCategory.Speaker,

        _ => DeviceCategory.Other,
    };
}
