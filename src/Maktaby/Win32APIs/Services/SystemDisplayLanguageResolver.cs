using System;
using System.Globalization;
using System.Runtime.Versioning;
using Maktaby.Core.Interfaces;
using Maktaby.Win32.NativeMethods;

namespace Maktaby.Win32.Services;

/// <summary>
/// Reads the OS UI (display) language from Win32's <c>GetUserDefaultUILanguage</c> and maps the
/// LANGID to a two-letter ISO code.
/// </summary>
/// <remarks>
/// <para>
/// The reason this class exists rather than a <see cref="CultureInfo"/> lookup: with an empty
/// per-user language list, <c>CultureInfo.InstalledUICulture</c> reports the regional *format*
/// locale instead of the display language. Measured on a machine with installed display
/// language <c>en-US</c> and regional format <c>ar-EG</c>:
/// <c>GetUserDefaultUILanguage()</c> → <c>0x0409</c> (English) while
/// <c>CultureInfo.InstalledUICulture</c> → <c>ar-EG</c>. Reading the latter would start Maktaby
/// in Arabic on an English Windows.
/// </para>
/// <para>
/// Resolution order is per-user display language → installed system display language → null.
/// The system call is the primary path and the managed fallback exists only so a resolver
/// constructed in a context without interop still produces a sensible answer rather than
/// silently falling back to English.
/// </para>
/// </remarks>
[SupportedOSPlatform("windows10.0.14393")]
public sealed class SystemDisplayLanguageResolver : ISystemLanguageResolver
{
    /// <inheritdoc />
    public string? DisplayLanguageCode
    {
        get
        {
            var langId = Win32Apis.GetUserDefaultUILanguage();
            if (langId == 0)
            {
                langId = Win32Apis.GetSystemDefaultUILanguage();
            }

            return LangIdToTwoLetterIso(langId);
        }
    }

    /// <summary>
    /// LANGID → two-letter ISO language code, e.g. <c>0x0409 → "en"</c>.
    /// </summary>
    /// <remarks>
    /// <c>new CultureInfo(lcId)</c> is the BCL's own LANGID→culture mapping, so this inherits
    /// Windows' names (including the RTL and legacy code pages) instead of a hand-rolled table
    /// that would go stale. The primary language subtag is then trimmed off: the translation
    /// files are keyed <c>en</c>/<c>ar</c>, not <c>en-US</c>/<c>ar-EG</c>.
    /// </remarks>
    private static string? LangIdToTwoLetterIso(ushort langId)
    {
        if (langId == 0)
        {
            return null;
        }

        try
        {
            var culture = CultureInfo.GetCultureInfo(langId);
            var twoLetter = culture.TwoLetterISOLanguageName;
            return string.IsNullOrWhiteSpace(twoLetter) ? null : twoLetter;
        }
        catch (CultureNotFoundException)
        {
            // A LANGID Windows knows but the runtime does not (e.g. an obsolete code page).
            return null;
        }
        catch (ArgumentException)
        {
            return null;
        }
    }
}