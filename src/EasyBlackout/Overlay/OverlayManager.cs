using System.Windows.Input;
using System.Windows.Threading;
using EasyBlackout.Core.Logging;
using EasyBlackout.Core.Monitors;

namespace EasyBlackout.Overlay;

/// <summary>Owns one reusable <see cref="BlackoutWindow"/> per monitor. All work is marshalled to the UI thread.</summary>
internal sealed class OverlayManager : IOverlayHost, IDisposable
{
    private readonly Dispatcher _dispatcher;
    private readonly Dictionary<string, BlackoutWindow> _windows = new(StringComparer.OrdinalIgnoreCase);
    private readonly DispatcherTimer _topmostTimer;

    public OverlayManager(Dispatcher dispatcher)
    {
        _dispatcher = dispatcher;
        _topmostTimer = new DispatcherTimer(TimeSpan.FromMilliseconds(750), DispatcherPriority.Normal, (_, _) => Reassert(), dispatcher);
        _topmostTimer.Stop();
    }

    /// <summary>When true, Esc on an overlay requests a restore (used as a safety net if no hotkey is registered).</summary>
    public bool EscapeRestores { get; set; }

    public event EventHandler? RestoreRequested;

    public bool IsShowing { get; private set; }

    /// <summary>Creates the windows ahead of time so the first blackout is as fast as later ones.</summary>
    public void Prewarm(IEnumerable<MonitorInfo> monitors)
    {
        Run(() =>
        {
            foreach (var m in monitors) GetOrCreate(m.GdiDeviceName);
        });
    }

    public void Show(IReadOnlyList<MonitorInfo> monitors)
    {
        Run(() =>
        {
            var wanted = new HashSet<string>(monitors.Select(m => m.GdiDeviceName), StringComparer.OrdinalIgnoreCase);
            foreach (var m in monitors)
            {
                try { GetOrCreate(m.GdiDeviceName).Cover(m); }
                catch (Exception ex) { Log.Error($"Overlay for {m.FriendlyName} failed", ex); }
            }
            foreach (var (name, window) in _windows)
                if (!wanted.Contains(name) && window.IsVisible) window.Hide();

            // Foreground the primary overlay: keyboard input is swallowed and Windows hands us Dynamic Lighting control.
            var primary = monitors.FirstOrDefault(m => m.IsPrimary) ?? monitors.FirstOrDefault();
            if (primary is not null) _windows[primary.GdiDeviceName].TakeFocus();

            IsShowing = monitors.Count > 0;
            if (IsShowing) _topmostTimer.Start();
        });
    }

    public void Hide()
    {
        Run(() =>
        {
            _topmostTimer.Stop();
            foreach (var window in _windows.Values)
                if (window.IsVisible) window.Hide();
            IsShowing = false;
        });
    }

    private void Reassert()
    {
        foreach (var window in _windows.Values) window.ReassertTopmost();
    }

    private BlackoutWindow GetOrCreate(string gdiName)
    {
        if (_windows.TryGetValue(gdiName, out var window)) return window;
        window = new BlackoutWindow();
        window.OverlayKeyDown += OnOverlayKeyDown;
        _windows[gdiName] = window;
        return window;
    }

    private void OnOverlayKeyDown(object? sender, KeyEventArgs e)
    {
        if (EscapeRestores && e.Key == Key.Escape) RestoreRequested?.Invoke(this, EventArgs.Empty);
    }

    private void Run(Action action)
    {
        if (_dispatcher.CheckAccess()) action();
        // Bounded, so a wedged UI thread can never hang a crash/shutdown path.
        else _dispatcher.Invoke(action, DispatcherPriority.Send, CancellationToken.None, TimeSpan.FromSeconds(2));
    }

    public void Dispose()
    {
        Run(() =>
        {
            _topmostTimer.Stop();
            foreach (var window in _windows.Values)
            {
                window.AllowClose = true;
                window.Close();
            }
            _windows.Clear();
        });
    }
}
