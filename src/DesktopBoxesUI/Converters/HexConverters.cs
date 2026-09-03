using System;
using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;
using DesktopBoxesUI.Helpers;

namespace DesktopBoxesUI.Converters;

/// <summary>
/// Converts a hex color string (#RGB / #RRGGBB / #AARRGGBB) to a <see cref="SolidColorBrush"/>.
/// Returns <see cref="Brushes.Transparent"/> when the string is empty or invalid. View-only.
/// </summary>
public sealed class HexToBrushConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is string hex && BoxAppearance.TryColor(hex, out var color)
            ? new SolidColorBrush(color)
            : Brushes.Transparent;

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => null;
}

/// <summary>
/// Picks a brush based on hex validity: red outline when invalid, transparent when valid.
/// </summary>
public sealed class HexValidToBrushConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is string hex && BoxAppearance.TryColor(hex, out _)
            ? Brushes.Transparent
            : Brushes.Red;

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => null;
}
