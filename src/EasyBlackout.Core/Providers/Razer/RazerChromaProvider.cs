using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using EasyBlackout.Core.Logging;
using EasyBlackout.Core.Peripherals;

namespace EasyBlackout.Core.Providers.Razer;

/// <summary>
/// Razer Synapse via the Chroma SDK REST API (http://localhost:54235). A session exists only during a blackout;
/// deleting it hands every device straight back to Synapse.
/// </summary>
public sealed class RazerChromaProvider : BlackoutProviderBase
{
    private const string BaseUrl = "http://localhost:54235/razer/chromasdk";
    private static readonly string[] Endpoints = ["keyboard", "mouse", "mousepad", "headset", "keypad", "chromalink"];

    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(3) };
    private string? _sessionUri;
    private Timer? _heartbeat;

    public override string Id => "razer";
    public override string DisplayName => "Razer Chroma";

    protected override async Task RefreshCoreAsync(PeripheralSnapshot peripherals, CancellationToken cancellationToken)
    {
        var scanned = peripherals.ForVendor(KnownVendors.Razer).ToList();
        var version = await GetVersionAsync(cancellationToken).ConfigureAwait(false);
        if (version is null)
        {
            SetState(ProviderState.NotRunning, scanned.Count > 0
                ? "Razer devices found, but Razer Synapse (Chroma SDK) isn't running. Install or start Synapse."
                : "Razer Synapse (Chroma SDK) isn't running.");
            SetDevices(DevicesFromScan(scanned, Id, controllable: false, "Needs Razer Synapse"));
            return;
        }

        SetState(ProviderState.Ready, $"Chroma SDK {version}");
        SetDevices(scanned.Count > 0
            ? DevicesFromScan(scanned, Id, controllable: true, "Chroma")
            : [new DeviceInfo($"{Id}:all", "All Chroma devices", DeviceCategory.Other, "Synapse didn't report specific hardware", SupportsSelection: false)]);
    }

    private async Task<string?> GetVersionAsync(CancellationToken cancellationToken)
    {
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            cts.CancelAfter(TimeSpan.FromMilliseconds(800));
            var json = await _http.GetFromJsonAsync<JsonObject>(BaseUrl, cts.Token).ConfigureAwait(false);
            return json?["version"]?.GetValue<string>() ?? json?["core"]?.GetValue<string>() ?? "available";
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException or InvalidOperationException)
        {
            return null;
        }
    }

    protected override async Task BlackoutCoreAsync(BlackoutContext context, CancellationToken cancellationToken)
    {
        // Synapse wasn't there at the last refresh: skip rather than wait on a refused connection
        // (localhost refusals take ~1 s on Windows). The periodic refresh picks it up once it starts.
        if (State is not ProviderState.Ready) return;

        var init = new
        {
            title = "EasyBlackout",
            description = "Turns all lighting off on demand",
            author = new { name = "EasyBlackout", contact = "https://github.com/" },
            device_supported = Endpoints,
            category = "application",
        };
        using var response = await _http.PostAsJsonAsync(BaseUrl, init, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        var body = await response.Content.ReadFromJsonAsync<JsonObject>(cancellationToken).ConfigureAwait(false);
        _sessionUri = body?["uri"]?.GetValue<string>() ?? throw new InvalidOperationException("Chroma SDK returned no session uri");

        _heartbeat = new Timer(_ => SendHeartbeat(), null, TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(5));

        await ApplyBlackAsync(cancellationToken).ConfigureAwait(false);
        // Synapse sometimes drops effects sent in the first moments of a new session; send once more.
        _ = Task.Delay(1200).ContinueWith(_ => IsBlackedOut ? ApplyBlackAsync(CancellationToken.None) : Task.CompletedTask,
            TaskScheduler.Default).Unwrap();
    }

    private async Task ApplyBlackAsync(CancellationToken cancellationToken)
    {
        var uri = _sessionUri;
        if (uri is null) return;
        var effect = new { effect = "CHROMA_STATIC", param = new { color = 0 } };
        await Task.WhenAll(Endpoints.Select(async endpoint =>
        {
            try
            {
                using var r = await _http.PutAsJsonAsync($"{uri}/{endpoint}", effect, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
            {
                Log.Warn($"[razer] {endpoint}: {ex.Message}");
            }
        })).ConfigureAwait(false);
    }

    private void SendHeartbeat()
    {
        var uri = _sessionUri;
        if (uri is null) return;
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Put, $"{uri}/heartbeat");
            _http.Send(request).Dispose();
        }
        catch (Exception ex)
        {
            Log.Warn($"[razer] heartbeat failed: {ex.Message}");
        }
    }

    protected override async Task RestoreCoreAsync(CancellationToken cancellationToken)
    {
        _heartbeat?.Dispose();
        _heartbeat = null;
        var uri = Interlocked.Exchange(ref _sessionUri, null);
        if (uri is null) return;
        using var response = await _http.DeleteAsync(uri, cancellationToken).ConfigureAwait(false);
    }

    protected override void EmergencyRestoreCore()
    {
        _heartbeat?.Dispose();
        var uri = Interlocked.Exchange(ref _sessionUri, null);
        if (uri is null) return;
        try { _http.Send(new HttpRequestMessage(HttpMethod.Delete, uri)).Dispose(); }
        catch (Exception ex) { Log.Warn($"[razer] emergency delete failed: {ex.Message}"); }
    }

    protected override ValueTask DisposeCoreAsync()
    {
        _heartbeat?.Dispose();
        _http.Dispose();
        return ValueTask.CompletedTask;
    }
}
