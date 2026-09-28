using System;
using System.Globalization;
using System.Windows.Data;

namespace Maktaby.Converters;

/// <summary>
/// Converts the resolved icon size (px) into the item tile edge length: tile = icon + offset.
/// Offsets are the hand-tuned chrome deltas at the 32px baseline (68×64 tile ⇒ +36 width, +32 height).
/// </summary>
public sealed class AdaptiveTileSizeConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        double icon = value is double d && d > 0 ? d : 32d;
        double offset = 0;
        if (parameter is not null)
        {
            _ = double.TryParse(parameter.ToString(), out offset);
        }

        return icon + offset;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
