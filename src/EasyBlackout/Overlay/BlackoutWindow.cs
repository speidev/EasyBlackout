using System.ComponentModel;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using EasyBlackout.Core.Monitors;
using static EasyBlackout.Interop.NativeMethods;

namespace EasyBlackout.Overlay;

/// <summary>A borderless, topmost, pure-black window covering one monitor. Never appears in the taskbar or Alt+Tab.</summary>
internal sealed class BlackoutWindow : Window
{
    private IntPtr _hwnd;
    private MonitorInfo? _monitor;

    public BlackoutWindow()
    {
        Title = "EasyBlackout";
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        Topmost = true;
        ShowActivated = false;
        Background = Brushes.Black;
        Cursor = Cursors.None;
        WindowStartupLocation = WindowStartupLocation.Manual;
        Left = -32000;
        Top = -32000;
        Width = 1;
        Height = 1;
        Focusable = true;

        SourceInitialized += OnSourceInitialized;
        _hwnd = new WindowInteropHelper(this).EnsureHandle();
    }

    public bool AllowClose { get; set; }

    public event EventHandler<KeyEventArgs>? OverlayKeyDown;

    public string? MonitorName => _monitor?.GdiDeviceName;

    private void OnSourceInitialized(object? sender, EventArgs e)
    {
        _hwnd = new WindowInteropHelper(this).Handle;
        var ex = (long)GetWindowLongPtr(_hwnd, GWL_EXSTYLE);
        ex = (ex | WS_EX_TOOLWINDOW) & ~(long)WS_EX_APPWINDOW;
        SetWindowLongPtr(_hwnd, GWL_EXSTYLE, new IntPtr(ex));
        HwndSource.FromHwnd(_hwnd)?.AddHook(WndProc);
    }

    private static IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        // Moving between monitors with different scaling makes WPF resize to a "suggested" rect.
        // We place the window in physical pixels ourselves, so ignore it.
        if (msg == WM_DPICHANGED) handled = true;
        return IntPtr.Zero;
    }

    /// <summary>Covers the given monitor exactly (physical pixels) and shows the window.</summary>
    public void Cover(MonitorInfo monitor)
    {
        _monitor = monitor;
        const uint flags = SWP_NOACTIVATE | SWP_NOOWNERZORDER | SWP_FRAMECHANGED;
        SetWindowPos(_hwnd, HWND_TOPMOST, monitor.Left, monitor.Top, monitor.Width, monitor.Height, flags);
        if (!IsVisible) Show();
        // Second pass in case showing on a different-DPI monitor nudged the size.
        SetWindowPos(_hwnd, HWND_TOPMOST, monitor.Left, monitor.Top, monitor.Width, monitor.Height, flags | SWP_SHOWWINDOW);
    }

    /// <summary>Keeps us above other always-on-top windows (taskbar, game overlays, notifications).</summary>
    public void ReassertTopmost()
    {
        if (IsVisible) SetWindowPos(_hwnd, HWND_TOPMOST, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE);
    }

    public void TakeFocus()
    {
        SetForegroundWindow(_hwnd);
        Activate();
        Focus();
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        OverlayKeyDown?.Invoke(this, e);
        e.Handled = true; // nothing typed while blacked out leaks anywhere
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        // Alt+F4 on the overlay must not break the blackout; only the manager may close it.
        if (!AllowClose) e.Cancel = true;
        base.OnClosing(e);
    }
}
