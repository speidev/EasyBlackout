using System.Runtime.InteropServices;
using System.Windows.Interop;
using EasyBlackout.Core.Hotkeys;
using EasyBlackout.Core.Logging;
using static EasyBlackout.Interop.NativeMethods;

namespace EasyBlackout.Hotkeys;

/// <summary>A system-wide hotkey registered on a hidden message-only window.</summary>
internal sealed class GlobalHotkey : IDisposable
{
    private const int HotkeyId = 0xB1AC;
    private readonly HwndSource _source;
    private bool _suspended;

    public GlobalHotkey()
    {
        _source = new HwndSource(new HwndSourceParameters("EasyBlackoutHotkey")
        {
            ParentWindow = HWND_MESSAGE,
            WindowStyle = 0,
            Width = 0,
            Height = 0,
        });
        _source.AddHook(WndProc);
    }

    public HotkeyBinding? Binding { get; private set; }

    public bool IsRegistered { get; private set; }

    /// <summary>User-facing reason the last registration failed, or null.</summary>
    public string? Error { get; private set; }

    public event EventHandler? Pressed;

    public event EventHandler? RegistrationChanged;

    public bool Register(HotkeyBinding binding)
    {
        UnregisterNative();
        _suspended = false; // an explicit registration ends any recording session
        Binding = binding;
        Error = binding.Validate();
        if (Error is null && !_suspended) RegisterNative();
        RegistrationChanged?.Invoke(this, EventArgs.Empty);
        return Error is null;
    }

    /// <summary>Temporarily releases the hotkey (while the user records a new one).</summary>
    public void Suspend()
    {
        _suspended = true;
        UnregisterNative();
    }

    public void Resume()
    {
        if (!_suspended) return;
        _suspended = false;
        if (Binding is { } b && b.Validate() is null)
        {
            RegisterNative();
            RegistrationChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    private void RegisterNative()
    {
        var b = Binding!.Value;
        if (RegisterHotKey(_source.Handle, HotkeyId, (uint)b.Modifiers | MOD_NOREPEAT, (uint)b.VirtualKey))
        {
            IsRegistered = true;
            Error = null;
            Log.Info($"Hotkey registered: {b}");
            return;
        }

        var code = Marshal.GetLastWin32Error();
        IsRegistered = false;
        Error = code == ERROR_HOTKEY_ALREADY_REGISTERED
            ? $"{b} is already used by another app or by Windows. Pick a different shortcut."
            : $"Windows refused {b} (error {code}).";
        Log.Warn($"Hotkey registration failed: {Error}");
    }

    private void UnregisterNative()
    {
        if (!IsRegistered) return;
        UnregisterHotKey(_source.Handle, HotkeyId);
        IsRegistered = false;
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WM_HOTKEY && wParam.ToInt32() == HotkeyId)
        {
            handled = true;
            Pressed?.Invoke(this, EventArgs.Empty);
        }
        return IntPtr.Zero;
    }

    public void Dispose()
    {
        UnregisterNative();
        _source.RemoveHook(WndProc);
        _source.Dispose();
    }
}
