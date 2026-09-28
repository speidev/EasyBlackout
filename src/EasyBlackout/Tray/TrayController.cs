using System.Windows;
using System.Windows.Controls;
using H.NotifyIcon;
using H.NotifyIcon.Core;
using EasyBlackout.Core.Logging;

namespace EasyBlackout.Tray;

internal sealed class TrayController : IDisposable
{
    private readonly TaskbarIcon _icon;
    private readonly System.Drawing.Icon _normalIcon = LoadIcon("EasyBlackout.ico");
    private readonly System.Drawing.Icon _activeIcon = LoadIcon("EasyBlackoutActive.ico");
    private readonly MenuItem _toggleItem;
    private readonly MenuItem _startupItem;
    private bool? _lastActive;

    public TrayController(Action toggle, Action open, Action openSettings, Func<bool> getStartup, Action<bool> setStartup, Action exit)
    {
        _toggleItem = new MenuItem { Header = "Black out now", FontWeight = FontWeights.SemiBold };
        _toggleItem.Click += (_, _) => toggle();

        var openItem = new MenuItem { Header = "Open EasyBlackout" };
        openItem.Click += (_, _) => open();

        var settingsItem = new MenuItem { Header = "Settings…" };
        settingsItem.Click += (_, _) => openSettings();

        _startupItem = new MenuItem { Header = "Start with Windows", IsCheckable = true };
        _startupItem.Click += (_, _) => setStartup(_startupItem.IsChecked);

        var logsItem = new MenuItem { Header = "View logs" };
        logsItem.Click += (_, _) =>
        {
            try { System.Diagnostics.Process.Start("explorer.exe", $"\"{Log.Directory}\""); }
            catch (Exception ex) { Log.Error("Opening logs failed", ex); }
        };

        var exitItem = new MenuItem { Header = "Exit" };
        exitItem.Click += (_, _) => exit();

        var menu = new ContextMenu();
        menu.Items.Add(_toggleItem);
        menu.Items.Add(new Separator());
        menu.Items.Add(openItem);
        menu.Items.Add(settingsItem);
        menu.Items.Add(_startupItem);
        menu.Items.Add(logsItem);
        menu.Items.Add(new Separator());
        menu.Items.Add(exitItem);
        menu.Opened += (_, _) => _startupItem.IsChecked = getStartup();

        _icon = new TaskbarIcon
        {
            Icon = (System.Drawing.Icon)_normalIcon.Clone(),
            ToolTipText = "EasyBlackout",
            ContextMenu = menu,
            MenuActivation = PopupActivationMode.RightClick,
            NoLeftClickDelay = true,
        };
        _icon.TrayLeftMouseUp += (_, _) => open();
        _icon.ForceCreate(enablesEfficiencyMode: false);
    }

    public void Update(bool blackedOut, string hotkey, bool hotkeyRegistered)
    {
        _toggleItem.Header = blackedOut ? "Restore" : "Black out now";
        _toggleItem.InputGestureText = hotkeyRegistered ? hotkey : "";
        _icon.ToolTipText = blackedOut
            ? $"EasyBlackout: blacked out. Press {hotkey} to restore."
            : hotkeyRegistered ? $"EasyBlackout: ready ({hotkey})" : "EasyBlackout: hotkey unavailable";
        if (_lastActive != blackedOut)
        {
            // TaskbarIcon disposes the icon it replaces, so always hand it a copy.
            _icon.Icon = (System.Drawing.Icon)(blackedOut ? _activeIcon : _normalIcon).Clone();
            _lastActive = blackedOut;
        }
    }

    public void Notify(string title, string message, bool warning = false)
    {
        try
        {
            _icon.ShowNotification(title, message, warning ? NotificationIcon.Warning : NotificationIcon.Info);
        }
        catch (Exception ex)
        {
            Log.Warn($"Tray notification failed: {ex.Message}");
        }
    }

    private static System.Drawing.Icon LoadIcon(string name)
    {
        var stream = Application.GetResourceStream(new Uri($"pack://application:,,,/Assets/{name}"))!.Stream;
        // Pick the small-icon size for the current DPI so the tray icon stays crisp.
        var size = GetSystemMetrics(49 /*SM_CXSMICON*/);
        return new System.Drawing.Icon(stream, size > 0 ? size : 16, size > 0 ? size : 16);
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern int GetSystemMetrics(int index);

    public void Dispose()
    {
        _icon.Dispose();
        _normalIcon.Dispose();
        _activeIcon.Dispose();
    }
}
