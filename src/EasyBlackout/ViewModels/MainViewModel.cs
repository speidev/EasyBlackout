using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Windows.Input;
using System.Windows.Threading;
using EasyBlackout.Core.Blackout;
using EasyBlackout.Core.Hotkeys;
using EasyBlackout.Core.Logging;
using EasyBlackout.Core.Monitors;
using EasyBlackout.Core.Startup;
using EasyBlackout.Core.Settings;
using EasyBlackout.Hotkeys;

namespace EasyBlackout.ViewModels;

public sealed class MainViewModel : ObservableObject
{
    private readonly BlackoutOrchestrator _orchestrator;
    private readonly SettingsStore _settings;
    private readonly MonitorProvider _monitors;
    private readonly GlobalHotkey _hotkey;
    private readonly Func<Task> _toggle;
    private readonly Dispatcher _dispatcher;
    private int _syncQueued;

    private bool _isBlackedOut;
    private string _statusTitle = "";
    private string _statusSubtitle = "";
    private string _hotkeyText = "";
    private string? _hotkeyError;
    private string? _applyError; // why the last attempted shortcut was rejected
    private bool _isRefreshing;
    private bool _showSettings;

    internal MainViewModel(BlackoutOrchestrator orchestrator, SettingsStore settings, MonitorProvider monitors,
        GlobalHotkey hotkey, Func<Task> toggle, Dispatcher dispatcher)
    {
        _orchestrator = orchestrator;
        _settings = settings;
        _monitors = monitors;
        _hotkey = hotkey;
        _toggle = toggle;
        _dispatcher = dispatcher;

        foreach (var provider in orchestrator.Providers)
            (provider.Kind == Core.Providers.ProviderKind.Monitors ? MonitorGroups : LightingGroups)
                .Add(new ProviderGroupViewModel(provider, settings));

        ToggleCommand = new RelayCommand(async () => await _toggle());
        RefreshCommand = new RelayCommand(async () => await RefreshAsync(), () => !IsRefreshing && !IsBlackedOut);
        OpenLogsCommand = new RelayCommand(() => OpenFolder(Log.Directory));
        OpenSettingsFolderCommand = new RelayCommand(() => OpenFolder(Path.GetDirectoryName(_settings.FilePath)!));
        ResetHotkeyCommand = new RelayCommand(() => ApplyHotkey(HotkeyBinding.Default));
        ShowDevicesCommand = new RelayCommand(() => ShowSettings = false);
        ShowSettingsCommand = new RelayCommand(() => ShowSettings = true);

        orchestrator.Changed += (_, _) => QueueSync();
        settings.Changed += (_, _) => QueueSync();
        hotkey.RegistrationChanged += (_, _) => QueueSync();
        Sync();
    }

    public ObservableCollection<ProviderGroupViewModel> MonitorGroups { get; } = [];
    public ObservableCollection<ProviderGroupViewModel> LightingGroups { get; } = [];
    public ObservableCollection<MonitorDdcViewModel> DdcMonitors { get; } = [];

    public ICommand ToggleCommand { get; }
    public ICommand RefreshCommand { get; }
    public ICommand OpenLogsCommand { get; }
    public ICommand OpenSettingsFolderCommand { get; }
    public ICommand ResetHotkeyCommand { get; }
    public ICommand ShowDevicesCommand { get; }
    public ICommand ShowSettingsCommand { get; }

    public string Version { get; } =
        "Version " + (Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "1.0.0");

    public bool IsBlackedOut
    {
        get => _isBlackedOut;
        private set { if (Set(ref _isBlackedOut, value)) OnPropertyChanged(nameof(ToggleText)); }
    }

    public string ToggleText => IsBlackedOut ? "Restore" : "Black out now";
    public string StatusTitle { get => _statusTitle; private set => Set(ref _statusTitle, value); }
    public string StatusSubtitle { get => _statusSubtitle; private set => Set(ref _statusSubtitle, value); }
    public string HotkeyText { get => _hotkeyText; private set => Set(ref _hotkeyText, value); }

    public string[] HotkeyParts => HotkeyText.Split('+');

    public string? HotkeyError
    {
        get => _hotkeyError;
        private set { if (Set(ref _hotkeyError, value)) OnPropertyChanged(nameof(HasHotkeyError)); }
    }

    public bool HasHotkeyError => HotkeyError is not null;

    public bool IsRefreshing
    {
        get => _isRefreshing;
        private set { if (Set(ref _isRefreshing, value)) CommandManager.InvalidateRequerySuggested(); }
    }

    public bool ShowSettings
    {
        get => _showSettings;
        set { if (Set(ref _showSettings, value)) OnPropertyChanged(nameof(ShowDevices)); }
    }

    public bool ShowDevices
    {
        get => !_showSettings;
        set => ShowSettings = !value;
    }

    public bool StartWithWindows
    {
        get => StartupRegistration.IsEnabled();
        set
        {
            try
            {
                StartupRegistration.SetEnabled(value, Environment.ProcessPath!);
            }
            catch (Exception ex)
            {
                Log.Error("Changing startup registration failed", ex);
            }
            OnPropertyChanged();
        }
    }

    public bool ShowNotifications
    {
        get => _settings.Current.ShowNotifications;
        set
        {
            _settings.Update(s => s.ShowNotifications = value);
            OnPropertyChanged();
        }
    }

    /// <summary>Validates, registers and persists a new hotkey. Keeps the previous one if Windows rejects it.</summary>
    public void ApplyHotkey(HotkeyBinding binding)
    {
        var invalid = binding.Validate();
        if (invalid is not null)
        {
            _applyError = invalid;
            _hotkey.Resume();
            Sync();
            return;
        }

        var previous = _hotkey.Binding;
        _hotkey.Register(binding);
        if (!_hotkey.IsRegistered)
        {
            var error = _hotkey.Error;
            if (previous is { } p && p != binding) _hotkey.Register(p);
            _applyError = error;
            Sync();
            return;
        }

        _applyError = null;
        _settings.Update(s => s.Hotkey = binding.ToString());
        Sync();
    }

    public void BeginHotkeyRecording() => _hotkey.Suspend();

    public void EndHotkeyRecording() => _hotkey.Resume();

    public async Task RefreshAsync()
    {
        if (IsRefreshing) return;
        IsRefreshing = true;
        try
        {
            await _orchestrator.RefreshAsync();
        }
        finally
        {
            IsRefreshing = false;
            Sync();
        }
    }

    private void QueueSync()
    {
        if (Interlocked.Exchange(ref _syncQueued, 1) == 1) return;
        _dispatcher.BeginInvoke(DispatcherPriority.Background, () =>
        {
            Interlocked.Exchange(ref _syncQueued, 0);
            Sync();
        });
    }

    private void Sync()
    {
        var settings = _settings.Current;
        foreach (var g in MonitorGroups.Concat(LightingGroups)) g.Sync(settings);

        IsBlackedOut = _orchestrator.IsBlackedOut;
        HotkeyText = (_hotkey.Binding ?? HotkeyBinding.ParseOrDefault(settings.Hotkey)).ToString();
        OnPropertyChanged(nameof(HotkeyParts));
        HotkeyError = _applyError ?? _hotkey.Error;

        var monitors = MonitorGroups.Sum(g => g.ActiveDeviceCount);
        var lighting = LightingGroups.Sum(g => g.ActiveDeviceCount);
        if (IsBlackedOut)
        {
            StatusTitle = "Blacked out";
            StatusSubtitle = $"Press {HotkeyText} to restore everything.";
        }
        else
        {
            StatusTitle = _hotkey.IsRegistered ? "Ready" : "Hotkey unavailable";
            StatusSubtitle = $"{Plural(monitors, "display")} and {Plural(lighting, "lighting device")} will go dark"
                             + (_hotkey.IsRegistered ? $" when you press {HotkeyText}." : ".");
        }

        SyncDdcMonitors(settings);
    }

    private void SyncDdcMonitors(AppSettings settings)
    {
        var monitors = _monitors.Monitors;
        var byKey = DdcMonitors.ToDictionary(m => m.Key);
        var keep = new HashSet<string>();
        for (var i = 0; i < monitors.Count; i++)
        {
            var m = monitors[i];
            keep.Add(m.Key);
            if (!byKey.TryGetValue(m.Key, out var vm))
            {
                vm = new MonitorDdcViewModel(m.Key, _settings, _monitors);
                DdcMonitors.Insert(Math.Min(i, DdcMonitors.Count), vm);
            }
            vm.Sync(m, settings);
        }
        for (var i = DdcMonitors.Count - 1; i >= 0; i--)
            if (!keep.Contains(DdcMonitors[i].Key)) DdcMonitors.RemoveAt(i);
    }

    private static string Plural(int n, string noun) => n == 1 ? $"1 {noun}" : $"{n} {noun}s";

    private static void OpenFolder(string path)
    {
        try
        {
            Directory.CreateDirectory(path);
            Process.Start(new ProcessStartInfo("explorer.exe", $"\"{path}\"") { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            Log.Error($"Opening {path} failed", ex);
        }
    }
}
