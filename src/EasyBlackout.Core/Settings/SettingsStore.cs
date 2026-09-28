using System.Text.Json;
using EasyBlackout.Core.Hotkeys;
using EasyBlackout.Core.Logging;

namespace EasyBlackout.Core.Settings;

/// <summary>Loads and atomically saves <see cref="AppSettings"/> as JSON.</summary>
public sealed class SettingsStore
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    private readonly object _sync = new();
    private AppSettings _current;

    public SettingsStore(string? filePath = null)
    {
        FilePath = filePath ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "EasyBlackout", "settings.json");
        _current = Load();
    }

    public string FilePath { get; }

    public event EventHandler? Changed;

    /// <summary>A snapshot copy; mutate through <see cref="Update"/>.</summary>
    public AppSettings Current
    {
        get { lock (_sync) return _current.Clone(); }
    }

    public void Update(Action<AppSettings> mutate)
    {
        lock (_sync)
        {
            var next = _current.Clone();
            mutate(next);
            _current = next;
            Save(next);
        }
        Changed?.Invoke(this, EventArgs.Empty);
    }

    private AppSettings Load()
    {
        try
        {
            if (File.Exists(FilePath))
            {
                var loaded = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(FilePath), JsonOptions);
                if (loaded is not null)
                {
                    // Clone() rebuilds the sets with the case-insensitive comparer System.Text.Json drops.
                    var normalized = loaded.Clone();
                    if (!HotkeyBinding.TryParse(normalized.Hotkey, out _)) normalized.Hotkey = HotkeyBinding.Default.ToString();
                    return normalized;
                }
            }
        }
        catch (Exception ex)
        {
            Log.Error($"Settings file '{FilePath}' is unreadable; using defaults", ex);
            try { File.Copy(FilePath, FilePath + ".bad", overwrite: true); } catch { /* keep going */ }
        }
        return new AppSettings();
    }

    private void Save(AppSettings settings)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            var temp = FilePath + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(settings, JsonOptions));
            File.Move(temp, FilePath, overwrite: true);
        }
        catch (Exception ex)
        {
            Log.Error("Failed to save settings", ex);
        }
    }
}
