using EasyBlackout.Core.Peripherals;

namespace EasyBlackout.Core.Providers;

/// <summary>
/// One lighting ecosystem (or the monitors). Providers only take control of hardware between
/// <see cref="BlackoutAsync"/> and <see cref="RestoreAsync"/>; while idle they must not affect anything.
/// </summary>
public interface IBlackoutProvider : IAsyncDisposable
{
    /// <summary>Stable id used in settings, e.g. "corsair".</summary>
    string Id { get; }

    string DisplayName { get; }

    ProviderKind Kind { get; }

    ProviderState State { get; }

    /// <summary>Human-readable explanation of <see cref="State"/> (setup instructions, versions, …).</summary>
    string? StatusDetail { get; }

    /// <summary>Error from the most recent blackout/restore, cleared by the next successful one.</summary>
    string? LastError { get; }

    bool IsBlackedOut { get; }

    IReadOnlyList<DeviceInfo> Devices { get; }

    event EventHandler? Changed;

    Task RefreshAsync(PeripheralSnapshot peripherals, CancellationToken cancellationToken);

    Task BlackoutAsync(BlackoutContext context, CancellationToken cancellationToken);

    Task RestoreAsync(CancellationToken cancellationToken);

    /// <summary>Synchronous best-effort restore for crash / shutdown paths. Must never throw.</summary>
    void EmergencyRestore();

    /// <summary>Records a failure detected outside the provider (e.g. the orchestrator's timeout).</summary>
    void ReportError(string message, Exception? ex = null);
}
