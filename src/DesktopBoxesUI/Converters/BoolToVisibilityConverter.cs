using System;
using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace DesktopBoxesUI.Converters;

/// <summary>
/// Converts a <see cref="bool"/> to <see cref="Visibility"/> (true = Visible, false = Collapsed).
/// Used by the WPF views only; keeps the Core layer free of presentation concerns.
/// </summary>
public sealed class BoolToVisibilityConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is bool b && b ? Visibility.Visible : Visibility.Collapsed;

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is Visibility v && v == Visibility.Visible;
}

/// <summary>
/// Converts a <see cref="bool"/> to <see cref="Visibility"/> (true = Visible, false = Collapsed).
/// Used by the WPF views only; keeps the Core layer free of presentation concerns.
/// </summary>
public sealed class BoolInvertedToVisibilityConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is bool b && b ? Visibility.Collapsed : Visibility.Visible;

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is Visibility v && v == Visibility.Collapsed;
}
