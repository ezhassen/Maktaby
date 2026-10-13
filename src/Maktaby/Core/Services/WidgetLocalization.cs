using Maktaby.Core.Interfaces;
using Maktaby.WidgetSdk;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.IO;
using System.Windows;

namespace Maktaby.Core.Services;

/// <summary>
/// Per-widget <see cref="IWidgetLocalization"/>: loads a native widget's own
/// <c>langs/*.xlf</c> files (Xliff 2.0) from its folder and resolves strings against the
/// active app language. English (<c>en.xlf</c>) is the fallback; a missing key returns the
/// caller-supplied default. Flow direction comes from the active xlf's
/// <c>meta.flowdirection</c>, defaulting to the host app language's direction.
/// Pure .NET except the <see cref="FlowDirection"/> return (SDK type); no WPF tree access.
/// </summary>
public sealed class WidgetLocalization : IWidgetLocalization
{
    private const string EnglishCode = "en";

    private readonly Dictionary<string, XlfDocument> _docs = new(StringComparer.OrdinalIgnoreCase);
    private string _activeCode = EnglishCode;
    private CultureInfo _culture = CultureInfo.InvariantCulture;
    private FlowDirection _flow = FlowDirection.LeftToRight;

    /// <summary>Active language code this instance currently resolves against.</summary>
    public string ActiveCode => _activeCode;

    public FlowDirection FlowDirection => _flow;

    public event Action? LanguageChanged;

    /// <summary>
    /// Loads all <c>*.xlf</c> under <paramref name="langsFolder"/> and activates
    /// <paramref name="activeCode"/> (falls back to <c>en</c>, then to whatever is present).
    /// <paramref name="hostFlowRightToLeft"/> supplies the fallback flow direction (the host
    /// app language's) when the active xlf declares none.
    /// </summary>
    [RequiresUnreferencedCode("XDocument.Load is not trimming-safe; this app is not trimmed.")]
    [RequiresDynamicCode("XDocument.Load may require dynamic code on some runtimes; this app is not AOT-published.")]
    public void Initialize(string langsFolder, string activeCode, bool hostFlowRightToLeft)
    {
        _docs.Clear();
        if (Directory.Exists(langsFolder))
        {
            foreach (var file in Directory.EnumerateFiles(langsFolder, "*.xlf"))
            {
                var code = Path.GetFileNameWithoutExtension(file);
                if (string.IsNullOrWhiteSpace(code)) continue;
                try { _docs[code] = XlfDocument.Load(file); }
                catch { /* corrupt file: skip, fall back */ }
            }
        }

        string effective = activeCode;
        if (string.IsNullOrWhiteSpace(effective) || !_docs.ContainsKey(effective))
            effective = _docs.ContainsKey(EnglishCode) ? EnglishCode
                : (_docs.Count > 0 ? _docs.Keys.First() : EnglishCode);

        Activate(effective, hostFlowRightToLeft);
    }

    /// <summary>Switches the active language (host app language changed) and raises
    /// <see cref="LanguageChanged"/>.</summary>
    public void SetActive(string? activeCode, bool hostFlowRightToLeft)
    {
        string effective = activeCode ?? string.Empty;
        if (string.IsNullOrWhiteSpace(effective) || !_docs.ContainsKey(effective))
            effective = _docs.ContainsKey(EnglishCode) ? EnglishCode
                : (_docs.Count > 0 ? _docs.Keys.First() : EnglishCode);
        if (string.Equals(_activeCode, effective, StringComparison.OrdinalIgnoreCase)
            && _flow == (hostFlowRightToLeft ? FlowDirection.RightToLeft : FlowDirection.LeftToRight))
            return;
        Activate(effective, hostFlowRightToLeft);
        Raise();
    }

    private void Activate(string code, bool hostFlowRightToLeft)
    {
        _activeCode = code;
        try { _culture = WithSystemFormats(CultureInfo.GetCultureInfo(code)); }
        catch { _culture = CultureInfo.InvariantCulture; }

        // The widget's own xlf direction wins; otherwise follow the host app language.
        bool rtl;
        if (_docs.TryGetValue(code, out var doc) && doc.FlowDirection.HasValue)
            rtl = doc.FlowDirection.Value == Interfaces.TextFlowDirection.RightToLeft;
        else
            rtl = hostFlowRightToLeft;
        _flow = rtl ? FlowDirection.RightToLeft : FlowDirection.LeftToRight;
    }

    public string Get(string key, string defaultValue, params object?[] args)
    {
        var doc = Resolve(key, out var text);
        if (doc is null || text is null)
            return defaultValue;

        if (doc.ContainsPlural(key))
            return FormatSegment(PluralRules.ParseSegments(text), text, 1, args);

        if (args is null || args.Length == 0)
            return text;
        try { return string.Format(_culture, text, args); }
        catch (FormatException) { return text; }
    }

    public string Plural(string key, int count, string defaultValue, params object?[] args)
    {
        var doc = Resolve(key, out var text);
        if (doc is null || text is null)
            return defaultValue;

        var category = PluralRules.GetCategory(_culture, count);
        var segments = PluralRules.ParseSegments(text);
        if (!segments.TryGetValue(category, out var template))
            segments.TryGetValue("other", out template!);
        template ??= text;
        return FormatSegment(segments, template, count, args);
    }

    private XlfDocument? Resolve(string key, out string? text)
    {
        text = null;
        if (string.IsNullOrEmpty(key)) return null;
        if (_docs.TryGetValue(_activeCode, out var cur) && cur.Entries.TryGetValue(key, out var t1))
        {
            text = t1;
            return cur;
        }
        if (_docs.TryGetValue(EnglishCode, out var en) && en.Entries.TryGetValue(key, out var t2))
        {
            text = t2;
            return en;
        }
        return null;
    }

    private string FormatSegment(Dictionary<string, string> segments, string template, int count, object?[]? args)
    {
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

    private void Raise()
    {
        var handler = LanguageChanged;
        if (handler is null) return;
        foreach (var h in handler.GetInvocationList())
        {
            try { ((Action)h)(); }
            catch { }
        }
    }

    /// <summary>Clones <paramref name="language"/> and replaces its date/number formats with
    /// the OS's current ones (see LocalizationProvider.WithSystemFormats): the language drives
    /// UI text, formatting follows the user's regional settings.</summary>
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
}
