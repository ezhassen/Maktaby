using Microsoft.Win32;
using System;
using Wpf.Ui.Appearance;

namespace Maktaby.Helpers;

/// <summary>
/// Resolves the user's actual Windows light/dark choice ("Settings → Personalization →
/// Colors → Choose your default Windows mode").
/// </summary>
/// <remarks>
/// <para>
/// This exists because <c>ApplicationThemeManager.ApplySystemTheme()</c> gets it wrong on
/// Windows 10/11, so the app cannot use it on the "System" setting. Wpf.Ui 4.3.0's
/// <c>SystemThemeManager.GetCurrentSystemTheme()</c> reads, in order:
///
/// <list type="number">
///   <item><description><c>HKCU\…\Themes\CurrentTheme</c> — the active visual-STYLE FILE
///   (<c>aero.theme</c> / <c>dark.theme</c> / <c>hcblack.theme</c> …), and</description></item>
///   <item><description>only if that is empty, <c>…\Themes\Personalize\AppsUseLightTheme</c>,
///   the actual light/dark preference.</description></item>
/// </list>
///
/// <c>CurrentTheme</c> is essentially never empty on Win10/11, so branch 2 is dead code and the
/// library reports whatever the style file happens to be. Verified on the developer's machine:
/// <c>CurrentTheme = C:\WINDOWS\resources\Themes\dark.theme</c> while
/// <c>AppsUseLightTheme = 1</c> (Light chosen) — so <c>ApplySystemTheme()</c> applied Dark and
/// the app could not follow Windows. The two are unrelated: the style file is a leftover of the
/// installed theme, the preference is the user's live toggle.
/// </para>
/// <para>
/// Fix is to read the authoritative value and apply the <see cref="ApplicationTheme"/>
/// explicitly, keeping Wpf.Ui for the one thing it still does correctly — mapping a Windows
/// high-contrast style onto <see cref="ApplicationTheme.HighContrast"/>, which
/// <c>AppsUseLightTheme</c> cannot express.
/// </para>
/// </remarks>
internal static class SystemThemeReader
{
    private const string PersonalizeKey = @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize";

    /// <summary>
    /// The theme to apply for the "System" preference, or <c>null</c> when it cannot be
    /// determined from the registry — and also when Windows high contrast is active, in which
    /// case the caller must go through Wpf.Ui so the correct HC style is chosen.
    /// </summary>
    public static ApplicationTheme? ReadPreferredAppTheme()
    {
        try
        {
            // High contrast wins over the light/dark preference. Wpf.Ui maps the active HC style
            // (HC1/HC2/HCWhite/HCBlack) to the right theme dictionary; AppsUseLightTheme cannot
            // express that, so defer the whole decision to Wpf.Ui.
            SystemThemeManager.UpdateSystemThemeCache();
            if (SystemThemeManager.HighContrast) { return null; }
        }
        catch
        {
            // If the HC probe fails, fall through to the registry value rather than giving up:
            // a wrong light/dark choice is a much smaller problem than a stuck theme.
        }

        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(PersonalizeKey);
            if (key is null) { return null; }

            // AppsUseLightTheme is the value Windows itself writes for the light/dark toggle.
            // 0 => dark, anything else (normally 1) => light. Match Wpf.Ui's own reading of it
            // so there is no second dialect to keep in sync.
            if (key.GetValue("AppsUseLightTheme") is int appsUseLightTheme)
            {
                return appsUseLightTheme == 0 ? ApplicationTheme.Dark : ApplicationTheme.Light;
            }

            // Older builds only track the shell-side value. Same 0/1 convention.
            if (key.GetValue("SystemUsesLightTheme") is int systemUsesLightTheme)
            {
                return systemUsesLightTheme == 0 ? ApplicationTheme.Dark : ApplicationTheme.Light;
            }
        }
        catch
        {
            // Registry unavailable (locked down / partial profile): report unknown so the
            // caller falls back to Wpf.Ui rather than guessing.
        }

        return null;
    }
}
