using System.ComponentModel;
using Wpf.Ui.Appearance;

namespace DesktopBoxesUI.Settings;

public class UserSettings
{
    /// <summary>
    /// App Selected Theme Dark, light, null(as System)
    /// </summary>
    [Category("Appearance"), DefaultValue(null)]
    public string? SelectedTheme { get; set; }

    public ApplicationTheme GetSelectedTheme()
    {
        switch (SelectedTheme?.Trim().ToLowerInvariant())
        {
            case "light":
                return ApplicationTheme.Light;
            case "dark":
                return ApplicationTheme.Dark;
            default:
                return ApplicationTheme.Unknown;
        }
    }

    public bool SelectedTheme_IsSystem()
    {
        return GetSelectedTheme() switch
        {
            ApplicationTheme.Light or ApplicationTheme.Dark => false,
            _ => true
        };
    }

    public bool SelectedTheme_IsLight()
    {
        return GetSelectedTheme() switch
        {
            ApplicationTheme.Light => true,
            _ => false
        };
    }

    public bool SelectedTheme_IsDark()
    {
        return GetSelectedTheme() switch
        {
            ApplicationTheme.Dark => true,
            _ => false
        };
    }

    /// <summary>
    /// Global default box transparency, 0 = fully opaque .. 1 = fully transparent.
    /// Used when a <see cref="Core.Models.Box"/> does not override it.
    /// </summary>
    [Category("Appearance_Boxes"), DefaultValue(0.5d)]
    public double DefaultBoxTransparencyValue { get; set; } = 0.5d;

    #region Default box theme overrides

    /// <summary>
    /// Default box Selected Theme, dark, light, (null= get app SelectedTheme)
    /// </summary>
    [Category("Appearance_Boxes"), DefaultValue(null)]
    public string? DefaultBoxTheme { get; set; }

    [Category("Appearance_Boxes"), DefaultValue(null)]
    public string? DefaultBoxBackColor { get; set; }

    [Category("Appearance_Boxes"), DefaultValue(null)]
    public string? DefaultBoxTitleBarBackColor { get; set; }

    [Category("Appearance_Boxes"), DefaultValue(null)]
    public string? DefaultBoxForeColor { get; set; }

    [Category("Appearance_Boxes"), DefaultValue(null)]
    public string? DefaultBoxTitleBarForeColor { get; set; }

    [Category("Appearance_Boxes"), DefaultValue(null)]
    public string? DefaultBoxBorderColor { get; set; }

    [Category("Appearance_Boxes"), DefaultValue(1)]
    public double? DefaultBoxBorderThickness { get; set; }

    /// <summary>
    /// When true the TitleBar colors (back, fore) uses Box Colors. (default to true)
    /// </summary>
    [Category("Appearance_Boxes"), DefaultValue(true)]
    public bool DefaultBoxTitleBarColorsSameAsBox { get; set; } = true;

    #endregion

    /// <summary>
    /// Default box item icon size min is 16 max 128. if null use desktop current icon size
    /// </summary>
    [Category("Appearance_Boxes"), DefaultValue(null)]
    public int? DefaultBoxIconSize { get; set; }


    #region CSSWidgets

    /// <summary>
    /// Default box Selected Theme, dark, light, (null= get app SelectedTheme)
    /// </summary>
    [Category("Appearance_CSSWidgets"), DefaultValue(null)]
    public string? DefaultCSSWidgetsTheme { get; set; }

    #endregion

    #region LiveWallpaper

    /// <summary>Full path of the live wallpaper video (.mp4). Null/empty = no wallpaper.</summary>
    [Category("LiveWallpaper"), DefaultValue(null)]
    public string? LiveWallpaperPath { get; set; }

    /// <summary>Master switch. When false all wallpaper windows are closed (settings kept).</summary>
    [Category("LiveWallpaper"), DefaultValue(true)]
    public bool LiveWallpaperEnabled { get; set; } = false;

    /// <summary>User Play/Pause intent. Auto-pause (fullscreen cover) is runtime-only.</summary>
    [Category("LiveWallpaper"), DefaultValue(true)]
    public bool LiveWallpaperPlaying { get; set; } = true;

    #endregion

}


//public class ColorsProps
//{
//    public string? BackColor { get; set; }
//    public string? ForeColor { get; set; }
//}