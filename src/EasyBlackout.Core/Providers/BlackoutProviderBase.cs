using EasyBlackout.Core.Logging;
using EasyBlackout.Core.Peripherals;

namespace EasyBlackout.Core.Providers;

/// <summary>
/// Shared plumbing: serialises operations per provider, tracks state and errors, raises <see cref="Changed"/>.
/// </summary>
public abstract class BlackoutProviderBase : IBlackoutProvider
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private volatile bool _disposed;

    public abstract string Id { get; }
    public abstract string DisplayName { get; }
    public virtual ProviderKind Kind => ProviderKind.Lighting;

    public ProviderState State { get; private set; } = ProviderState.Checking;
    public string? StatusDetail { get; private set; }
    public string? LastError { get; private set; }
    public bool IsBlackedOut { get; protected set; }
    public IReadOnlyList<DeviceInfo> Devices { get; private set; } = [];

    public event EventHandler? Changed;

    public async Task RefreshAsync(PeripheralSnapshot peripherals, CancellationToken cancellationToken)
    {
        if (_disposed) return;
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await RefreshCoreAsync(peripherals, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            Log.Error($"[{Id}] refresh failed", ex);
            SetState(ProviderState.Error, ex.Message);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task BlackoutAsync(BlackoutContext context, CancellationToken cancellationToken)
    {
        if (_disposed) return;
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            IsBlackedOut = true; // set first so a partial failure still gets restored
            await BlackoutCoreAsync(context, cancellationToken).ConfigureAwait(false);
            ClearError();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            ReportError("Blackout failed", ex);
        }
        finally
        {
            _gate.Release();
            RaiseChanged();
        }
    }

    public async Task RestoreAsync(CancellationToken cancellationToken)
    {
        if (_disposed) return;
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await RestoreCoreAsync(cancellationToken).ConfigureAwait(false);
            ClearError();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            ReportError("Restore failed", ex);
        }
        finally
        {
            IsBlackedOut = false;
            _gate.Release();
            RaiseChanged();
        }
    }

    public void EmergencyRestore()
    {
        if (!IsBlackedOut) return;
        try
        {
            EmergencyRestoreCore();
        }
        catch (Exception ex)
        {
            Log.Error($"[{Id}] emergency restore failed", ex);
        }
        IsBlackedOut = false;
    }

    /// <summary>Records a failure that did not come from an exception inside this provider (e.g. a timeout).</summary>
    public void ReportError(string message, Exception? ex = null)
    {
        LastError = ex is null ? message : $"{message}: {ex.Message}";
        Log.Error($"[{Id}] {message}", ex);
        RaiseChanged();
    }

    protected abstract Task RefreshCoreAsync(PeripheralSnapshot peripherals, CancellationToken cancellationToken);
    protected abstract Task BlackoutCoreAsync(BlackoutContext context, CancellationToken cancellationToken);
    protected abstract Task RestoreCoreAsync(CancellationToken cancellationToken);

    /// <summary>Default: run the async restore synchronously with a short cap.</summary>
    protected virtual void EmergencyRestoreCore()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        Task.Run(() => RestoreCoreAsync(cts.Token)).Wait(TimeSpan.FromSeconds(2.5));
    }

    protected void SetState(ProviderState state, string? detail = null)
    {
        if (State == state && StatusDetail == detail) return;
        State = state;
        StatusDetail = detail;
        RaiseChanged();
    }

    protected void SetDevices(IReadOnlyList<DeviceInfo> devices)
    {
        if (Devices.SequenceEqual(devices)) return;
        Devices = devices;
        RaiseChanged();
    }

    /// <summary>
    /// Devices from a USB scan, used by ecosystems whose SDK can't enumerate hardware.
    /// </summary>
    protected static IReadOnlyList<DeviceInfo> DevicesFromScan(
        IEnumerable<UsbPeripheral> peripherals, string keyPrefix, bool controllable, string? detail)
    {
        return peripherals
            .Select(p => new DeviceInfo($"{keyPrefix}:{p.Vid:X4}:{p.Pid:X4}", p.Name, p.Category, detail, controllable,
                SupportsSelection: false))
            .DistinctBy(d => d.Key)
            .OrderBy(d => d.Category).ThenBy(d => d.Name)
            .ToList();
    }

    protected void ClearError()
    {
        if (LastError is null) return;
        LastError = null;
        RaiseChanged();
    }

    protected void RaiseChanged() => Changed?.Invoke(this, EventArgs.Empty);

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        EmergencyRestore();
        _disposed = true;
        try { await DisposeCoreAsync().ConfigureAwait(false); }
        catch (Exception ex) { Log.Error($"[{Id}] dispose failed", ex); }
        GC.SuppressFinalize(this);
    }

    protected virtual ValueTask DisposeCoreAsync() => ValueTask.CompletedTask;
}
