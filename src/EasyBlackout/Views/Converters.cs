using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;
using EasyBlackout.Core.Providers;

namespace EasyBlackout.Views;

public sealed class BoolToVisibilityConverter : IValueConverter
{
    public bool Invert { get; set; }

    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        (value is true) ^ Invert ? Visibility.Visible : Visibility.Collapsed;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        (value is Visibility.Visible) ^ Invert;
}

/// <summary>Maps a <see cref="DeviceStatus"/> or <see cref="ProviderState"/> to a theme brush. Parameter "bg" gives the subtle fill.</summary>
public sealed class StatusBrushConverter : IValueConverter
{
    private enum Tone { Success, Accent, Warning, Danger, Neutral }

    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        var tone = value switch
        {
            DeviceStatus.Ready => Tone.Success,
            DeviceStatus.BlackedOut => Tone.Accent,
            DeviceStatus.NeedsSetup or DeviceStatus.NotControllable => Tone.Warning,
            DeviceStatus.Error => Tone.Danger,
            ProviderState.Ready => Tone.Success,
            ProviderState.NeedsSetup => Tone.Warning,
            ProviderState.Error => Tone.Danger,
            _ => Tone.Neutral,
        };
        var subtle = parameter as string == "bg";
        var key = (tone, subtle) switch
        {
            (Tone.Success, false) => "SuccessBrush",
            (Tone.Success, true) => "SuccessSubtleBrush",
            (Tone.Accent, false) => "AccentHoverBrush",
            (Tone.Accent, true) => "AccentSubtleBrush",
            (Tone.Warning, false) => "WarningBrush",
            (Tone.Warning, true) => "WarningSubtleBrush",
            (Tone.Danger, false) => "DangerBrush",
            (Tone.Danger, true) => "DangerSubtleBrush",
            (_, false) => "TextSecondaryBrush",
            (_, true) => "NeutralSubtleBrush",
        };
        return Application.Current.TryFindResource(key) as Brush ?? Brushes.Gray;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
