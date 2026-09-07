using DesktopBoxesUI.Core.Interfaces;
using DesktopBoxesUI.Win32.NativeMethods;
using Microsoft.Extensions.DependencyInjection;
using System;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Wpf.Ui.Tray.Controls;

namespace DesktopBoxesUI.Controls;

/// <summary>
/// System-tray icon implemented with WPF-UI's <see cref="NotifyIcon"/>. The control must live in a
/// visual tree (it is hosted by a hidden, always-on window created in <see cref="App"/>), which is how
/// WPF-UI's NotifyIcon registers and shows its context menu.
/// </summary>
public partial class TrayIconUI
{
    public event EventHandler? NewBoxRequested;
    public event EventHandler? NewBoxFolderPortalRequested;
    public event EventHandler? NewWidgetRequested;
    public event EventHandler? ManageWidgetsRequested;
    public event EventHandler? ResetRequested;
    public event EventHandler? SettingsRequested;
    public event EventHandler? AboutRequested;
    public event EventHandler<string?>? ThemeRequested;
    public event EventHandler? ExitRequested;
    public event EventHandler? ToggleHideAllRequested;
    public event EventHandler? DebugTreeRequested;
    public event EventHandler? MenuToggleDisableClick;
    public event EventHandler? LiveWallpaperToggleEnable;
    public event EventHandler? LiveWallpaperTogglePlayPause;
    public event EventHandler? LiveWallpaperChangeRequested;
    public event EventHandler? LiveWallpaperRemoveRequested;

    public TrayIconUI()
    {
        InitializeComponent();
        Icon = LoadTrayIcon()!;
        contextMenu.Opened += ContextMenu_Opened;
    }

    private void ContextMenu_Opened(object sender, RoutedEventArgs e)
    {
#if DEBUG
        menuTest.Visibility = GlobalFeaturesSwitches.TrayIcon_ShowTestButton ? Visibility.Visible : Visibility.Collapsed;
#else
    menuTest.Visibility = Visibility.Collapsed;
#endif

        var dtMan = App.Services.GetRequiredService<DesktopManager>();
        ISettingsService _settingsService = App.Services.GetRequiredService<ISettingsService>();
        MenuToggleHideAll.Header = dtMan.AllBoxesHidden ? "Show All Boxes" : "Hide All Boxes";
        // Icon per state — centralized in AppStyles.xaml (ItemIcon_HideAllBoxes_*)
        MenuToggleHideAll.Icon = Application.Current.FindResource(dtMan.AllBoxesHidden ? "ItemIcon_HideAllBoxes_Show" : "ItemIcon_HideAllBoxes_Hide") as object ?? MenuToggleHideAll.Icon;
        ToggleDebugTree.Visibility = GlobalFeaturesSwitches.ShowDebugTree ? Visibility.Visible : Visibility.Collapsed;
        resetMenuItem.Visibility = GlobalFeaturesSwitches.TrayIcon_ShowReset ? Visibility.Visible : Visibility.Collapsed;
        menu_theme_system.IsChecked = _settingsService.UserSettings.SelectedTheme_IsSystem();
        menu_theme_light.IsChecked = _settingsService.UserSettings.SelectedTheme_IsLight();
        menu_theme_dark.IsChecked = _settingsService.UserSettings.SelectedTheme_IsDark();
        //
        // Live Wallpaper submenu reflects manager state (independent from boxes).
        SyncMenuItemState_LiveWallPaperItems();
        //
        bool isDisabled = dtMan.IsDisabled;
        MenuToggleDisable.Header = isDisabled ? "Enable Desktop Boxes" : "Disable Desktop Boxes";
        MenuToggleDisable.Foreground = isDisabled
            ? System.Windows.Media.Brushes.LimeGreen
            : System.Windows.Media.Brushes.OrangeRed;
        // Icon per state — PlugConnected (Enable) vs PlugDisconnected (Disable)
        MenuToggleDisable.Icon = Application.Current.FindResource(isDisabled ? "ItemIcon_Enable" : "ItemIcon_Disable") as object ?? MenuToggleDisable.Icon;

        foreach (var item in contextMenu.Items)
        {
            // disable all menu items except MenuToggleDisable, exit, about, liveWallpaper
            if (item == MenuToggleDisable || item == menuExit || item == menuAbout || item == menuLiveWallpaper) continue;
            if (item is not MenuItem mItem) continue;
            mItem.IsEnabled = !isDisabled;
        }
    }

    #region Sync Menu Items States


    void SyncMenuItemState_LiveWallPaperItems()
    {
        // Live Wallpaper submenu reflects manager state (independent from boxes).
        var liveWallpaper = App.Services.GetService<LiveWallpaperManager>();
        menuLiveWallpaperEnable.IsChecked = liveWallpaper?.IsEnabled == true;
        bool hasWallpaper = liveWallpaper?.HasWallpaper == true && liveWallpaper?.IsEnabled == true;
        menuLiveWallpaperPlayPause.IsEnabled = hasWallpaper;
        menuLiveWallpaperPlayPause.Header = liveWallpaper?.IsPlaying == true ? "Pause" : "Play";
        menuLiveWallpaperPlayPause.Icon = Application.Current.FindResource(liveWallpaper?.IsPlaying == true ? "ItemIcon_Pause" : "ItemIcon_Play") as object;
        menuLiveWallpaperRemove.IsEnabled = liveWallpaper?.HasWallpaper == true;
    }

    #endregion

    /// <summary>Loads the app's own icon (largest embedded size) for the tray; falls back to the
    /// desktop folder glyph if the resource is unavailable.</summary>
    private static ImageSource? LoadTrayIcon()
    {
        try
        {
            var uri = new Uri("pack://application:,,,/Assets/app.ico");
            var decoder = new IconBitmapDecoder(uri, BitmapCreateOptions.None, BitmapCacheOption.OnLoad);
            return decoder.Frames.OrderByDescending(f => f.PixelHeight).First();
        }
        catch
        {
            // Fall through to the legacy desktop-folder icon.
        }

        var path = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
        var psfi = new SHFILEINFOW();
        uint cb = (uint)Marshal.SizeOf<SHFILEINFOW>();
        Win32Apis.SHGetFileInfo(path, 0, ref psfi, cb, SHGFI.Icon | SHGFI.LargeIcon | SHGFI.AddOverlays);
        if (psfi.hIcon == IntPtr.Zero)
        {
            return null;
        }

        try
        {
            return Imaging.CreateBitmapSourceFromHIcon(psfi.hIcon, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
        }
        finally
        {
            Win32Apis.DestroyIcon(psfi.hIcon);
        }
    }

    private void NewBox_Click(object sender, RoutedEventArgs e) => NewBoxRequested?.Invoke(this, EventArgs.Empty);
    private void NewBoxFolderPortal_Click(object sender, RoutedEventArgs e) => NewBoxFolderPortalRequested?.Invoke(this, EventArgs.Empty);
    private void NewWidget_Click(object sender, RoutedEventArgs e) => NewWidgetRequested?.Invoke(this, EventArgs.Empty);
    private void ManageWidgets_Click(object sender, RoutedEventArgs e) => ManageWidgetsRequested?.Invoke(this, EventArgs.Empty);

    private void Reset_Click(object sender, RoutedEventArgs e) => ResetRequested?.Invoke(this, EventArgs.Empty);

    private void Settings_Click(object sender, RoutedEventArgs e) => SettingsRequested?.Invoke(this, EventArgs.Empty);

    private void About_Click(object sender, RoutedEventArgs e) => AboutRequested?.Invoke(this, EventArgs.Empty);

    private void Exit_Click(object sender, RoutedEventArgs e) => ExitRequested?.Invoke(this, EventArgs.Empty);

    private void ToggleHideAll_Click(object sender, RoutedEventArgs e) => ToggleHideAllRequested?.Invoke(this, EventArgs.Empty);

    private void DebugTree_Click(object sender, RoutedEventArgs e) => DebugTreeRequested?.Invoke(this, EventArgs.Empty);

    private void LiveWallpaperEnable_Click(object sender, RoutedEventArgs e)
    {
        LiveWallpaperToggleEnable?.Invoke(this, EventArgs.Empty);
        // Menu stays open (StaysOpenOnClick), so refresh check/enabled state immediately.
        SyncMenuItemState_LiveWallPaperItems();
    }

    private void LiveWallpaperPlayPause_Click(object sender, RoutedEventArgs e)
    {
        LiveWallpaperTogglePlayPause?.Invoke(this, EventArgs.Empty);
        // Menu stays open (StaysOpenOnClick), so refresh Play/Pause header immediately.
        SyncMenuItemState_LiveWallPaperItems();
    }

    private void LiveWallpaperChange_Click(object sender, RoutedEventArgs e)
    {
        LiveWallpaperChangeRequested?.Invoke(this, EventArgs.Empty);
        // Menu stays open (StaysOpenOnClick); file dialog may have changed state.
        SyncMenuItemState_LiveWallPaperItems();
    }

    private void LiveWallpaperRemove_Click(object sender, RoutedEventArgs e)
    {
        LiveWallpaperRemoveRequested?.Invoke(this, EventArgs.Empty);
        // Menu stays open (StaysOpenOnClick), so refresh enabled state immediately.
        SyncMenuItemState_LiveWallPaperItems();
    }

    private void PerformanceMonitor_Click(object sender, RoutedEventArgs e)
    {
        Application.Current.Dispatcher.BeginInvoke(() =>
        {
            foreach (var w in Application.Current.Windows.OfType<Views.PerformanceMonitorWindow>())
            {
                if (w.WindowState == WindowState.Minimized) w.WindowState = WindowState.Normal;
                w.Activate();
                return;
            }
            new Views.PerformanceMonitorWindow().Show();
        });
    }

    private void Theme_Click(object sender, RoutedEventArgs e)
    {
        if (sender is MenuItem item)
        {
            ThemeRequested?.Invoke(this, item.Tag as string);
        }
    }

    private void AppTrayIcon_OnLeftClick(NotifyIcon sender, RoutedEventArgs e)
    {
        // No left-click action (matches prior behaviour); the menu opens on right click.
        //contextMenu.IsOpen = !contextMenu.IsOpen;
    }

    private void MenuToggleDisable_Click(object sender, RoutedEventArgs e)
    {
        MenuToggleDisableClick?.Invoke(this, EventArgs.Empty);
    }

    private void Test_Click(object sender, RoutedEventArgs e)
    {
        throw new Exception("Test Exception");
    }
}
