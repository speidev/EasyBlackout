using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using EasyBlackout.Core.Logging;
using EasyBlackout.Core.Peripherals;

namespace EasyBlackout.Core.Providers.SteelSeries;

/// <summary>
/// SteelSeries GG / Engine via GameSense. We register as a "game" whose events paint every zone black;
/// events only show while we send them, and stop_game immediately returns devices to GG's own lighting.
/// </summary>
public sealed class SteelSeriesProvider : BlackoutProviderBase
{
    private const string Game = "EASYBLACKOUT";

    // One event per device type, so a zone name one device family rejects can't break the others.
    private static readonly (string Event, string DeviceType, string[] Zones)[] Bindings =
    [
        ("BLACK_PERKEY", "rgb-per-key-zones", ["all"]),
        ("BLACK_Z1", "rgb-1-zone", Zones(1)),
        ("BLACK_Z2", "rgb-2-zone", Zones(2)),
        ("BLACK_Z3", "rgb-3-zone", Zones(3)),
        ("BLACK_Z4", "rgb-4-zone", Zones(4)),
        ("BLACK_Z5", "rgb-5-zone", Zones(5)),
        ("BLACK_Z8", "rgb-8-zone", Zones(8)),
        ("BLACK_Z12", "rgb-12-zone", Zones(12)),
        ("BLACK_Z17", "rgb-17-zone", Zones(17)),
        ("BLACK_Z24", "rgb-24-zone", Zones(24)),
    ];

    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(3) };
    private string? _address;
    private bool _registered;
    private int _eventValue;
    private Timer? _heartbeat;

    public override string Id => "steelseries";
    public override string DisplayName => "SteelSeries GG";

    private static string[] Zones(int count)
    {
        string[] names =
        [
            "one", "two", "three", "four", "five", "six", "seven", "eight", "nine", "ten", "eleven", "twelve",
            "thirteen", "fourteen", "fifteen", "sixteen", "seventeen", "eighteen", "nineteen", "twenty",
            "twenty-one", "twenty-two", "twenty-three", "twenty-four",
        ];
        return names[..count];
    }

    private static string? ReadAddress()
    {
        var programData = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
        foreach (var dir in new[] { @"SteelSeries\SteelSeries Engine 3", @"SteelSeries\GG" })
        {
            var file = Path.Combine(programData, dir, "coreProps.json");
            try
            {
                if (!File.Exists(file)) continue;
                var json = JsonNode.Parse(File.ReadAllText(file));
                var address = json?["address"]?.GetValue<string>();
                if (!string.IsNullOrWhiteSpace(address)) return address;
            }
            catch (Exception ex)
            {
                Log.Warn($"[steelseries] could not read {file}: {ex.Message}");
            }
        }
        return null;
    }

    protected override async Task RefreshCoreAsync(PeripheralSnapshot peripherals, CancellationToken cancellationToken)
    {
        var scanned = peripherals.ForVendor(KnownVendors.SteelSeries).ToList();
        var address = ReadAddress();
        if (address != _address) _registered = false;
        _address = address;

        if (address is null || !await TryRegisterAsync(cancellationToken).ConfigureAwait(false))
        {
            SetState(ProviderState.NotRunning, scanned.Count > 0
                ? "SteelSeries devices found, but SteelSeries GG isn't running. Install or start GG."
                : "SteelSeries GG isn't running.");
            SetDevices(DevicesFromScan(scanned, Id, controllable: false, "Needs SteelSeries GG"));
            return;
        }

        SetState(ProviderState.Ready, "GameSense");
        SetDevices(scanned.Count > 0
            ? DevicesFromScan(scanned, Id, controllable: true, "GameSense")
            : [new DeviceInfo($"{Id}:all", "All GameSense devices", DeviceCategory.Other, "GG didn't report specific hardware", SupportsSelection: false)]);
    }

    /// <summary>Registers the app and binds the black handlers once per GG session. Harmless while idle.</summary>
    private async Task<bool> TryRegisterAsync(CancellationToken cancellationToken)
    {
        if (_registered) return true;
        try
        {
            await PostAsync("game_metadata", new
            {
                game = Game,
                game_display_name = "EasyBlackout",
                developer = "EasyBlackout",
                deinitialize_timer_length_ms = 60000,
            }, cancellationToken).ConfigureAwait(false);

            foreach (var (evt, deviceType, zones) in Bindings)
            {
                var handlers = zones.Select(z => new Dictionary<string, object>
                {
                    ["device-type"] = deviceType,
                    ["zone"] = z,
                    ["color"] = new { red = 0, green = 0, blue = 0 },
                    ["mode"] = "color",
                }).ToArray();
                try
                {
                    await PostAsync("bind_game_event", new
                    {
                        game = Game,
                        @event = evt,
                        min_value = 0,
                        max_value = 100,
                        icon_id = 0,
                        value_optional = true,
                        handlers,
                    }, cancellationToken).ConfigureAwait(false);
                }
                catch (HttpRequestException ex)
                {
                    Log.Warn($"[steelseries] binding {evt} ({deviceType}) rejected: {ex.Message}");
                }
            }
            _registered = true;
            return true;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            return false;
        }
    }

    protected override async Task BlackoutCoreAsync(BlackoutContext context, CancellationToken cancellationToken)
    {
        if (State is not ProviderState.Ready) return; // GG absent at last refresh
        _address ??= ReadAddress();
        if (_address is null || !await TryRegisterAsync(cancellationToken).ConfigureAwait(false)) return;

        await SendEventsAsync(cancellationToken).ConfigureAwait(false);
        // Keep the game alive and re-assert the frame while blacked out.
        _heartbeat = new Timer(_ => _ = SendEventsAsync(CancellationToken.None), null,
            TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(10));
    }

    private async Task SendEventsAsync(CancellationToken cancellationToken)
    {
        // GameSense ignores repeated identical values, so cycle the value.
        var value = Interlocked.Increment(ref _eventValue) % 100 + 1;
        try
        {
            await PostAsync("multiple_game_events", new
            {
                game = Game,
                events = Bindings.Select(b => new { @event = b.Event, data = new { value } }).ToArray(),
            }, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            Log.Warn($"[steelseries] sending events failed: {ex.Message}");
            if (!cancellationToken.CanBeCanceled) return; // heartbeat: swallow
            throw;
        }
    }

    protected override async Task RestoreCoreAsync(CancellationToken cancellationToken)
    {
        _heartbeat?.Dispose();
        _heartbeat = null;
        if (_address is null) return;
        await PostAsync("stop_game", new { game = Game }, cancellationToken).ConfigureAwait(false);
    }

    protected override void EmergencyRestoreCore()
    {
        _heartbeat?.Dispose();
        if (_address is null) return;
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, $"http://{_address}/stop_game")
            {
                Content = JsonContent.Create(new { game = Game }),
            };
            _http.Send(request).Dispose();
        }
        catch (Exception ex)
        {
            Log.Warn($"[steelseries] emergency stop failed: {ex.Message}");
        }
    }

    private async Task PostAsync(string endpoint, object payload, CancellationToken cancellationToken)
    {
        using var response = await _http.PostAsJsonAsync($"http://{_address}/{endpoint}", payload, cancellationToken)
            .ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
    }

    protected override ValueTask DisposeCoreAsync()
    {
        _heartbeat?.Dispose();
        _http.Dispose();
        return ValueTask.CompletedTask;
    }
}
