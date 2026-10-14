using Maktaby.Core.Interfaces;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.IO;

namespace Maktaby.Core.Services;

/// <summary>
/// Runtime localization provider: loads Xliff 2.0 .xlf files from disk (no resx, no
/// satellite assemblies), resolves strings with English fallback, and handles CLDR
/// plurals. Pure .NET — no WPF.
/// <para>
/// Lookup order for a string: current language → English (<c>en</c>) → <c>[key]</c>.
/// Missing keys never blank or throw, so a partial translation renders English rather
/// than an empty label.
/// </para>
/// </summary>
public sealed class LocalizationProvider : ILocalizationProvider
{
    /// <summary>English, the source and fallback language.</summary>
    private const string EnglishCode = "en";

    private readonly Dictionary<string, XlfDocument> _documents = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, LanguageInfo> _languages = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _gate = new();
    private readonly ISystemLanguageResolver? _systemLanguage;

    private LanguageInfo? _current;
    private CultureInfo _culture = CultureInfo.InvariantCulture;

    public LocalizationProvider()
    {
    }

    /// <param name="systemLanguage">
    /// Resolves the OS display language for "System" (follow the OS). Optional so the provider
    /// stays constructible without a platform layer — see <see cref="ResolveSystemLanguage"/>.
    /// </param>
    public LocalizationProvider(ISystemLanguageResolver? systemLanguage)
    {
        _systemLanguage = systemLanguage;
    }

    public LanguageInfo? Current => _current;

    public CultureInfo Culture => _culture;

    public IReadOnlyList<LanguageInfo> AvailableLanguages
    {
        get
        {
            lock (_gate)
            {
                return _languages.Values
                    .OrderBy(l => l.Code == EnglishCode ? 0 : 1)
                    .ThenBy(l => l.DisplayName, StringComparer.CurrentCultureIgnoreCase)
                    .ToList();
            }
        }
    }

    public event Action? LanguageChanged;

    public void Initialize(string langsFolder, string? code)
    {
        lock (_gate)
        {
            _documents.Clear();
            _languages.Clear();

            if (Directory.Exists(langsFolder))
            {
                foreach (var file in Directory.EnumerateFiles(langsFolder, "*.xlf"))
                {
                    var doc = LoadDocument(file);
                    if (doc is null || doc.Entries.Count == 0) continue;
                    var code2 = DeriveCode(file, doc);
                    if (string.IsNullOrEmpty(code2)) continue;
                    _documents[code2] = doc;
                    _languages[code2] = BuildInfo(code2, doc);
                }
            }

            // English is the fallback: if the folder shipped without en.xlf we still need an
            // entry so resolution always has a base.
            if (!_languages.ContainsKey(EnglishCode))
            {
                var inv = CultureInfo.GetCultureInfo("en");
                _languages[EnglishCode] = new LanguageInfo
                {
                    Code = EnglishCode,
                    DisplayName = "English",
                    EnglishName = "English",
                    FlowDirection = Interfaces.TextFlowDirection.LeftToRight,
                    Culture = inv,
                };
            }

            // "System" means the OS DISPLAY language, never the regional format culture.
            //
            // Two traps, both of which used to be here:
            //  1. CurrentUICulture is NOT readable here - ActivateLocked assigns it to the
            //     active language on every switch, so "System" would resolve to whatever
            //     language is currently active (ar -> ar), i.e. never follow the OS at all.
            //  2. InstalledUICulture is not reliable either. It is documented as the OS UI
            //     language, but with an empty per-user language list the runtime falls back
            //     to the user's DEFAULT LOCALE - the regional FORMAT culture. Measured on
            //     English-display + Arabic-region Windows: GetUserDefaultUILanguage() =
            //     0x0409 (en) while InstalledUICulture = ar-EG, so this picked Arabic on an
            //     English machine. The resolver is what actually asks Win32; the managed
            //     fallback below is only for when no platform layer supplied one.
            string? effective = code;
            if (string.IsNullOrWhiteSpace(effective))
            {
                var candidate = ResolveSystemLanguage();
                if (candidate is not null && _languages.ContainsKey(candidate)) effective = candidate;
            }

            if (string.IsNullOrWhiteSpace(effective) || !_languages.ContainsKey(effective))
                effective = EnglishCode;

            ActivateLocked(effective);
        }

        // A re-initialize (e.g. Settings → Language → "System") re-resolves everything, so
        // open windows must re-translate. Raise outside the lock (handlers re-read state).
        RaiseLanguageChanged();
    }

    /// <summary>Switches the active language at runtime (Settings → Language, live apply).</summary>
    public void SetLanguage(string code)
    {
        lock (_gate)
        {
            if (string.IsNullOrWhiteSpace(code) || !_languages.ContainsKey(code)) return;
            if (string.Equals(_current?.Code, code, StringComparison.OrdinalIgnoreCase)) return;
            ActivateLocked(code);
        }

        // Raise OUTSIDE the lock: subscribers re-read Current/Culture and must never
        // deadlock against a handler that calls back into Get().
        RaiseLanguageChanged();
    }

    /// <summary>
    /// The two-letter code of the OS display language, for "System".
    /// </summary>
    /// <remarks>
    /// Prefers the injected <see cref="ISystemLanguageResolver"/> (which asks Win32). Falls back
    /// to <see cref="CultureInfo.InstalledUICulture"/> only when no platform layer supplied one:
    /// it is wrong on a machine whose display language and region disagree, but it is still
    /// better than nothing, and this path only runs in a resolver-less context.
    /// </remarks>
    private string? ResolveSystemLanguage()
    {
        if (_systemLanguage is not null)
        {
            try
            {
                return _systemLanguage.DisplayLanguageCode;
            }
            catch
            {
                // Fall through to the managed guess.
            }
        }

        try
        {
            var twoLetter = CultureInfo.InstalledUICulture.TwoLetterISOLanguageName;
            return string.IsNullOrWhiteSpace(twoLetter) ? null : twoLetter;
        }
        catch
        {
            return null;
        }
    }

    public string Get(string key, params object?[] args)
    {
        var doc = ResolveDocument(key, out var text);
        if (doc is null || text is null)
            return Missing(key);

        // Plural unit used through Get (not Plural): resolve the "other" segment so a
        // count-bearing key never prints its raw "one|…|other|…" form.
        if (doc.ContainsPlural(key))
            return FormatSegment(doc, key, text, 1, args);

        if (args is null || args.Length == 0)
            return text;

        try
        {
            return string.Format(_culture, text, args);
        }
        catch (FormatException)
        {
            // A bad placeholder must not break a label: show the raw template.
            return text;
        }
    }

    /// <summary>English (source) text for <paramref name="key"/>, ignoring the active language.
    /// Reads the English document directly; empty when the key is missing there.</summary>
    public string GetEnglish(string key)
    {
        if (string.IsNullOrEmpty(key)) return string.Empty;
        if (_documents.TryGetValue(EnglishCode, out var english)
            && english.Entries.TryGetValue(key, out var text))
        {
            return text;
        }
        return string.Empty;
    }

    public string Plural(string key, int count, params object?[] args)
    {
        var doc = ResolveDocument(key, out var text);
        if (doc is null || text is null)
            return Missing(key);

        var category = PluralRules.GetCategory(_culture, count);
        return FormatSegment(doc, key, text, count, args, category);
    }

    #region internals

    private void ActivateLocked(string code)
    {
        var info = _languages[code];
        _current = info;

        // Formatting culture: the language's own culture so numbers/dates localize with the
        // text (Arabic-Indic digits for ar). CurrentCulture is thread-local in .NET; set it
        // here so every later string.Format in the app formats in the active language.
        _culture = info.Culture;
        try
        {
            CultureInfo.CurrentCulture = info.Culture;
            CultureInfo.CurrentUICulture = info.Culture;
        }
        catch
        {
            // A custom culture we cannot set: keep the invariant one. Formatting still works.
        }
    }

    [RequiresUnreferencedCode("XDocument.Load is not trimming-safe; this app is not trimmed.")]
    [RequiresDynamicCode("XDocument.Load may require dynamic code on some runtimes; this app is not AOT-published.")]
    private static XlfDocument? LoadDocument(string file)
    {
        try
        {
            if (!File.Exists(file)) return null;
            return XlfDocument.Load(file);
        }
        catch
        {
            return null;
        }
    }

    private string DeriveCode(string file, XlfDocument doc)
    {
        var name = Path.GetFileNameWithoutExtension(file);
        if (!string.IsNullOrWhiteSpace(name)) return name;
        if (!string.IsNullOrWhiteSpace(doc.TargetLanguage)) return doc.TargetLanguage;
        return doc.SourceLanguage;
    }

    private LanguageInfo BuildInfo(string code, XlfDocument doc)
    {
        CultureInfo culture;
        try { culture = CultureInfo.GetCultureInfo(code); }
        catch { culture = CultureInfo.InvariantCulture; }

        // The language drives UI text + text direction, but number/date formats must follow
        // the user's Windows regional settings, not the language's default culture. So start
        // from the language culture and overlay the OS's current date/number formats (e.g.
        // Arabic UI on an en-US machine keeps Gregorian dates and '.' decimals rather than
        // Arabic-Indic digits and the Umm al-Qura calendar). Fall back to the OS culture's
        // formats when the language culture is read-only/invariant.
        culture = WithSystemFormats(culture);

        return new LanguageInfo
        {
            Code = code,
            DisplayName = !string.IsNullOrWhiteSpace(doc.MetaNameValue)
                ? doc.MetaNameValue!
                : culture.IsNeutralCulture ? culture.EnglishName : culture.NativeName,
            EnglishName = culture.EnglishName,
            FlowDirection = doc.FlowDirection ?? Interfaces.TextFlowDirection.LeftToRight,
            Culture = culture,
        };
    }

    /// <summary>Clones <paramref name="language"/> and replaces its date/number formats with
    /// the OS's current ones, so formatting follows the user's regional settings rather than
    /// the language's defaults. The invariant culture is returned unchanged (no OS formats to
    /// mirror). Cached cultures from <c>GetCultureInfo</c> are read-only, so we always clone —
    /// the clone is writable and lets us swap the formats safely.</summary>
    private static CultureInfo WithSystemFormats(CultureInfo language)
    {
        try
        {
            if (language == CultureInfo.InvariantCulture)
                return language;

            var os = CultureInfo.CurrentCulture;
            if (os == null || os == CultureInfo.InvariantCulture)
                return language;

            var clone = (CultureInfo)language.Clone();
            clone.DateTimeFormat = (DateTimeFormatInfo)os.DateTimeFormat.Clone();
            clone.NumberFormat = (NumberFormatInfo)os.NumberFormat.Clone();
            return clone;
        }
        catch
        {
            return language;
        }
    }

    /// <summary>Finds the document holding <paramref name="key"/>: current language first,
    /// then English. Returns null when neither has it.</summary>
    private XlfDocument? ResolveDocument(string key, out string? text)
    {
        text = null;
        if (string.IsNullOrEmpty(key)) return null;

        if (_current is not null
            && _documents.TryGetValue(_current.Code, out var current)
            && current.Entries.TryGetValue(key, out var t1))
        {
            text = t1;
            return current;
        }

        if (_documents.TryGetValue(EnglishCode, out var english)
            && english.Entries.TryGetValue(key, out var t2))
        {
            text = t2;
            return english;
        }

        return null;
    }

    private string FormatSegment(XlfDocument doc, string key, string text, int count, object?[]? args, string? category = null)
    {
        var segments = PluralRules.ParseSegments(text);
        string? template = null;
        if (category is not null)
        {
            if (!segments.TryGetValue(category, out template))
                segments.TryGetValue("other", out template);
        }
        else
        {
            segments.TryGetValue("other", out template);
        }
        template ??= text;

        if (args is null || args.Length == 0)
        {
            try { return string.Format(_culture, template, count); }
            catch (FormatException) { return template; }
        }

        var all = new object?[args.Length + 1];
        all[0] = count;
        Array.Copy(args, 0, all, 1, args.Length);
        try { return string.Format(_culture, template, all); }
        catch (FormatException) { return template; }
    }

    private static string Missing(string key) => $"[{key}]";

    private void RaiseLanguageChanged()
    {
        var handler = LanguageChanged;
        if (handler is null) return;
        foreach (var h in handler.GetInvocationList())
        {
            try { ((Action)h)(); }
            catch
            {
                // One broken subscriber must not stop the others (or the switch itself).
            }
        }
    }

    #endregion
}
