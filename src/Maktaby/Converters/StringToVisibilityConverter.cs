using System;
using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace Maktaby.Converters;

/// <summary>
/// Converts a string to <see cref="Visibility"/>: non-blank = Visible, null/empty/whitespace =
/// Collapsed. Used to hide "what's new" blocks that have nothing to show rather than leaving
/// an empty gap in the layout.
/// </summary>
public sealed class StringToVisibilityConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => string.IsNullOrWhiteSpace(value as string) ? Visibility.Collapsed : Visibility.Visible;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException("StringToVisibilityConverter is one-way.");
}
