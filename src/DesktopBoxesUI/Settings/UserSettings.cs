using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Text;

namespace DesktopBoxesUI.Settings;

public class UserSettings
{
    /// <summary>
    /// App Selected Theme Dark, light, null(as System)
    /// </summary>
    [Category("Appearance"), DefaultValue(null)]
    public string? SelectedTheme { get; set; }


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

}


//public class ColorsProps
//{
//    public string? BackColor { get; set; }
//    public string? ForeColor { get; set; }
//}