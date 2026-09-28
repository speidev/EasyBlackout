using System.ComponentModel;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using EasyBlackout.Core.Hotkeys;
using EasyBlackout.ViewModels;
using static EasyBlackout.Interop.NativeMethods;

namespace EasyBlackout.Views;

public partial class MainWindow : Window
{
    private readonly MainViewModel _vm;
    private bool _recording;

    public MainWindow(MainViewModel vm)
    {
        InitializeComponent();
        DataContext = _vm = vm;
        SourceInitialized += (_, _) => UseDarkTitleBar();
    }

    /// <summary>Set by the app when it is really exiting; otherwise closing just hides to the tray.</summary>
    public bool AllowClose { get; set; }

    public event EventHandler? HiddenToTray;

    protected override void OnClosing(CancelEventArgs e)
    {
        if (!AllowClose)
        {
            e.Cancel = true;
            Hide();
            HiddenToTray?.Invoke(this, EventArgs.Empty);
        }
        base.OnClosing(e);
    }

    private void UseDarkTitleBar()
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        var on = 1;
        DwmSetWindowAttribute(hwnd, DWMWA_USE_IMMERSIVE_DARK_MODE, ref on, sizeof(int));
    }

    private void HotkeyInput_GotFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        _recording = true;
        _vm.BeginHotkeyRecording();
        HotkeyInput.Text = "Press a shortcut…";
    }

    private void HotkeyInput_LostFocus(object sender, KeyboardFocusChangedEventArgs e) => EndRecording();

    private void EndRecording()
    {
        if (!_recording) return;
        _recording = false;
        _vm.EndHotkeyRecording();
        HotkeyInput.Text = _vm.HotkeyText;
    }

    private void HotkeyInput_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        e.Handled = true;
        var key = e.Key switch
        {
            Key.System => e.SystemKey,
            Key.ImeProcessed => e.ImeProcessedKey,
            _ => e.Key,
        };

        if (key is Key.Tab && Keyboard.Modifiers == ModifierKeys.None)
        {
            e.Handled = false; // keep keyboard navigation working
            return;
        }
        if (key is Key.Escape && Keyboard.Modifiers == ModifierKeys.None)
        {
            Keyboard.ClearFocus();
            return;
        }

        var modifiers = HotkeyModifiers.None;
        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Control)) modifiers |= HotkeyModifiers.Control;
        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Alt)) modifiers |= HotkeyModifiers.Alt;
        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Shift)) modifiers |= HotkeyModifiers.Shift;
        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Windows)) modifiers |= HotkeyModifiers.Windows;

        if (key is Key.LeftCtrl or Key.RightCtrl or Key.LeftAlt or Key.RightAlt or Key.LeftShift or Key.RightShift or Key.LWin or Key.RWin)
        {
            // Live preview while the user is still holding modifiers.
            var parts = new List<string>();
            if (modifiers.HasFlag(HotkeyModifiers.Control)) parts.Add("Ctrl");
            if (modifiers.HasFlag(HotkeyModifiers.Alt)) parts.Add("Alt");
            if (modifiers.HasFlag(HotkeyModifiers.Shift)) parts.Add("Shift");
            if (modifiers.HasFlag(HotkeyModifiers.Windows)) parts.Add("Win");
            HotkeyInput.Text = parts.Count == 0 ? "Press a shortcut…" : string.Join("+", parts) + "+…";
            return;
        }

        var binding = new HotkeyBinding(modifiers, KeyInterop.VirtualKeyFromKey(key));
        _recording = false; // ApplyHotkey re-registers (or resumes) the hotkey itself
        _vm.ApplyHotkey(binding);
        HotkeyInput.Text = _vm.HotkeyText;
        Keyboard.ClearFocus();
        FocusManager.SetFocusedElement(this, this);
    }
}
