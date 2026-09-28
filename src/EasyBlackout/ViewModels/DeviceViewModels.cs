using System.Collections.ObjectModel;
using System.Windows.Input;
using EasyBlackout.Core.Blackout;
using EasyBlackout.Core.Monitors;
using EasyBlackout.Core.Providers;
using EasyBlackout.Core.Settings;

namespace EasyBlackout.ViewModels;

public sealed class ProviderGroupViewModel : ObservableObject
{
    private readonly IBlackoutProvider _provider;
    private readonly SettingsStore _settings;
    private bool _isEnabled;
    private ProviderState _state;
    private string _stateText = "";
    private string? _detail;
    private string? _error;
    private string _summary = "";

    public ProviderGroupViewModel(IBlackoutProvider provider, SettingsStore settings)
    {
        _provider = provider;
        _settings = settings;
        Name = provider.DisplayName;
        IsMonitors = provider.Kind == ProviderKind.Monitors;
    }

    public string Id => _provider.Id;
    public string Name { get; }
    public bool IsMonitors { get; }
    public string Glyph => IsMonitors ? "" : "";

    public ObservableCollection<DeviceRowViewModel> Devices { get; } = [];

    public ProviderState State { get => _state; private set => Set(ref _state, value); }
    public string StateText { get => _stateText; private set => Set(ref _stateText, value); }
    public string? Detail { get => _detail; private set { if (Set(ref _detail, value)) OnPropertyChanged(nameof(HasDetail)); } }
    public bool HasDetail => !string.IsNullOrWhiteSpace(Detail);
    public string? Error { get => _error; private set { if (Set(ref _error, value)) OnPropertyChanged(nameof(HasError)); } }
    public bool HasError => Error is not null;
    public string Summary { get => _summary; private set => Set(ref _summary, value); }
    public bool HasDevices => Devices.Count > 0;

    /// <summary>Two-way: persisted immediately.</summary>
    public bool IsEnabled
    {
        get => _isEnabled;
        set
        {
            if (!Set(ref _isEnabled, value)) return;
            _settings.Update(s =>
            {
                if (value) s.DisabledProviders.Remove(Id);
                else s.DisabledProviders.Add(Id);
            });
        }
    }

    internal void Sync(AppSettings settings)
    {
        var enabled = !settings.DisabledProviders.Contains(Id);
        if (_isEnabled != enabled)
        {
            _isEnabled = enabled;
            OnPropertyChanged(nameof(IsEnabled));
        }

        State = _provider.State;
        StateText = enabled ? StatusResolver.Describe(_provider.State) : "Off";
        Detail = _provider.StatusDetail;
        Error = _provider.LastError;

        var devices = _provider.Devices;
        var byKey = Devices.ToDictionary(d => d.Key);
        var keep = new HashSet<string>();
        for (var i = 0; i < devices.Count; i++)
        {
            var info = devices[i];
            keep.Add(info.Key);
            if (!byKey.TryGetValue(info.Key, out var row))
            {
                row = new DeviceRowViewModel(info.Key, _settings);
                Devices.Insert(Math.Min(i, Devices.Count), row);
            }
            var included = !settings.ExcludedDevices.Contains(info.Key);
            row.Sync(info, StatusResolver.Resolve(_provider, info, enabled, included), included, enabled);
        }
        for (var i = Devices.Count - 1; i >= 0; i--)
            if (!keep.Contains(Devices[i].Key)) Devices.RemoveAt(i);

        var active = Devices.Count(d => d.Status is DeviceStatus.Ready or DeviceStatus.BlackedOut);
        Summary = Devices.Count switch
        {
            0 => "No devices",
            _ when active == Devices.Count => Devices.Count == 1 ? "1 device" : $"{Devices.Count} devices",
            _ => $"{active} of {Devices.Count} active",
        };
        OnPropertyChanged(nameof(HasDevices));
    }

    internal int ActiveDeviceCount => Devices.Count(d => d.Status is DeviceStatus.Ready or DeviceStatus.BlackedOut);
}

public sealed class DeviceRowViewModel : ObservableObject
{
    private readonly SettingsStore _settings;
    private string _name = "";
    private string? _detail;
    private string _glyph = "";
    private DeviceStatus _status;
    private bool _isIncluded;
    private bool _canToggle;

    public DeviceRowViewModel(string key, SettingsStore settings)
    {
        Key = key;
        _settings = settings;
    }

    public string Key { get; }
    public string Name { get => _name; private set => Set(ref _name, value); }
    public string? Detail { get => _detail; private set => Set(ref _detail, value); }
    public string Glyph { get => _glyph; private set => Set(ref _glyph, value); }
    public string StatusText => StatusResolver.Describe(Status);
    public bool CanToggle { get => _canToggle; private set => Set(ref _canToggle, value); }

    public DeviceStatus Status
    {
        get => _status;
        private set { if (Set(ref _status, value)) OnPropertyChanged(nameof(StatusText)); }
    }

    public bool IsIncluded
    {
        get => _isIncluded;
        set
        {
            if (!Set(ref _isIncluded, value)) return;
            _settings.Update(s =>
            {
                if (value) s.ExcludedDevices.Remove(Key);
                else s.ExcludedDevices.Add(Key);
            });
        }
    }

    internal void Sync(DeviceInfo info, DeviceStatus status, bool included, bool providerEnabled)
    {
        Name = info.Name;
        Detail = info.Detail;
        Glyph = GlyphFor(info.Category);
        Status = status;
        CanToggle = info.SupportsSelection && providerEnabled;
        if (_isIncluded != included)
        {
            _isIncluded = included;
            OnPropertyChanged(nameof(IsIncluded));
        }
    }

    private static string GlyphFor(DeviceCategory category) => category switch
    {
        DeviceCategory.Monitor => "",
        DeviceCategory.Keyboard or DeviceCategory.Keypad => "",
        DeviceCategory.Mouse => "",
        DeviceCategory.Mousepad => "",
        DeviceCategory.Headset or DeviceCategory.HeadsetStand => "",
        DeviceCategory.Speaker => "",
        DeviceCategory.Controller => "",
        DeviceCategory.Memory or DeviceCategory.Motherboard or DeviceCategory.GraphicsCard => "",
        DeviceCategory.Case => "",
        _ => "",
    };
}

public sealed class MonitorDdcViewModel : ObservableObject
{
    private readonly SettingsStore _settings;
    private readonly MonitorProvider _monitors;
    private string _name = "";
    private DdcCi.Support _support;
    private bool _isEnabled;
    private bool _isTesting;
    private string? _testResult;

    public MonitorDdcViewModel(string key, SettingsStore settings, MonitorProvider monitors)
    {
        Key = key;
        _settings = settings;
        _monitors = monitors;
        TestCommand = new RelayCommand(async () => await TestAsync(), () => !IsTesting && IsSupported);
    }

    public string Key { get; }
    public ICommand TestCommand { get; }
    public string Name { get => _name; private set => Set(ref _name, value); }

    public bool IsSupported => _support == DdcCi.Support.Supported;

    public string SupportText => _support switch
    {
        DdcCi.Support.Supported => "Supports DDC/CI power control",
        DdcCi.Support.NotSupported => "Doesn't answer DDC/CI. Enable DDC/CI in the monitor's on-screen menu to use this.",
        _ => "Checking…",
    };

    public bool IsTesting
    {
        get => _isTesting;
        private set { if (Set(ref _isTesting, value)) CommandManager.InvalidateRequerySuggested(); }
    }

    public string? TestResult { get => _testResult; private set => Set(ref _testResult, value); }

    public bool IsEnabled
    {
        get => _isEnabled;
        set
        {
            if (!Set(ref _isEnabled, value)) return;
            _settings.Update(s =>
            {
                if (value) s.DdcPowerOffMonitors.Add(Key);
                else s.DdcPowerOffMonitors.Remove(Key);
            });
        }
    }

    internal void Sync(MonitorInfo monitor, AppSettings settings)
    {
        Name = monitor.FriendlyName + (monitor.IsPrimary ? " (primary)" : "");
        var support = _monitors.GetDdcSupport(monitor.Key);
        if (_support != support)
        {
            _support = support;
            OnPropertyChanged(nameof(IsSupported));
            OnPropertyChanged(nameof(SupportText));
            CommandManager.InvalidateRequerySuggested();
        }
        var enabled = settings.DdcPowerOffMonitors.Contains(Key);
        if (_isEnabled != enabled)
        {
            _isEnabled = enabled;
            OnPropertyChanged(nameof(IsEnabled));
        }
    }

    private async Task TestAsync()
    {
        IsTesting = true;
        TestResult = "Turning the panel off for 3 seconds…";
        try
        {
            var ok = await _monitors.TestDdcAsync(Key, TimeSpan.FromSeconds(3), CancellationToken.None);
            TestResult = ok
                ? "Monitor accepted the command. If the panel went dark and came back, you're good to enable this."
                : "Monitor didn't accept the power-off command. Keep this off; the overlay still blacks it out.";
        }
        catch (Exception ex)
        {
            TestResult = $"Test failed: {ex.Message}";
        }
        finally
        {
            IsTesting = false;
        }
    }
}
