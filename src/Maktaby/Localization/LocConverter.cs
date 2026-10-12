using System.Globalization;
using System.Windows.Data;

namespace Maktaby.Localization;

/// <summary>
/// Translates a ViewModel-bound key into localized text and re-evaluates when the
/// language changes, so view models can hold keys instead of display strings.
/// <code>
/// &lt;TextBlock Text="{Binding ResetConfirmKey, Converter={StaticResource Loc}}" /&gt;
/// </code>
/// The bound value is the key; the converter returns <see cref="Loc.Get"/>'s result and
/// re-pushes on <see cref="Loc.LanguageChanged"/>.
/// </summary>
public sealed class LocConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is string key && !string.IsNullOrEmpty(key))
            return Loc.Get(key);
        if (value is int count && parameter is string pk && !string.IsNullOrEmpty(pk))
            return Loc.Plural(pk, count);
        return value;
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value;
}
