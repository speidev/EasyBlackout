using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using EasyBlackout.Core.Logging;
using EasyBlackout.Core.Peripherals;
using EasyBlackout.Core.Providers;

namespace EasyBlackout.Core.Monitors;

/// <summary>Implemented by the UI: shows/hides the black fullscreen windows.</summary>
public interface IOverlayHost
{
    /// <summary>Shows overlays on exactly these monitors (hiding any others). Must be synchronous and fast.</summary>
    void Show(IReadOnlyList<MonitorInfo> monitors);

    void Hide();
}

/// <summary>The part of the monitor blackout that must happen synchronously before anything else.</summary>
public interface IOverlayController
{
    void ShowOverlays(BlackoutContext context);
    void HideOverlays();
}

/// <summary>
/// Blacks out displays with topmost overlay windows (always) and, per monitor and opt-in, DDC/CI hardware power-off.
/// </summary>
public sealed class MonitorProvider : BlackoutProviderBase, IOverlayController
{
    private readonly IOverlayHost _overlay;
    private readonly ConcurrentDictionary<string, DdcCi.Support> _ddcSupport = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, int> _ddcAttempts = new(StringComparer.OrdinalIgnoreCase);
    private const int MaxDdcAttempts = 3;
    private readonly List<string> _poweredOff = [];
    private IReadOnlyList<MonitorInfo> _monitors = [];

    public MonitorProvider(IOverlayHost overlay) => _overlay = overlay;

    public override string Id => "monitors";
    public override string DisplayName => "Displays";
    public override ProviderKind Kind => ProviderKind.Monitors;

    public IReadOnlyList<MonitorInfo> Monitors => _monitors;

    public DdcCi.Support GetDdcSupport(string monitorKey) =>
        _ddcSupport.TryGetValue(monitorKey, out var s) ? s : DdcCi.Support.Unknown;

    public void ShowOverlays(BlackoutContext context)
    {
        // Re-enumerate (a few ms) so a monitor plugged in since the last refresh is covered too.
        try { _monitors = MonitorManager.GetMonitors(); }
        catch (Exception ex) { Log.Error("Monitor enumeration failed; using cached list", ex); }

        _overlay.Show(_monitors.Where(m => context.IsIncluded(m.Key)).ToList());
    }

    public void HideOverlays() => _overlay.Hide();

    /// <summary>Turns a monitor off via DDC/CI, waits, and turns it back on. Returns whether both commands were accepted.</summary>
    public async Task<bool> TestDdcAsync(string monitorKey, TimeSpan offDuration, CancellationToken cancellationToken)
    {
        var monitor = MonitorManager.GetMonitors().FirstOrDefault(m => m.Key == monitorKey);
        if (monitor is null) return false;
        var off = await Task.Run(() => DdcCi.SetPower(monitor.Handle, DdcCi.PowerOff), cancellationToken).ConfigureAwait(false);
        try
        {
            await Task.Delay(offDuration, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            await Task.Run(() => PowerOn(monitorKey)).ConfigureAwait(false);
        }
        return off;
    }

    protected override async Task RefreshCoreAsync(PeripheralSnapshot peripherals, CancellationToken cancellationToken)
    {
        var monitors = MonitorManager.GetMonitors();
        _monitors = monitors;

        // DDC probing is slow (and can stall on monitors that don't speak it). A "yes" is final; a "no" is
        // re-checked a few times because many monitors miss DDC/CI requests while busy (mode switches, wake-up).
        foreach (var m in monitors.Where(m => GetDdcSupport(m.Key) != DdcCi.Support.Supported &&
                                              _ddcAttempts.GetValueOrDefault(m.Key) < MaxDdcAttempts))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var support = await Task.Run(() => DdcCi.Probe(m.Handle), cancellationToken).ConfigureAwait(false);
            _ddcAttempts[m.Key] = _ddcAttempts.GetValueOrDefault(m.Key) + 1;
            _ddcSupport[m.Key] = support;
            Log.Info($"Monitor '{m.FriendlyName}' ({m.Key}) DDC/CI: {support} (attempt {_ddcAttempts[m.Key]})");
        }

        SetDevices(monitors.Select(m => new DeviceInfo(
            m.Key,
            m.FriendlyName,
            DeviceCategory.Monitor,
            Describe(m))).ToList());
        SetState(ProviderState.Ready, "Black overlay on every display, plus optional DDC/CI power-off (see Settings).");
    }

    private string Describe(MonitorInfo m)
    {
        var parts = new List<string> { $"{m.Width} × {m.Height}" };
        if (m.IsPrimary) parts.Add("Primary");
        parts.Add(GetDdcSupport(m.Key) switch
        {
            DdcCi.Support.Supported => "DDC/CI power-off available",
            DdcCi.Support.NotSupported => "Overlay only (no DDC/CI)",
            _ => "Overlay",
        });
        return string.Join(" · ", parts);
    }

    protected override Task BlackoutCoreAsync(BlackoutContext context, CancellationToken cancellationToken)
    {
        // Overlays were already shown synchronously by the orchestrator; here we only do the slow hardware part.
        var targets = _monitors
            .Where(m => context.IsIncluded(m.Key) && context.DdcPowerOffMonitors.Contains(m.Key))
            .ToList();
        foreach (var m in targets)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (DdcCi.SetPower(m.Handle, DdcCi.PowerOff))
            {
                lock (_poweredOff) _poweredOff.Add(m.Key);
            }
            else
            {
                ReportError($"'{m.FriendlyName}' did not accept the DDC/CI power-off command");
            }
        }
        return Task.CompletedTask;
    }

    protected override Task RestoreCoreAsync(CancellationToken cancellationToken)
    {
        string[] keys;
        lock (_poweredOff)
        {
            keys = [.. _poweredOff];
            _poweredOff.Clear();
        }
        var allOk = true;
        foreach (var key in keys) allOk &= PowerOn(key);
        if (!allOk) WakeAllDisplays();
        return Task.CompletedTask;
    }

    // Overlays are hidden by the orchestrator; here only the hardware needs turning back on.
    protected override void EmergencyRestoreCore() => RestoreCoreAsync(CancellationToken.None).GetAwaiter().GetResult();

    private static bool PowerOn(string monitorKey)
    {
        // The HMONITOR may have changed while the panel was off, so look it up again by its stable key.
        var monitor = MonitorManager.GetMonitors().FirstOrDefault(m => m.Key == monitorKey);
        if (monitor is null)
        {
            Log.Warn($"Monitor {monitorKey} not present during restore");
            return false;
        }
        return DdcCi.SetPower(monitor.Handle, DdcCi.PowerOn);
    }

    /// <summary>Fallback: ask Windows to power displays on (works for monitors that dropped off the bus).</summary>
    private static void WakeAllDisplays()
    {
        Log.Info("Sending SC_MONITORPOWER on as a fallback");
        PostMessageW(new IntPtr(0xFFFF), 0x0112 /*WM_SYSCOMMAND*/, new IntPtr(0xF170) /*SC_MONITORPOWER*/, new IntPtr(-1));
        // A zero-distance mouse move also counts as user input for waking displays.
        var input = new INPUT { type = 0, mi = new MOUSEINPUT { dwFlags = 0x0001 /*MOUSEEVENTF_MOVE*/ } };
        SendInput(1, [input], Marshal.SizeOf<INPUT>());
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MOUSEINPUT
    {
        public int dx, dy;
        public uint mouseData, dwFlags, time;
        public IntPtr dwExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct INPUT
    {
        public uint type;
        public MOUSEINPUT mi;
        // Padding so the struct matches sizeof(INPUT) on x64 (the union's largest member is MOUSEINPUT).
    }

    [DllImport("user32.dll")]
    private static extern bool PostMessageW(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint SendInput(uint count, INPUT[] inputs, int size);
}
