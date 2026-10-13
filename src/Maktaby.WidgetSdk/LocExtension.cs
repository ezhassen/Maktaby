using System.Windows;
using System.Windows.Data;
using System.Windows.Markup;

namespace Maktaby.WidgetSdk;

/// <summary>
/// Markup extension for a native widget's loose XAML: <c>{sdk:Loc Key}</c> resolves the
/// widget's localized string and re-translates on a live language switch.
/// <code>
/// &lt;TextBlock Text="{sdk:Loc Today}" /&gt;
/// </code>
/// <para>
/// Post-parse resolution: at parse time the extension records its target
/// (<c>IProvideValueTarget</c>) and returns the raw key as a placeholder. The owning
/// <see cref="NativeWidgetControl"/> resolves every recorded target once the host attaches
/// an <see cref="IWidgetLocalization"/> (see <see cref="NativeWidgetControl.SetLocalization"/>),
/// pushing the translated value and re-pushing on <see cref="IWidgetLocalization.LanguageChanged"/>.
/// This mirrors the host app's own <c>{loc:LocKey …}</c> behavior, so live switching works
/// identically. The default value used when a key is missing is the key itself.
/// </para>
/// </summary>
public sealed class LocExtension : MarkupExtension
{
    /// <summary>Creates an extension for <paramref name="key"/> — the XAML positional form
    /// <c>{sdk:Loc Today}</c>.</summary>
    public LocExtension(string key)
    {
        Key = key;
    }

    /// <summary>Creates an empty extension (XAML parser property form).</summary>
    public LocExtension() { }

    /// <summary>The localization key (positional constructor argument in XAML).</summary>
    public string? Key { get; set; }

    public override object ProvideValue(IServiceProvider serviceProvider)
    {
        var key = Key ?? string.Empty;

        // Record the target so the owning control can resolve + live-update it later.
        if (!string.IsNullOrEmpty(key)
            && serviceProvider.GetService(typeof(IProvideValueTarget)) is IProvideValueTarget pvt
            && pvt.TargetObject is DependencyObject target
            && pvt.TargetProperty is DependencyProperty property)
        {
            NativeWidgetControl.RegisterLocTarget(target, property, key);
        }

        // Placeholder until SetLocalization resolves it; shows the key meanwhile.
        return key;
    }
}
