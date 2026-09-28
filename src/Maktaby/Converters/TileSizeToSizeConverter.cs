using System;
using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace Maktaby.Converters;

public sealed class TileSizeToSizeConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        double icon = value is double d && d > 0 ? d : 32d;
        double offset = 0;
        if (parameter != null) _ = double.TryParse(parameter.ToString(), out offset);
        double tile = icon + offset;
        return new Size(tile, tile);
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotSupportedException();
}
