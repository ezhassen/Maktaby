using System;
using System.Globalization;
using System.Windows.Data;

namespace DesktopBoxesUI.Converters;

/// <summary>
/// Compares the two supplied <see cref="ViewModels.BoxViewModel"/> values and returns true when they
/// are the same reference (i.e. the tab item is the selected box).
/// </summary>
public sealed class TabSelectedConverter : IMultiValueConverter
{
    public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture)
    {
        if (values is not { Length: >= 2 })
        {
            return false;
        }

        return ReferenceEquals(values[0], values[1]);
    }

    public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
