using System;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media.Imaging;
using Wpf.Ui.Controls;

namespace Maktaby.Views.AppWindows;

/// <summary>
/// Shared base for every <c>Views/AppWindows/</c> window. Applies the FluentWindow chrome
/// contract — <c>Mica</c> backdrop, extended titlebar, round corners — which is required
/// for backgrounds/controls to follow live app-theme switches (without it an open window
/// keeps the theme it was created with), plus the app icon.
/// Provides a prebuilt <see cref="TitleBar"/> (app icon included, title synced from
/// <see cref="Window.Title"/>): consumers host it in row 0 via a ContentControl and only
/// tweak <c>ShowMaximize</c>/<c>ShowMinimize</c> as needed — never redeclare the above.
/// </summary>
public class AppFluentWindow : FluentWindow
{
    private static BitmapImage? s_appIcon;

    /// <summary>Prebuilt title bar. Host it in the window's first row; the title tracks
    /// <see cref="Window.Title"/> automatically (including runtime changes).</summary>
    public TitleBar TitleBar { get; }

    public AppFluentWindow()
    {
        ExtendsContentIntoTitleBar = true;
        WindowBackdropType = WindowBackdropType.Mica;
        WindowCornerPreference = WindowCornerPreference.Round;

        s_appIcon ??= LoadAppIcon();
        if (s_appIcon != null)
        {
            Icon = s_appIcon;
        }

        TitleBar = new TitleBar
        {
            ShowMaximize = true,
            ShowMinimize = true
        };
        if (s_appIcon != null)
        {
            TitleBar.Icon = new ImageIcon { Source = s_appIcon };
        }
        TitleBar.SetBinding(TitleBar.TitleProperty, new Binding(nameof(Title)) { Source = this });
    }


    private static BitmapImage? LoadAppIcon()
    {
        try
        {
            var bmp = new BitmapImage();
            bmp.BeginInit();
            bmp.UriSource = new Uri("pack://application:,,,/Assets/app.ico", UriKind.Absolute);
            bmp.CacheOption = BitmapCacheOption.OnLoad;
            bmp.EndInit();
            bmp.Freeze();
            return bmp;
        }
        catch
        {
            return null;
        }
    }
}
