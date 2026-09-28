using System.Diagnostics;
using EasyBlackout.Core.Interop;
using EasyBlackout.Core.Logging;
using EasyBlackout.Core.Peripherals;

namespace EasyBlackout.Core.Providers.HyperX;

/// <summary>
/// HyperX peripherals. NGENUITY has no third-party lighting SDK, so supported models are driven directly over HID
/// in "direct mode": black frames are streamed only while blacked out. HyperX firmware falls back to the lighting
/// stored on the device (as configured in NGENUITY) as soon as the stream stops, so restore, exit and even a crash
/// all hand lighting straight back without us having to save or rewrite anything.
/// </summary>
public sealed class HyperXProvider : BlackoutProviderBase
{
    private static readonly IReadOnlySet<ushort> Vids = new HashSet<ushort> { HyperXModels.KingstonVid, HyperXModels.HpVid };

    private readonly object _sync = new();
    private List<SupportedDevice> _supported = [];
    private List<(HidDevice Device, HyperXModel Model)> _open = [];
    private Thread? _streamThread;
    private volatile bool _streaming;

    private sealed record SupportedDevice(HyperXModel Model, HidInterfaceInfo Info, string Key);

    public override string Id => "hyperx";
    public override string DisplayName => "HyperX";

    private static bool IsNgenuityRunning() =>
        Process.GetProcesses().Any(p =>
        {
            try { return p.ProcessName.Contains("ngenuity", StringComparison.OrdinalIgnoreCase); }
            catch { return false; }
        });

    private static string KeyFor(HyperXModel model, HidInterfaceInfo info)
    {
        // Instance part of the path distinguishes two identical mice.
        var parts = info.Path.Split('#');
        return $"hyperx:{info.Vid:X4}:{info.Pid:X4}:{(parts.Length > 2 ? parts[2] : info.Path)}";
    }

    private static List<SupportedDevice> FindSupported()
    {
        var found = new List<SupportedDevice>();
        foreach (var info in HidDevice.Enumerate(Vids))
        {
            var model = HyperXModels.Find(info.Vid, info.Pid);
            if (model is null || info.InterfaceNumber != model.InterfaceNumber || info.UsagePage != model.UsagePage) continue;
            if (info.FeatureReportLength != model.FeatureReportLength)
            {
                Log.Warn($"[hyperx] {model.Name}: unexpected feature report length {info.FeatureReportLength}; skipping");
                continue;
            }
            found.Add(new SupportedDevice(model, info, KeyFor(model, info)));
        }
        return found;
    }

    protected override Task RefreshCoreAsync(PeripheralSnapshot peripherals, CancellationToken cancellationToken)
    {
        var supported = FindSupported();
        lock (_sync) _supported = supported;

        var devices = supported
            .Select(s => new DeviceInfo(s.Key, s.Model.Name, s.Model.Category, "Direct HID · reverts to onboard lighting on restore"))
            .ToList();

        // HyperX gear we can see but have no native protocol for. OpenRGB or Dynamic Lighting may still reach it.
        var supportedPids = supported.Select(s => (s.Info.Vid, s.Info.Pid)).ToHashSet();
        devices.AddRange(peripherals.Devices
            .Where(p => KnownVendors.IsHyperX(p.Vid, p.Name) && !supportedPids.Contains((p.Vid, p.Pid)))
            .DistinctBy(p => (p.Vid, p.Pid))
            .Select(p => new DeviceInfo($"hyperx:{p.Vid:X4}:{p.Pid:X4}", p.Name, p.Category,
                "No native support yet. Run OpenRGB (or use Dynamic Lighting) to include it.",
                Controllable: false, SupportsSelection: false)));

        SetDevices(devices);
        var ngenuity = IsNgenuityRunning() ? "NGENUITY is running; its lighting resumes after restore." : "Works with or without NGENUITY.";
        SetState(ProviderState.Ready, devices.Count == 0
            ? "No HyperX devices connected."
            : $"{ngenuity} Supported natively: {string.Join(", ", HyperXModels.All.Select(m => m.Name.Replace("HyperX ", "")))}.");
        return Task.CompletedTask;
    }

    protected override Task BlackoutCoreAsync(BlackoutContext context, CancellationToken cancellationToken)
    {
        List<SupportedDevice> targets;
        lock (_sync) targets = _supported.Where(s => context.IsIncluded(s.Key)).ToList();
        if (targets.Count == 0) return Task.CompletedTask;

        var open = new List<(HidDevice, HyperXModel)>();
        var failures = new List<string>();
        foreach (var (model, info, _) in targets)
        {
            var device = HidDevice.Open(info);
            if (device is null)
            {
                failures.Add($"{model.Name}: could not open the lighting interface");
                continue;
            }
            if (!SendBlack(device, model))
            {
                device.Dispose();
                failures.Add($"{model.Name}: device rejected the lighting command");
                continue;
            }
            open.Add((device, model));
        }

        lock (_sync) _open = open;
        if (open.Count > 0) StartStreaming(open.Min(o => o.Item2.DirectModeTimeout));
        if (failures.Count > 0) throw new InvalidOperationException(string.Join("; ", failures));
        return Task.CompletedTask;
    }

    private static bool SendBlack(HidDevice device, HyperXModel model)
    {
        foreach (var packet in model.BuildFrame(0, 0, 0))
            if (!device.SetFeature(packet)) return false;
        return true;
    }

    /// <summary>Keeps direct mode alive with black frames at a comfortable margin below the device timeout.</summary>
    private void StartStreaming(TimeSpan deviceTimeout)
    {
        var interval = TimeSpan.FromMilliseconds(Math.Max(10, deviceTimeout.TotalMilliseconds * 0.4));
        _streaming = true;
        _streamThread = new Thread(() =>
        {
            var failuresInARow = 0;
            while (_streaming)
            {
                List<(HidDevice Device, HyperXModel Model)> open;
                lock (_sync) open = _open;
                var ok = true;
                foreach (var (device, model) in open)
                {
                    try { ok &= SendBlack(device, model); }
                    catch (ObjectDisposedException) { return; }
                }
                // Unplugged mid-blackout: stop hammering a dead handle; the refresh after restore rediscovers it.
                failuresInARow = ok ? 0 : failuresInARow + 1;
                if (failuresInARow == 100) Log.Warn("[hyperx] device stopped accepting frames (unplugged?)");
                Thread.Sleep(interval);
            }
        })
        {
            IsBackground = true, // never keeps the process alive; if we die, the mouse reverts by itself
            Priority = ThreadPriority.AboveNormal,
            Name = "HyperX direct-mode stream",
        };
        _streamThread.Start();
    }

    protected override Task RestoreCoreAsync(CancellationToken cancellationToken)
    {
        StopStreaming();
        return Task.CompletedTask;
    }

    protected override void EmergencyRestoreCore() => StopStreaming();

    private void StopStreaming()
    {
        _streaming = false;
        _streamThread?.Join(TimeSpan.FromSeconds(1));
        _streamThread = null;
        List<(HidDevice Device, HyperXModel Model)> open;
        lock (_sync)
        {
            open = _open;
            _open = [];
        }
        // Nothing to write back: once frames stop, the firmware returns to its stored (NGENUITY) lighting.
        foreach (var (device, _) in open) device.Dispose();
    }

    protected override ValueTask DisposeCoreAsync()
    {
        StopStreaming();
        return ValueTask.CompletedTask;
    }
}
