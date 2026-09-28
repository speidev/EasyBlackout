using EasyBlackout.Core.Settings;

namespace EasyBlackout.Tests;

public sealed class SettingsStoreTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "EasyBlackoutTests", Guid.NewGuid().ToString("N"));
    private string FilePath => Path.Combine(_dir, "settings.json");

    [Fact]
    public void MissingFileGivesDefaults()
    {
        var store = new SettingsStore(FilePath);
        Assert.Equal("Ctrl+Alt+B", store.Current.Hotkey);
        Assert.True(store.Current.ShowNotifications);
        Assert.Empty(store.Current.ExcludedDevices);
    }

    [Fact]
    public void UpdatesPersistAndReload()
    {
        var store = new SettingsStore(FilePath);
        var changed = 0;
        store.Changed += (_, _) => changed++;
        store.Update(s =>
        {
            s.Hotkey = "Ctrl+Shift+F12";
            s.ExcludedDevices.Add("corsair:ABC");
            s.DisabledProviders.Add("razer");
            s.DdcPowerOffMonitors.Add("monitor:X");
            s.ShowNotifications = false;
        });
        Assert.Equal(1, changed);

        var reloaded = new SettingsStore(FilePath).Current;
        Assert.Equal("Ctrl+Shift+F12", reloaded.Hotkey);
        Assert.False(reloaded.ShowNotifications);
        Assert.Contains("monitor:X", reloaded.DdcPowerOffMonitors);
        Assert.Contains("razer", reloaded.DisabledProviders);
        // Comparer must survive a JSON round trip.
        Assert.Contains("CORSAIR:abc", reloaded.ExcludedDevices);
    }

    [Fact]
    public void CurrentIsASnapshot()
    {
        var store = new SettingsStore(FilePath);
        store.Current.ExcludedDevices.Add("x");
        Assert.Empty(store.Current.ExcludedDevices);
    }

    [Fact]
    public void CorruptFileFallsBackAndKeepsBackup()
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllText(FilePath, "{ not json");
        var store = new SettingsStore(FilePath);
        Assert.Equal("Ctrl+Alt+B", store.Current.Hotkey);
        Assert.True(File.Exists(FilePath + ".bad"));
    }

    [Fact]
    public void InvalidHotkeyInFileIsReplaced()
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllText(FilePath, """{ "Hotkey": "Ctrl+Nope" }""");
        Assert.Equal("Ctrl+Alt+B", new SettingsStore(FilePath).Current.Hotkey);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* temp */ }
    }
}
