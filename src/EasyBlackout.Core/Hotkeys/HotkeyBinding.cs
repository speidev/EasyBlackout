using System.Globalization;

namespace EasyBlackout.Core.Hotkeys;

[Flags]
public enum HotkeyModifiers
{
    None = 0,
    Alt = 0x0001,
    Control = 0x0002,
    Shift = 0x0004,
    Windows = 0x0008,
}

/// <summary>A global hotkey: modifier flags (Win32 MOD_* values) plus a Win32 virtual-key code.</summary>
public readonly record struct HotkeyBinding(HotkeyModifiers Modifiers, int VirtualKey)
{
    public static readonly HotkeyBinding Default = new(HotkeyModifiers.Control | HotkeyModifiers.Alt, 0x42); // Ctrl+Alt+B

    private static readonly Dictionary<int, string> KeyNames = BuildKeyNames();
    private static readonly Dictionary<string, int> KeyCodes =
        KeyNames.GroupBy(kv => kv.Value, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(g => g.Key, g => g.First().Key, StringComparer.OrdinalIgnoreCase);

    /// <summary>Virtual keys that are pure modifiers and can never be the main key.</summary>
    private static readonly HashSet<int> ModifierKeys =
        [0x10, 0x11, 0x12, 0x5B, 0x5C, 0xA0, 0xA1, 0xA2, 0xA3, 0xA4, 0xA5];

    /// <summary>Keys that are rarely typed, so they are allowed without any modifier.</summary>
    private static readonly HashSet<int> StandaloneKeys = BuildStandaloneKeys();

    public bool IsEmpty => VirtualKey == 0;

    /// <summary>Returns null when valid, otherwise a user-facing reason.</summary>
    public string? Validate()
    {
        if (VirtualKey == 0) return "Press a key.";
        if (ModifierKeys.Contains(VirtualKey)) return "Add a non-modifier key (e.g. Ctrl+Alt+B).";
        if (!KeyNames.ContainsKey(VirtualKey)) return "That key isn't supported for global hotkeys.";
        if (Modifiers == HotkeyModifiers.None && !StandaloneKeys.Contains(VirtualKey))
            return "Add at least one modifier (Ctrl, Alt, Shift or Win).";
        if (Modifiers == HotkeyModifiers.Shift && !StandaloneKeys.Contains(VirtualKey))
            return "Shift alone would interfere with typing — add Ctrl, Alt or Win.";
        return null;
    }

    public override string ToString()
    {
        if (IsEmpty) return "None";
        var parts = new List<string>(5);
        if (Modifiers.HasFlag(HotkeyModifiers.Control)) parts.Add("Ctrl");
        if (Modifiers.HasFlag(HotkeyModifiers.Alt)) parts.Add("Alt");
        if (Modifiers.HasFlag(HotkeyModifiers.Shift)) parts.Add("Shift");
        if (Modifiers.HasFlag(HotkeyModifiers.Windows)) parts.Add("Win");
        parts.Add(KeyNames.TryGetValue(VirtualKey, out var name) ? name : $"0x{VirtualKey:X2}");
        return string.Join("+", parts);
    }

    public static bool TryParse(string? text, out HotkeyBinding binding)
    {
        binding = default;
        if (string.IsNullOrWhiteSpace(text)) return false;

        var modifiers = HotkeyModifiers.None;
        int? key = null;
        // Split on '+' but allow "+" itself as a key name is not supported; "=" is used for that key.
        foreach (var raw in text.Split('+', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
        {
            switch (raw.ToUpperInvariant())
            {
                case "CTRL" or "CONTROL": modifiers |= HotkeyModifiers.Control; continue;
                case "ALT": modifiers |= HotkeyModifiers.Alt; continue;
                case "SHIFT": modifiers |= HotkeyModifiers.Shift; continue;
                case "WIN" or "WINDOWS" or "META": modifiers |= HotkeyModifiers.Windows; continue;
            }

            if (key is not null) return false; // two main keys
            if (KeyCodes.TryGetValue(raw, out var code)) key = code;
            else if (raw.StartsWith("0x", StringComparison.OrdinalIgnoreCase) &&
                     int.TryParse(raw.AsSpan(2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var hex))
                key = hex;
            else return false;
        }

        if (key is null) return false;
        binding = new HotkeyBinding(modifiers, key.Value);
        return true;
    }

    public static HotkeyBinding ParseOrDefault(string? text) => TryParse(text, out var b) ? b : Default;

    private static Dictionary<int, string> BuildKeyNames()
    {
        var map = new Dictionary<int, string>();
        for (var c = 'A'; c <= 'Z'; c++) map[c] = c.ToString();
        for (var d = 0; d <= 9; d++) map[0x30 + d] = d.ToString(CultureInfo.InvariantCulture);
        for (var f = 1; f <= 24; f++) map[0x6F + f] = $"F{f}";
        for (var n = 0; n <= 9; n++) map[0x60 + n] = $"Num{n}";
        map[0x6A] = "NumMultiply"; map[0x6B] = "NumAdd"; map[0x6D] = "NumSubtract";
        map[0x6E] = "NumDecimal"; map[0x6F] = "NumDivide";
        map[0x08] = "Backspace"; map[0x09] = "Tab"; map[0x0D] = "Enter"; map[0x13] = "Pause";
        map[0x14] = "CapsLock"; map[0x1B] = "Esc"; map[0x20] = "Space"; map[0x21] = "PageUp";
        map[0x22] = "PageDown"; map[0x23] = "End"; map[0x24] = "Home"; map[0x25] = "Left";
        map[0x26] = "Up"; map[0x27] = "Right"; map[0x28] = "Down"; map[0x2C] = "PrintScreen";
        map[0x2D] = "Insert"; map[0x2E] = "Delete"; map[0x90] = "NumLock"; map[0x91] = "ScrollLock";
        map[0xBA] = ";"; map[0xBB] = "="; map[0xBC] = ","; map[0xBD] = "-"; map[0xBE] = ".";
        map[0xBF] = "/"; map[0xC0] = "`"; map[0xDB] = "["; map[0xDC] = "\\"; map[0xDD] = "]"; map[0xDE] = "'";
        map[0xAD] = "VolumeMute"; map[0xB3] = "MediaPlayPause"; map[0xB2] = "MediaStop";
        map[0xB0] = "MediaNext"; map[0xB1] = "MediaPrevious";
        return map;
    }

    private static HashSet<int> BuildStandaloneKeys()
    {
        var set = new HashSet<int> { 0x13 /*Pause*/, 0x91 /*ScrollLock*/, 0x2C /*PrintScreen*/ };
        for (var f = 13; f <= 24; f++) set.Add(0x6F + f); // F13–F24 (macro keys)
        return set;
    }
}
