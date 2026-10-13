using System.Windows;

namespace Maktaby.WidgetSdk;

/// <summary>
/// Per-widget localization, provided by the host at attach time. Resolves the widget's own
/// <c>langs/*.xlf</c> files against the active app language, with English fallback and a
/// caller-supplied default when a key is missing. Null-safe: every member returns a sensible
/// default so a widget works before the host attaches one (design time, standalone use).
/// </summary>
public interface IWidgetLocalization
{
    /// <summary>
    /// Localized text for <paramref name="key"/>: active language → the widget's
    /// <c>en.xlf</c> → <paramref name="defaultValue"/> (never null, never throws).
    /// <paramref name="args"/> are formatted with the current culture when non-empty.
    /// </summary>
    string Get(string key, string defaultValue, params object?[] args);

    /// <summary>
    /// Plural form of <paramref name="key"/> for <paramref name="count"/>: the target segment
    /// whose CLDR plural category matches, then formatted with <paramref name="args"/>. Falls
    /// back to the "other" segment, then <paramref name="defaultValue"/>.
    /// </summary>
    string Plural(string key, int count, string defaultValue, params object?[] args);

    /// <summary>Content flow direction of the active language for this widget:
    /// the widget's own <c>meta.flowdirection</c> when its active xlf declares one, otherwise
    /// the host app language's direction. Default LeftToRight.</summary>
    FlowDirection FlowDirection { get; }

    /// <summary>Raised after the active language changed (host app language switch).
    /// Subscribers re-resolve their strings and re-apply flow direction.</summary>
    event Action? LanguageChanged;
}
