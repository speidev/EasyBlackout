using EasyBlackout.Core.Hotkeys;

namespace EasyBlackout.Tests;

public class HotkeyBindingTests
{
    [Theory]
    [InlineData("Ctrl+Alt+B")]
    [InlineData("Ctrl+Shift+F12")]
    [InlineData("Alt+Win+Num5")]
    [InlineData("Ctrl+Alt+Shift+Win+Pause")]
    [InlineData("F13")]
    [InlineData("Alt+`")]
    public void RoundTrips(string text)
    {
        Assert.True(HotkeyBinding.TryParse(text, out var binding));
        Assert.Equal(text, binding.ToString());
    }

    [Theory]
    [InlineData("control + alt + b", "Ctrl+Alt+B")]
    [InlineData("ALT+CTRL+b", "Ctrl+Alt+B")]
    [InlineData("Meta+Space", "Win+Space")]
    public void ParsingIsForgivingAndNormalises(string input, string expected)
    {
        Assert.True(HotkeyBinding.TryParse(input, out var binding));
        Assert.Equal(expected, binding.ToString());
    }

    [Theory]
    [InlineData("")]
    [InlineData("Ctrl+Alt")]
    [InlineData("Ctrl+A+B")]
    [InlineData("Ctrl+NotAKey")]
    public void RejectsMalformed(string input) => Assert.False(HotkeyBinding.TryParse(input, out _));

    [Fact]
    public void DefaultIsCtrlAltB() => Assert.Equal("Ctrl+Alt+B", HotkeyBinding.Default.ToString());

    [Fact]
    public void ParseOrDefaultFallsBack() => Assert.Equal(HotkeyBinding.Default, HotkeyBinding.ParseOrDefault("garbage"));

    [Theory]
    [InlineData("Ctrl+Alt+B", true)]
    [InlineData("F13", true)]          // macro keys may stand alone
    [InlineData("Pause", true)]
    [InlineData("B", false)]           // would eat normal typing
    [InlineData("Shift+B", false)]     // likewise
    [InlineData("Shift+F20", true)]
    public void Validation(string text, bool valid)
    {
        Assert.True(HotkeyBinding.TryParse(text, out var binding));
        Assert.Equal(valid, binding.Validate() is null);
    }

    [Fact]
    public void ModifierOnlyKeyIsInvalid()
    {
        var binding = new HotkeyBinding(HotkeyModifiers.Control, 0x11 /* VK_CONTROL */);
        Assert.NotNull(binding.Validate());
    }
}
