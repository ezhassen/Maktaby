using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Xml.Linq;

namespace Maktaby.Core.Services;

/// <summary>
/// One parsed Xliff 2.0 file: keys → target text. Plain .NET (System.Xml.Linq), so it
/// lives in Core with the rest of the localization machinery.
/// </summary>
public sealed class XlfDocument
{
    /// <summary>Reserved unit ids.</summary>
    public const string MetaFlowDirection = "meta.flowdirection";
    public const string MetaName = "meta.name";

    /// <summary>Source language of the file (srcLang attribute).</summary>
    public string SourceLanguage { get; private set; } = string.Empty;

    /// <summary>Target language of the file (trgLang attribute).</summary>
    public string TargetLanguage { get; private set; } = string.Empty;

    /// <summary>Display name from the reserved <c>meta.name</c> unit, or null.</summary>
    public string? MetaNameValue { get; private set; }

    /// <summary>Flow direction from the reserved <c>meta.flowdirection</c> unit.
    /// <c>null</c> when the file does not declare one (caller defaults to LeftToRight).</summary>
    public Maktaby.Core.Interfaces.TextFlowDirection? FlowDirection { get; private set; }

    /// <summary>Key → target text. Plural units keep their raw
    /// <c>category|text|category|text</c> form; the provider splits them on lookup.</summary>
    public Dictionary<string, string> Entries { get; } = new(StringComparer.Ordinal);

    /// <summary>True when the unit's value is a plural form (<c>category|text</c> segments),
    /// so callers know to split it instead of using the raw value.</summary>
    public bool ContainsPlural(string key)
        => Entries.TryGetValue(key, out var v) && v is not null && v.Contains('|');

    /// <summary>Loads and parses one .xlf file. Never throws: a malformed or unreadable
    /// file yields an empty document (missing keys fall back, they must not crash a label).</summary>
    [RequiresUnreferencedCode("XDocument.Load is not trimming-safe; this app is not trimmed.")]
    [RequiresDynamicCode("XDocument.Load may require dynamic code on some runtimes; this app is not AOT-published.")]
    public static XlfDocument Load(string path)
    {
        var doc = new XlfDocument();
        try
        {
            var root = XDocument.Load(path).Root;
            if (root is null || !string.Equals(root.Name.LocalName, "xliff", StringComparison.OrdinalIgnoreCase))
                return doc;

            doc.SourceLanguage = (string?)root.Attribute("srcLang") ?? string.Empty;
            doc.TargetLanguage = (string?)root.Attribute("trgLang") ?? string.Empty;

            foreach (var unit in root.Descendants().Where(e => string.Equals(e.Name.LocalName, "unit", StringComparison.OrdinalIgnoreCase)))
            {
                var id = (string?)unit.Attribute("id");
                if (string.IsNullOrEmpty(id)) continue;

                // Xliff 2.0: target holds the translation, source the original. We want the
                // translation; fall back to source (a partially-filled file still translates
                // its completed units, and untranslated ones read naturally as English).
                var segment = unit.Descendants().FirstOrDefault(e => string.Equals(e.Name.LocalName, "segment", StringComparison.OrdinalIgnoreCase));
                if (segment is null) continue;

                var target = segment.Elements().FirstOrDefault(e => string.Equals(e.Name.LocalName, "target", StringComparison.OrdinalIgnoreCase));
                var source = segment.Elements().FirstOrDefault(e => string.Equals(e.Name.LocalName, "source", StringComparison.OrdinalIgnoreCase));
                var text = ((string?)target ?? (string?)source);
                if (text is null) continue;

                if (id == MetaFlowDirection)
                {
                    if (Enum.TryParse<Maktaby.Core.Interfaces.TextFlowDirection>(text.Trim(), ignoreCase: true, out var dir))
                        doc.FlowDirection = dir;
                    continue;
                }
                if (id == MetaName)
                {
                    doc.MetaNameValue = text.Trim();
                    continue;
                }

                doc.Entries[id] = text;
            }
        }
        catch
        {
            // Corrupt/unreadable file: return what we got (possibly empty). The provider
            // falls back to English / [key], so this must never throw.
        }
        return doc;
    }
}
