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
    public event EventHandler? ResetRequested;
    public event EventHandler? SettingsRequested;
    public event EventHandler<string?>? ThemeRequested;
    public event EventHandler? ExitRequested;
    public event EventHandler? ToggleHideAllRequested;
    public event EventHandler? DebugTreeRequested;

    public TrayIconUI()
    {
        InitializeComponent();
        Icon = LoadTrayIcon()!;
        contextMenu.Opened += ContextMenu_Opened;
    }

    private void ContextMenu_Opened(object sender, RoutedEventArgs e)
    {
        var dtMan = App.Services.GetRequiredService<DesktopManager>();
        ISettingsService _settingsService = App.Services.GetRequiredService<ISettingsService>();
        MenuHideAll.Header = dtMan.AllBoxesHidden ? "Show All Boxes" : "Hide All Boxes";
        ToggleDebugTree.Visibility = GlobalFeaturesSwitches.ShowDebugTree ? Visibility.Visible : Visibility.Collapsed;
        resetMenuItem.Visibility = GlobalFeaturesSwitches.TrayIcon_ShowReset ? Visibility.Visible : Visibility.Collapsed;
        menu_theme_system.IsChecked = _settingsService.UserSettings.SelectedTheme_IsSystem();
        menu_theme_light.IsChecked = _settingsService.UserSettings.SelectedTheme_IsLight();
        menu_theme_dark.IsChecked = _settingsService.UserSettings.SelectedTheme_IsDark();
    }

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

    private void Reset_Click(object sender, RoutedEventArgs e) => ResetRequested?.Invoke(this, EventArgs.Empty);

    private void Settings_Click(object sender, RoutedEventArgs e) => SettingsRequested?.Invoke(this, EventArgs.Empty);

    private void Exit_Click(object sender, RoutedEventArgs e) => ExitRequested?.Invoke(this, EventArgs.Empty);

    private void HideAll_Click(object sender, RoutedEventArgs e) => ToggleHideAllRequested?.Invoke(this, EventArgs.Empty);

    private void DebugTree_Click(object sender, RoutedEventArgs e) => DebugTreeRequested?.Invoke(this, EventArgs.Empty);

    /// <summary>Reflects the current "all boxes hidden" state in the tray menu's check box.</summary>
    public void SetHideAllChecked(bool hidden) => MenuHideAll.IsChecked = hidden;

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
}
