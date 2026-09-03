using DesktopBoxesUI.Core.Interfaces;
using DesktopBoxesUI.Settings;
using System.Windows;
using System.Windows.Media;
using Wpf.Ui.Appearance;

namespace DesktopBoxesUI.Helpers;

/// <summary>
/// Resolves the effective appearance of a Box (its container chrome + items) from
/// <see cref="UserSettings"/>. The box theme falls back to the app theme when not set
/// (<see cref="UserSettings.DefaultBoxTheme"/> is <c>null</c>), and any explicit box colors
/// override the theme palette. Transparency is supplied per-container via the override.
/// </summary>
internal sealed class BoxPalette
{
    public Brush Back { get; init; } = Brushes.Transparent;
    public Brush HeaderBack { get; init; } = Brushes.Transparent;
    public Brush HeaderBorder { get; init; } = Brushes.Transparent;
    //public Brush TabBack { get; init; } = Brushes.Transparent;
    public Brush Fore { get; init; } = Brushes.White;
    public Brush HeaderFore { get; init; } = Brushes.White;
    public Brush Border { get; init; } = Brushes.Transparent;
    public Thickness Thickness { get; init; } = new Thickness(1);
}

internal static class BoxAppearance
{
    public static BoxPalette Resolve(UserSettings s, double? containerTransparencyOverride = null)
    {
        var theme = s.DefaultBoxTheme ?? s.SelectedTheme;
        bool dark = ResolveDark(theme);

        Color back = dark ? Color.FromRgb(0x12, 0x12, 0x12) : Color.FromRgb(0xF3, 0xF3, 0xF3);
        Color fore = dark ? Colors.White : Colors.Black;
        Color border = dark ? Color.FromRgb(0x3F, 0x3F, 0x46) : Color.FromRgb(0xCC, 0xCC, 0xCC);
        Color headerBack = dark ? Color.FromRgb(0x25, 0x25, 0x25) : Color.FromRgb(0xE5, 0xE5, 0xE5);
        Color headerFore = fore;
        Color headerBorder = border;
        //Color tabBack = dark ? Color.FromRgb(0x25, 0x25, 0x25) : Color.FromRgb(0xDD, 0xDD, 0xDD);

        // Explicit box colors override the theme palette.
        if (TryColor(s.DefaultBoxBackColor, out var c))
        {
            back = c;
        }

        if (TryColor(s.DefaultBoxForeColor, out var c2))
        {
            fore = c2;
        }

        if (TryColor(s.DefaultBoxBorderColor, out var c3))
        {
            border = c3;
        }
        bool titleBarColorsSameAsBox = s.DefaultBoxTitleBarColorsSameAsBox;
        if (titleBarColorsSameAsBox)
        {
            // The header is painted on top of the box background, so making it transparent lets the
            // (single) box background show through — identical to the body, even when translucent.
            headerBack = Colors.Transparent;
            headerBorder = Colors.Transparent;
            headerFore = fore;
        }
        else
        {
            if (TryColor(s.DefaultBoxTitleBarBackColor, out var c4))
            {
                headerBack = c4;
            }

            if (TryColor(s.DefaultBoxTitleBarForeColor, out var c5))
            {
                headerFore = c5;
            }
        }

        double transparency = containerTransparencyOverride ?? s.DefaultBoxTransparencyValue;
        // A layered (AllowsTransparency) window is hit-tested by the DWM per pixel: fully
        // transparent (alpha 0) pixels are treated as "not the window" and mouse events fall
        // through to the desktop. Keep a tiny, imperceptible floor so the box stays interactive
        // even at "full transparency".
        double alpha = Clamp(1.0 - transparency, MinimumAlpha, 1.0);

        return new BoxPalette
        {
            Back = new SolidColorBrush(back) { Opacity = alpha },
            HeaderBack = new SolidColorBrush(headerBack),// { Opacity = titleBarColorsSameAsBox ? 1.0 : alpha },
            //TabBack = new SolidColorBrush(tabBack) { Opacity = alpha },
            Fore = new SolidColorBrush(fore),
            HeaderFore = new SolidColorBrush(headerFore),
            Border = new SolidColorBrush(border),
            HeaderBorder = new SolidColorBrush(headerBorder),
            Thickness = new Thickness(s.DefaultBoxBorderThickness ?? 1),
        };
    }

    public static bool ResolveDark(string? theme)
    {
        switch (theme?.Trim().ToLowerInvariant())
        {
            case "light":
                return false;
            case "dark":
                return true;
            default:
                try
                {
                    return ApplicationThemeManager.GetSystemTheme() != SystemTheme.Light;
                }
                catch
                {
                    return true;
                }
        }
    }

    public static bool TryColor(string? hex, out Color color)
    {
        color = Colors.Transparent;
        if (string.IsNullOrWhiteSpace(hex))
        {
            return false;
        }

        try
        {
            color = (Color)ColorConverter.ConvertFromString(hex);
            return true;
        }
        catch
        {
            return false;
        }
    }

    // Smallest non-zero alpha that keeps a layered window hit-testable while being invisible.
    private const double MinimumAlpha = 1.0 / 255.0;

    private static double Clamp(double v, double lo, double hi) => v < lo ? lo : v > hi ? hi : v;
}
