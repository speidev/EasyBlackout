using System.Windows;
using System.Windows.Threading;
using EasyBlackout.Core.Blackout;
using EasyBlackout.Core.Hotkeys;
using EasyBlackout.Core.Logging;
using EasyBlackout.Core.Monitors;
using EasyBlackout.Core.Providers;
using EasyBlackout.Core.Providers.Corsair;
using EasyBlackout.Core.Providers.DynamicLighting;
using EasyBlackout.Core.Providers.HyperX;
using EasyBlackout.Core.Providers.Logitech;
using EasyBlackout.Core.Providers.OpenRgb;
using EasyBlackout.Core.Providers.Razer;
using EasyBlackout.Core.Providers.SteelSeries;
using EasyBlackout.Core.Settings;
using EasyBlackout.Core.Startup;
using EasyBlackout.Hotkeys;
using EasyBlackout.Overlay;
using EasyBlackout.Tray;
using EasyBlackout.ViewModels;
using EasyBlackout.Views;
using Microsoft.Win32;

namespace EasyBlackout;

public partial class App : Application
{
    private const string InstanceName = "EasyBlackout.SingleInstance.{6C1E2B64-3C3A-4E0B-9E0B-7B2B5E1D5A11}";
    private const string ActivateEventName = "EasyBlackout.Activate.{6C1E2B64-3C3A-4E0B-9E0B-7B2B5E1D5A11}";

    private Mutex? _instanceMutex;
    private EventWaitHandle? _activateEvent;
    private RegisteredWaitHandle? _activateWait;

    private SettingsStore _settings = null!;
    private OverlayManager _overlay = null!;
    private MonitorProvider _monitors = null!;
    private BlackoutOrchestrator _orchestrator = null!;
    private GlobalHotkey _hotkey = null!;
    private TrayController _tray = null!;
    private MainViewModel _vm = null!;
    private MainWindow? _window;
    private DispatcherTimer _refreshTimer = null!;
    private bool _exiting;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        Log.Initialize();

        if (!AcquireSingleInstance())
        {
            Shutdown();
            return;
        }

        Log.Info($"EasyBlackout {typeof(App).Assembly.GetName().Version} starting (args: {string.Join(' ', e.Args)})");
        HookCrashHandlers();

        _settings = new SettingsStore();
        _overlay = new OverlayManager(Dispatcher);
        _overlay.RestoreRequested += (_, _) => _ = ToggleAsync();
        _monitors = new MonitorProvider(_overlay);

        IBlackoutProvider[] providers =
        [
            _monitors,
            new CorsairProvider(),
            new RazerChromaProvider(),
            new LogitechProvider(),
            new SteelSeriesProvider(),
            new HyperXProvider(),
            new DynamicLightingProvider(),
            new OpenRgbProvider(),
        ];
        _orchestrator = new BlackoutOrchestrator(providers, _monitors, _settings);

        _hotkey = new GlobalHotkey();
        _hotkey.Pressed += (_, _) => _ = ToggleAsync();
        _hotkey.RegistrationChanged += (_, _) => _overlay.EscapeRestores = !_hotkey.IsRegistered;
        _hotkey.Register(HotkeyBinding.ParseOrDefault(_settings.Current.Hotkey));

        _vm = new MainViewModel(_orchestrator, _settings, _monitors, _hotkey, ToggleAsync, Dispatcher);

        _tray = new TrayController(
            toggle: () => _ = ToggleAsync(),
            open: () => ShowMainWindow(settings: false),
            openSettings: () => ShowMainWindow(settings: true),
            getStartup: StartupRegistration.IsEnabled,
            setStartup: enabled => _vm.StartWithWindows = enabled,
            exit: ExitApplication);

        _orchestrator.Changed += (_, _) => Dispatcher.BeginInvoke(UpdateTray);
        _hotkey.RegistrationChanged += (_, _) => UpdateTray();
        UpdateTray();

        SystemEvents.DisplaySettingsChanged += OnDisplaySettingsChanged;
        SystemEvents.PowerModeChanged += OnPowerModeChanged;
        SessionEnding += (_, _) => _orchestrator.EmergencyRestore();

        try { StartupRegistration.RepairPath(Environment.ProcessPath!); }
        catch (Exception ex) { Log.Warn($"Could not repair startup entry: {ex.Message}"); }

        // Keep device status fresh: often while the window is open, rarely in the background.
        _refreshTimer = new DispatcherTimer(TimeSpan.FromSeconds(60), DispatcherPriority.Background,
            (_, _) => _ = _orchestrator.RefreshAsync(), Dispatcher);
        _refreshTimer.Start();

        _ = InitialRefreshAsync();

        var startInTray = e.Args.Any(a => a.Equals(StartupRegistration.TrayArgument, StringComparison.OrdinalIgnoreCase));
        if (!startInTray) ShowMainWindow(settings: false);

        if (!_hotkey.IsRegistered)
            Notify("Hotkey unavailable", _hotkey.Error ?? "The blackout hotkey could not be registered.", warning: true, force: true);
    }

    private async Task InitialRefreshAsync()
    {
        await _orchestrator.RefreshAsync();
        _overlay.Prewarm(_monitors.Monitors);
    }

    private async Task ToggleAsync()
    {
        try
        {
            await _orchestrator.ToggleAsync();
        }
        catch (Exception ex)
        {
            Log.Error("Toggle failed", ex);
        }
    }

    private void UpdateTray()
    {
        if (_exiting) return;
        _tray.Update(_orchestrator.IsBlackedOut, (_hotkey.Binding ?? HotkeyBinding.Default).ToString(), _hotkey.IsRegistered);
    }

    private void ShowMainWindow(bool settings)
    {
        if (_window is null)
        {
            _window = new MainWindow(_vm);
            _window.HiddenToTray += (_, _) => OnHiddenToTray();
            _window.IsVisibleChanged += (_, _) =>
            {
                _refreshTimer.Interval = _window.IsVisible ? TimeSpan.FromSeconds(5) : TimeSpan.FromSeconds(60);
                if (_window.IsVisible) _ = _orchestrator.RefreshAsync();
            };
        }

        _vm.ShowSettings = settings;
        if (!_window.IsVisible) _window.Show();
        if (_window.WindowState == WindowState.Minimized) _window.WindowState = WindowState.Normal;
        _window.Activate();
        _window.Topmost = true;  // reliably bring to front from the tray
        _window.Topmost = false;
        _window.Focus();
    }

    private void OnHiddenToTray()
    {
        if (_settings.Current.HasShownTrayHint) return;
        _settings.Update(s => s.HasShownTrayHint = true);
        Notify("EasyBlackout is still running",
            $"Press {(_hotkey.Binding ?? HotkeyBinding.Default)} anytime. Right-click the tray icon for options.");
    }

    private void Notify(string title, string message, bool warning = false, bool force = false)
    {
        if (force || _settings.Current.ShowNotifications) _tray.Notify(title, message, warning);
    }

    private void OnDisplaySettingsChanged(object? sender, EventArgs e)
    {
        Dispatcher.BeginInvoke(async () =>
        {
            Log.Info("Display configuration changed");
            if (_orchestrator.IsBlackedOut)
            {
                _orchestrator.ReapplyOverlays();
            }
            else
            {
                await _orchestrator.RefreshAsync();
                _overlay.Prewarm(_monitors.Monitors);
            }
        });
    }

    private void OnPowerModeChanged(object sender, PowerModeChangedEventArgs e)
    {
        if (e.Mode != PowerModes.Resume) return;
        Dispatcher.BeginInvoke(async () =>
        {
            Log.Info("Resumed from sleep");
            // Vendor software re-initialises devices after resume; give it a moment, then re-assert.
            await Task.Delay(TimeSpan.FromSeconds(4));
            if (_orchestrator.IsBlackedOut) await _orchestrator.ReapplyAsync();
            else await _orchestrator.RefreshAsync();
        });
    }

    private void ExitApplication()
    {
        if (_exiting) return;
        _exiting = true;
        Log.Info("Exiting");
        _refreshTimer.Stop();
        SystemEvents.DisplaySettingsChanged -= OnDisplaySettingsChanged;
        SystemEvents.PowerModeChanged -= OnPowerModeChanged;

        _orchestrator.EmergencyRestore();
        try
        {
            // Disconnect SDKs (bounded so a hung vendor service can't keep us alive).
            Task.Run(async () => await _orchestrator.DisposeAsync()).Wait(TimeSpan.FromSeconds(3));
        }
        catch (Exception ex)
        {
            Log.Error("Shutdown cleanup failed", ex);
        }

        _hotkey.Dispose();
        _tray.Dispose();
        _overlay.Dispose();
        if (_window is not null)
        {
            _window.AllowClose = true;
            _window.Close();
        }
        Shutdown();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _activateWait?.Unregister(null);
        _activateEvent?.Dispose();
        if (_instanceMutex is not null)
        {
            try { _instanceMutex.ReleaseMutex(); } catch (ApplicationException) { /* not owned */ }
            _instanceMutex.Dispose();
        }
        base.OnExit(e);
    }

    /// <summary>One instance per user session; a second launch just brings the first one's window up.</summary>
    private bool AcquireSingleInstance()
    {
        _instanceMutex = new Mutex(initiallyOwned: true, InstanceName, out var created);
        _activateEvent = new EventWaitHandle(false, EventResetMode.AutoReset, ActivateEventName);
        if (!created)
        {
            _activateEvent.Set();
            _instanceMutex.Dispose();
            _instanceMutex = null;
            return false;
        }

        _activateWait = ThreadPool.RegisterWaitForSingleObject(_activateEvent,
            (_, _) => Dispatcher.BeginInvoke(() => ShowMainWindow(settings: false)), null, Timeout.Infinite, executeOnlyOnce: false);
        return true;
    }

    private void HookCrashHandlers()
    {
        // Whatever happens, never leave the user staring at black screens.
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
        {
            Log.Error("Unhandled exception (terminating)", args.ExceptionObject as Exception);
            _orchestrator?.EmergencyRestore();
        };
        DispatcherUnhandledException += (_, args) =>
        {
            Log.Error("Unhandled UI exception", args.Exception);
            args.Handled = true; // keep the tray app alive
        };
        TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            Log.Error("Unobserved task exception", args.Exception);
            args.SetObserved();
        };
    }
}
