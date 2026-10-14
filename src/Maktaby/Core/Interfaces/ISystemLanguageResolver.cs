namespace Maktaby.Core.Interfaces;

/// <summary>
/// Supplies the operating system's UI (display) language, so "System" in the language picker
/// means "the language Windows is showing", not "the region you format numbers in".
/// </summary>
/// <remarks>
/// The seam exists because <see cref="System.Globalization.CultureInfo"/> cannot answer this
/// correctly. <c>CultureInfo.InstalledUICulture</c> is documented as the OS UI language, but
/// when the per-user language list (<c>HKCU\Control Panel\Desktop\PreferredUILanguages</c>) is
/// empty — the default on most single-language installs — .NET falls back to the user's
/// DEFAULT LOCALE, which is the regional format culture. On a machine set to
/// <em>English display + Arabic region</em>, <c>InstalledUICulture</c> reports <c>ar-EG</c> and
/// an app that picks its language from it starts up in Arabic, mirroring a setting the user
/// never made.
/// <para>
/// The authority is Win32's <c>GetUserDefaultUILanguage</c>, which is why this is an interface:
/// the localization provider lives in Core, which stays free of platform interop.
/// </para>
/// </remarks>
public interface ISystemLanguageResolver
{
    /// <summary>
    /// The OS display language as a two-letter ISO code (e.g. <c>en</c>, <c>ar</c>), or
    /// <see langword="null"/> when it cannot be determined.
    /// </summary>
    /// <remarks>
    /// Only the two-letter code is returned deliberately: the translation files are keyed by
    /// it, and a region-qualified value would stop matching any language whose file is just
    /// <c>en.xlf</c>. The full culture is never wanted here — that is the format concern
    /// <c>LocalizationProvider.WithSystemFormats</c> handles separately.
    /// </remarks>
    string? DisplayLanguageCode { get; }
}