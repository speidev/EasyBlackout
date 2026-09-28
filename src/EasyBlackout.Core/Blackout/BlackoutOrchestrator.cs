using EasyBlackout.Core.Logging;
using EasyBlackout.Core.Monitors;
using EasyBlackout.Core.Peripherals;
using EasyBlackout.Core.Providers;
using EasyBlackout.Core.Settings;

namespace EasyBlackout.Core.Blackout;

/// <summary>
/// Coordinates a blackout across the overlay and every enabled provider. Screens go dark synchronously
/// first; providers then run in parallel, each with a timeout, so one slow or broken SDK never holds up the rest.
/// </summary>
public sealed class BlackoutOrchestrator : IAsyncDisposable
{
    private readonly IOverlayController? _overlay;
    private readonly SettingsStore _settings;
    private readonly Func<PeripheralSnapshot> _scanPeripherals;
    private readonly SemaphoreSlim _toggleGate = new(1, 1);
    private readonly SemaphoreSlim _refreshGate = new(1, 1);
    private readonly List<IBlackoutProvider> _engaged = [];
    private BlackoutContext _lastContext = BlackoutContext.Everything;
    private volatile bool _isBlackedOut;

    public BlackoutOrchestrator(
        IReadOnlyList<IBlackoutProvider> providers,
        IOverlayController? overlay,
        SettingsStore settings,
        Func<PeripheralSnapshot>? scanPeripherals = null)
    {
        Providers = providers;
        _overlay = overlay;
        _settings = settings;
        _scanPeripherals = scanPeripherals ?? PeripheralScanner.Scan;
        foreach (var p in providers) p.Changed += (_, _) => Changed?.Invoke(this, EventArgs.Empty);
    }

    public IReadOnlyList<IBlackoutProvider> Providers { get; }

    public bool IsBlackedOut => _isBlackedOut;

    public TimeSpan ProviderTimeout { get; init; } = TimeSpan.FromSeconds(4);

    /// <summary>Refresh may probe DDC/CI on several monitors, which is slow the first time.</summary>
    public TimeSpan RefreshTimeout { get; init; } = TimeSpan.FromSeconds(15);

    /// <summary>Raised (on arbitrary threads) whenever blackout state or any provider state changes.</summary>
    public event EventHandler? Changed;

    public bool IsProviderEnabled(IBlackoutProvider provider) =>
        !_settings.Current.DisabledProviders.Contains(provider.Id);

    public Task ToggleAsync() => IsBlackedOut ? RestoreAsync() : BlackoutAsync();

    public async Task BlackoutAsync()
    {
        await _toggleGate.WaitAsync().ConfigureAwait(true);
        try
        {
            if (_isBlackedOut) return;
            var stopwatch = System.Diagnostics.Stopwatch.StartNew();
            var settings = _settings.Current;
            var context = new BlackoutContext(settings.ExcludedDevices, settings.DdcPowerOffMonitors);
            _lastContext = context;
            _isBlackedOut = true;

            // 1) Screens: synchronous, on the caller's (UI) thread, before anything that could be slow.
            try { _overlay?.ShowOverlays(context); }
            catch (Exception ex) { Log.Error("Showing overlays failed", ex); }
            var overlayMs = stopwatch.ElapsedMilliseconds;
            Changed?.Invoke(this, EventArgs.Empty);

            // 2) Everything else in parallel.
            var targets = Providers.Where(p => !settings.DisabledProviders.Contains(p.Id)).ToList();
            lock (_engaged) _engaged.AddRange(targets);
            Log.Info($"Blackout → {string.Join(", ", targets.Select(t => t.Id))}");
            await Task.WhenAll(targets.Select(p => RunGuarded(p, "blackout", ct => p.BlackoutAsync(context, ct))))
                .ConfigureAwait(true);
            Log.Info($"Blackout complete: overlays {overlayMs} ms, all providers {stopwatch.ElapsedMilliseconds} ms");
        }
        finally
        {
            _toggleGate.Release();
            Changed?.Invoke(this, EventArgs.Empty);
        }
    }

    public async Task RestoreAsync()
    {
        await _toggleGate.WaitAsync().ConfigureAwait(true);
        try
        {
            if (!_isBlackedOut) return;
            _isBlackedOut = false;

            IBlackoutProvider[] engaged;
            lock (_engaged)
            {
                engaged = [.. _engaged];
                _engaged.Clear();
            }

            Log.Info($"Restore → {string.Join(", ", engaged.Select(t => t.Id))}");
            var restores = engaged.Select(p => RunGuarded(p, "restore", p.RestoreAsync)).ToList();

            // Screens come back immediately; hardware restores finish in the background of this await.
            try { _overlay?.HideOverlays(); }
            catch (Exception ex) { Log.Error("Hiding overlays failed", ex); }
            Changed?.Invoke(this, EventArgs.Empty);

            await Task.WhenAll(restores).ConfigureAwait(true);
            Log.Info("Restore complete");
        }
        finally
        {
            _toggleGate.Release();
            Changed?.Invoke(this, EventArgs.Empty);
        }
    }

    /// <summary>
    /// Re-applies the current blackout, e.g. after resume from sleep or a display change, when vendor software
    /// or Windows may have reset things underneath us.
    /// </summary>
    public async Task ReapplyAsync()
    {
        await _toggleGate.WaitAsync().ConfigureAwait(true);
        try
        {
            if (!_isBlackedOut) return;
            try { _overlay?.ShowOverlays(_lastContext); }
            catch (Exception ex) { Log.Error("Re-showing overlays failed", ex); }

            IBlackoutProvider[] engaged;
            lock (_engaged) engaged = [.. _engaged];
            await Task.WhenAll(engaged.Select(p => RunGuarded(p, "reapply", async ct =>
            {
                await p.RestoreAsync(ct).ConfigureAwait(false);
                await p.BlackoutAsync(_lastContext, ct).ConfigureAwait(false);
            }))).ConfigureAwait(true);
        }
        finally
        {
            _toggleGate.Release();
        }
    }

    /// <summary>Only re-shows overlays (display topology changed while blacked out).</summary>
    public void ReapplyOverlays()
    {
        if (!_isBlackedOut) return;
        try { _overlay?.ShowOverlays(_lastContext); }
        catch (Exception ex) { Log.Error("Re-showing overlays failed", ex); }
    }

    /// <summary>Scans peripherals and refreshes every provider (skipped while blacked out).</summary>
    public async Task RefreshAsync(CancellationToken cancellationToken = default)
    {
        if (_isBlackedOut || !await _refreshGate.WaitAsync(0, cancellationToken).ConfigureAwait(false)) return;
        try
        {
            var snapshot = await Task.Run(_scanPeripherals, cancellationToken).ConfigureAwait(false);
            await Task.WhenAll(Providers.Select(p => RunGuarded(p, "refresh", ct => p.RefreshAsync(snapshot, ct), cancellationToken, RefreshTimeout)))
                .ConfigureAwait(false);
        }
        finally
        {
            _refreshGate.Release();
            Changed?.Invoke(this, EventArgs.Empty);
        }
    }

    /// <summary>Synchronous, bounded, never-throwing restore for exit / crash / logoff paths.</summary>
    public void EmergencyRestore()
    {
        if (!_isBlackedOut) return;
        _isBlackedOut = false;
        Log.Warn("Emergency restore");

        // Hardware first: on a crash the UI thread may be unusable, and lights matter more than a window.
        IBlackoutProvider[] engaged;
        lock (_engaged)
        {
            engaged = [.. _engaged];
            _engaged.Clear();
        }
        var tasks = engaged.Select(p => Task.Run(p.EmergencyRestore)).ToArray();
        try { Task.WaitAll(tasks, TimeSpan.FromSeconds(3)); }
        catch (Exception ex) { Log.Error("Emergency restore incomplete", ex); }

        try { _overlay?.HideOverlays(); }
        catch (Exception ex) { Log.Error("Hiding overlays during emergency restore failed", ex); }
    }

    private async Task RunGuarded(IBlackoutProvider provider, string operation, Func<CancellationToken, Task> action,
        CancellationToken outer = default, TimeSpan? timeout = null)
    {
        var limit = timeout ?? ProviderTimeout;
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(outer);
        cts.CancelAfter(limit);
        // Task.Run: native SDK calls are synchronous and must never block the UI thread.
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var task = Task.Run(() => action(cts.Token), CancellationToken.None);
        var finished = await Task.WhenAny(task, Task.Delay(limit + TimeSpan.FromMilliseconds(250), CancellationToken.None))
            .ConfigureAwait(false);

        if (finished == task && operation != "refresh")
            Log.Info($"[{provider.Id}] {operation} finished in {sw.ElapsedMilliseconds} ms");

        if (finished != task)
        {
            provider.ReportError($"{Capitalize(operation)} timed out");
            ObserveLater(task);
            return;
        }

        try
        {
            await task.ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!outer.IsCancellationRequested)
        {
            provider.ReportError($"{Capitalize(operation)} timed out");
        }
        catch (OperationCanceledException)
        {
            // App shutting down / refresh cancelled.
        }
        catch (Exception ex)
        {
            provider.ReportError($"{Capitalize(operation)} failed", ex);
        }
    }

    private static void ObserveLater(Task task) =>
        task.ContinueWith(t => Log.Error("Late provider failure", t.Exception), TaskContinuationOptions.OnlyOnFaulted);

    private static string Capitalize(string s) => char.ToUpperInvariant(s[0]) + s[1..];

    public async ValueTask DisposeAsync()
    {
        EmergencyRestore();
        foreach (var p in Providers)
        {
            try { await p.DisposeAsync().ConfigureAwait(false); }
            catch (Exception ex) { Log.Error($"Disposing {p.Id} failed", ex); }
        }
    }
}
