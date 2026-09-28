using System.Collections.Concurrent;
using EasyBlackout.Core.Logging;
using EasyBlackout.Core.Peripherals;
using Windows.Devices.Enumeration;
using Windows.Devices.Lights;
using Windows.Foundation.Metadata;

namespace EasyBlackout.Core.Providers.DynamicLighting;

/// <summary>
/// Windows 11 Dynamic Lighting (HID LampArray) — covers devices that need no vendor software at all.
/// Windows grants lamp control to the foreground app that has the LampArray open; during a blackout that is our
/// overlay. LampArray objects are only opened for the duration of the blackout, so merely having the
/// EasyBlackout window focused never overrides the user's ambient lighting.
/// </summary>
public sealed class DynamicLightingProvider : BlackoutProviderBase
{
    private static readonly Windows.UI.Color Black = Windows.UI.Color.FromArgb(255, 0, 0, 0);

    private readonly ConcurrentDictionary<string, DeviceInformation> _known = new();
    private readonly List<LampArray> _active = [];
    private DeviceWatcher? _watcher;
    private bool? _supported;

    public override string Id => "dynamiclighting";
    public override string DisplayName => "Windows Dynamic Lighting";

    protected override async Task RefreshCoreAsync(PeripheralSnapshot peripherals, CancellationToken cancellationToken)
    {
        _supported ??= ApiInformation.IsTypePresent("Windows.Devices.Lights.LampArray") &&
                       ApiInformation.IsPropertyPresent("Windows.Devices.Lights.LampArray", "IsAvailable");
        if (_supported != true)
        {
            SetState(ProviderState.Unavailable, "Requires Windows 11 22H2 or later.");
            SetDevices([]);
            return;
        }

        if (_watcher is null)
        {
            // Initial list synchronously, then keep it current with a watcher.
            var found = await DeviceInformation.FindAllAsync(LampArray.GetDeviceSelector()).AsTask(cancellationToken).ConfigureAwait(false);
            foreach (var d in found) _known[d.Id] = d;
            StartWatcher();
        }

        PublishDevices();
    }

    private void StartWatcher()
    {
        _watcher = DeviceInformation.CreateWatcher(LampArray.GetDeviceSelector());
        _watcher.Added += (s, d) => { _known[d.Id] = d; PublishDevices(); };
        _watcher.Removed += (s, u) => { _known.TryRemove(u.Id, out _); PublishDevices(); };
        _watcher.Updated += (s, u) =>
        {
            if (_known.TryGetValue(u.Id, out var d)) { d.Update(u); PublishDevices(); }
        };
        _watcher.Start();
    }

    private void PublishDevices()
    {
        var devices = _known.Values
            .Where(d => d.IsEnabled)
            .Select(d => new DeviceInfo($"{Id}:{d.Id}", string.IsNullOrWhiteSpace(d.Name) ? "Lighting device" : d.Name,
                PeripheralScanner.GuessCategory(d.Name ?? ""), "Controlled while blacked out"))
            .OrderBy(d => d.Name)
            .ToList();
        SetDevices(devices);
        SetState(ProviderState.Ready, devices.Count == 0
            ? "No Dynamic Lighting devices connected."
            : "Lamps are controlled only while the blackout overlay is in front.");
    }

    protected override async Task BlackoutCoreAsync(BlackoutContext context, CancellationToken cancellationToken)
    {
        if (_supported != true) return;
        var targets = _known.Values.Where(d => d.IsEnabled && context.IsIncluded($"{Id}:{d.Id}")).ToList();
        foreach (var info in targets)
        {
            try
            {
                var lamps = await LampArray.FromIdAsync(info.Id).AsTask(cancellationToken).ConfigureAwait(false);
                if (lamps is null) continue;
                lamps.AvailabilityChanged += OnAvailabilityChanged;
                lock (_active) _active.Add(lamps);
                if (lamps.IsAvailable) lamps.SetColor(Black);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                Log.Warn($"[dynamiclighting] {info.Name}: {ex.Message}");
            }
        }
    }

    // Control arrives asynchronously once Windows sees our overlay in the foreground.
    private void OnAvailabilityChanged(LampArray sender, object args)
    {
        if (IsBlackedOut && sender.IsAvailable)
        {
            try { sender.SetColor(Black); }
            catch (Exception ex) { Log.Warn($"[dynamiclighting] SetColor failed: {ex.Message}"); }
        }
    }

    protected override Task RestoreCoreAsync(CancellationToken cancellationToken)
    {
        LampArray[] active;
        lock (_active)
        {
            active = [.. _active];
            _active.Clear();
        }
        foreach (var lamps in active) lamps.AvailabilityChanged -= OnAvailabilityChanged;

        // LampArray has no Close(); control returns to the ambient/background controller once our references are
        // released. Force that now, otherwise lamps stay dark while an EasyBlackout window keeps the foreground.
        if (active.Length > 0)
        {
            active = [];
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
        }
        return Task.CompletedTask;
    }

    protected override void EmergencyRestoreCore() => RestoreCoreAsync(CancellationToken.None).GetAwaiter().GetResult();

    protected override ValueTask DisposeCoreAsync()
    {
        if (_watcher is { Status: DeviceWatcherStatus.Started or DeviceWatcherStatus.EnumerationCompleted })
            _watcher.Stop();
        return ValueTask.CompletedTask;
    }
}
