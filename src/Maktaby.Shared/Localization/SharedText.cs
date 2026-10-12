using System.Globalization;

namespace Maktaby.Shared.Localization;

/// <summary>
/// Minimal localization hook for the Shared (Maktaby.Shared) UI surfaces — the dialog
/// service's default button text ("OK"/"Cancel") and the shared exception window. Maktaby
/// does not exist as a reference from Shared (Shared is standalone-usable), so the host
/// registers a resolver delegate at startup instead:
/// <code>
/// SharedText.Resolver = key =&gt; Maktaby.Localization.Loc.Get(key);
/// </code>
/// Falls back to English literals when no resolver is registered (Shared used standalone,
/// or before the host wires it), so a dialog never shows a blank or a <c>[key]</c>.
/// </summary>
public static class SharedText
{
    /// <summary>Host-registered resolver: key → localized text. Null = use the built-in
    /// English defaults.</summary>
    public static Func<string, string>? Resolver { get; set; }

    /// <summary>Content flow direction for the active language. LeftToRight by default.</summary>
    public static Func<bool>? IsRightToLeft { get; set; }

    /// <summary>Localized text for <paramref name="key"/>, or the <paramref name="fallback"/>
    /// when no resolver is registered.</summary>
    public static string Get(string key, string fallback)
        => Resolver is null ? fallback : SafeGet(key, fallback);

    /// <summary>True when the active language is right-to-left.</summary>
    public static bool RightToLeft => IsRightToLeft is null ? false : SafeRtl();

    private static string SafeGet(string key, string fallback)
    {
        try
        {
            var text = Resolver!(key);
            return string.IsNullOrEmpty(text) ? fallback : text;
        }
        catch
        {
            return fallback;
        }
    }

    private static bool SafeRtl()
    {
        try { return IsRightToLeft!(); }
        catch { return false; }
    }
}
