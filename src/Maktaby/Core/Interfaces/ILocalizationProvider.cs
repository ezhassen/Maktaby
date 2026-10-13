using System.Globalization;

namespace Maktaby.Core.Interfaces;

/// <summary>Direction content is laid out in for a language.</summary>
public enum TextFlowDirection
{
    /// <summary>Left-to-right (default).</summary>
    LeftToRight,

    /// <summary>Right-to-left (Arabic, Hebrew, …).</summary>
    RightToLeft,
}

/// <summary>One translatable language, discovered from its .xlf file.</summary>
public sealed class LanguageInfo
{
    /// <summary>BCP-47 culture code (e.g. "en", "ar"). Also the file name (ar.xlf).</summary>
    public required string Code { get; init; }

    /// <summary>Display name for the language picker, from <c>meta.name</c>
    /// (e.g. "العربية"). Falls back to the culture's native name.</summary>
    public required string DisplayName { get; init; }

    /// <summary>The language's English name (e.g. "Arabic"), from its culture. Used to build the
    /// picker label "EnglishName - NativeName" (e.g. "Arabic - العربية").</summary>
    public string EnglishName { get; init; } = "";

    /// <summary>Content flow direction for this language, from <c>meta.flowdirection</c>.
    /// Defaults to <see cref="TextFlowDirection.LeftToRight"/>.</summary>
    public TextFlowDirection FlowDirection { get; init; } = TextFlowDirection.LeftToRight;

    /// <summary>The .NET culture for formatting (numbers, dates).</summary>
    public CultureInfo Culture { get; init; } = CultureInfo.InvariantCulture;
}

/// <summary>
/// Runtime localization: loads Xliff 2.0 files from disk and resolves localized strings.
/// Implementations are pure .NET (no WPF); the WPF facade (<c>Maktaby.Localization.Loc</c>)
/// layers markup extension / FlowDirection on top.
/// </summary>
public interface ILocalizationProvider
{
    /// <summary>Currently active language, or null when localization is not yet initialized.</summary>
    LanguageInfo? Current { get; }

    /// <summary>Culture used for string/number/date formatting.</summary>
    CultureInfo Culture { get; }

    /// <summary>All discovered languages (including English, the source/fallback).</summary>
    IReadOnlyList<LanguageInfo> AvailableLanguages { get; }

    /// <summary>
    /// Localized text for <paramref name="key"/>. Lookup: current language → English →
    /// <c>[key]</c> (visible during development; never null, never throws).
    /// <paramref name="args"/> are formatted with the current culture when non-empty.
    /// </summary>
    string Get(string key, params object?[] args);

    /// <summary>The English (source) text for <paramref name="key"/>, regardless of the active
    /// language. Used for bilingual headers ("اللغة (Language)"). Empty when missing.</summary>
    string GetEnglish(string key);

    /// <summary>
    /// Plural form of <paramref name="key"/> for <paramref name="count"/>: the target segment
    /// whose CLDR plural category matches (one/two/few/many/other/zero), then formatted with
    /// <paramref name="args"/>. Falls back to the "other" segment, then to <see cref="Get"/>.
    /// </summary>
    string Plural(string key, int count, params object?[] args);

    /// <summary>Raised after the active language changed: strings, culture and flow direction
    /// are all settled when this fires. Subscribers re-translate their own surface.</summary>
    event Action? LanguageChanged;

    /// <summary>Switches the active language at runtime (Settings → Language, live apply).
    /// Raises <see cref="LanguageChanged"/> when the language actually changed.</summary>
    void SetLanguage(string code);

    /// <summary>
    /// Discovers all .xlf files in <paramref name="langsFolder"/> and activates
    /// <paramref name="code"/> (null = follow the OS UI culture, English when it has no
    /// translation). Safe to call again to switch languages.
    /// </summary>
    void Initialize(string langsFolder, string? code);
}
