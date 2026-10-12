using System.Globalization;

namespace Maktaby.Core.Services;

/// <summary>
/// CLDR plural categories per language. The set of categories actually used is small and
/// stable (zero/one/two/few/many/other), so this is a compact rules table rather than a
/// full CLDR parser — enough for every language this app is expected to ship.
/// </summary>
public static class PluralRules
{
    /// <summary>CLDR plural category for <paramref name="count"/> under <paramref name="culture"/>.</summary>
    public static string GetCategory(CultureInfo culture, int count)
    {
        var lang = culture.TwoLetterISOLanguageName;

        // Arabic: zero, one, two, few (3–10), many (11–99), other (100+).
        if (lang == "ar")
        {
            return count switch
            {
                0 => "zero",
                1 => "one",
                2 => "two",
                >= 3 and <= 10 => "few",
                >= 11 and <= 99 => "many",
                _ => "other",
            };
        }

        // Welsh: zero, one, two, few, many, other.
        if (lang == "cy")
        {
            return count switch
            {
                0 => "zero",
                1 => "one",
                2 => "two",
                3 => "few",
                6 => "many",
                _ => "other",
            };
        }

        // Slavic-family "few"/"many" languages (Polish, Russian, Ukrainian, Czech, …).
        if (lang is "pl" or "ru" or "uk" or "cs" or "sk" or "hr" or "sr" or "bs" or "be")
        {
            int mod10 = count % 10, mod100 = count % 100;
            if (count == 1) return "one";
            if (mod10 >= 2 && mod10 <= 4 && (mod100 < 12 || mod100 > 14)) return "few";
            return "many";
        }

        // Everything else (English, German, Spanish, …): one / other.
        return count == 1 ? "one" : "other";
    }

    /// <summary>
    /// Splits a raw plural unit value (<c>"zero|…|one|…|other|…"</c>) into a category → text map.
    /// A value without the pipe form is treated as the "other" segment, so a single-string
    /// translation still works.
    /// </summary>
    public static Dictionary<string, string> ParseSegments(string raw)
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (!raw.Contains('|'))
        {
            map["other"] = raw.Trim();
            return map;
        }

        var parts = raw.Split('|');
        for (int i = 0; i + 1 < parts.Length; i += 2)
        {
            var category = parts[i].Trim();
            var text = parts[i + 1];
            if (category.Length > 0 && !map.ContainsKey(category))
                map[category] = text;
        }
        return map;
    }
}
