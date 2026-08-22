using DesktopBoxesUI.Win32.NativeMethods;
using System;
using System.Runtime.Versioning;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using Windows.Win32.Foundation;
using Wpf.Ui;

namespace DesktopBoxesUI;

/// <summary>
/// Hosts the application in the Windows notification area (system tray) instead of a window. Shows a
/// tray icon and a context menu (New Box / Reset / Exit). A hidden, message-only Win32 window
/// receives the shell callback message. Native calls flow through <see cref="Win32Apis"/>.
/// </summary>
[SupportedOSPlatform("windows10.0.14393")]
[Obsolete("Use TrayIconUI instead")]
public sealed class TrayIconService : IDisposable
{
    private const int WM_TRAY_ICON = 0x8001; // WM_APP + 1
    private const uint NIM_ADD = 0, NIM_DELETE = 2;
    private const uint NIF_MESSAGE = 0x1, NIF_ICON = 0x2, NIF_TIP = 0x4;
    private const int WM_LBUTTONUP = 0x0202, WM_RBUTTONUP = 0x0205, WM_CONTEXTMENU = 0x007B;

    /// <summary>How many DIPs the tray menu overlaps the icon vertically (closes the gap, slight overlap).</summary>
    private const double IconVerticalOverlap = 3.0;

    private readonly HwndSource _host;
    private readonly IntPtr _iconHandle;
    private NOTIFYICONDATAW _data;

    public event EventHandler? NewBoxRequested;
    public event EventHandler? ResetRequested;
    public event EventHandler? SettingsRequested;
    public event EventHandler<string?>? ThemeRequested;
    public event EventHandler? ExitRequested;

    public TrayIconService()
    {
        _iconHandle = AcquireIcon();
        _host = CreateMessageWindow();
        _host.AddHook(WndProc);
        AddIcon();
    }

    private static IntPtr AcquireIcon()
    {
        var candidates = new[]
        {
            Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),
            Environment.GetFolderPath(Environment.SpecialFolder.Windows),
        };

        foreach (var path in candidates)
        {
            var psfi = new SHFILEINFOW();
            uint cb = (uint)System.Runtime.InteropServices.Marshal.SizeOf<SHFILEINFOW>();
            Win32Apis.SHGetFileInfo(path, 0, ref psfi, cb, SHGFI.Icon | SHGFI.LargeIcon | SHGFI.AddOverlays);
            if (psfi.hIcon != IntPtr.Zero)
            {
                return psfi.hIcon;
            }
        }

        return IntPtr.Zero;
    }

    private static HwndSource CreateMessageWindow()
    {
        var parameters = new HwndSourceParameters("DesktopBoxesTrayHost")
        {
            Width = 0,
            Height = 0,
            WindowStyle = 0,
        };

        // Parent to HWND_MESSAGE so the window is message-only (never visible, no taskbar entry).
        parameters.ParentWindow = (IntPtr)(-3);
        return new HwndSource(parameters);
    }

    private void AddIcon()
    {
        _data = new NOTIFYICONDATAW
        {
            cbSize = (uint)System.Runtime.InteropServices.Marshal.SizeOf<NOTIFYICONDATAW>(),
            hWnd = _host.Handle,
            uID = 1,
            uFlags = NIF_MESSAGE | NIF_ICON | NIF_TIP,
            uCallbackMessage = WM_TRAY_ICON,
            hIcon = _iconHandle,
            szTip = "DesktopBoxes",
        };

        Win32Apis.ShellNotifyIcon(NIM_ADD, ref _data);
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WM_TRAY_ICON && (uint)wParam == 1)
        {
            int mouseMessage = lParam.ToInt32();
            if (mouseMessage is WM_LBUTTONUP or WM_RBUTTONUP or WM_CONTEXTMENU)
            {
                ShowMenu();
                handled = true;
            }
        }

        return IntPtr.Zero;
    }

    private void AddThemeItem(MenuItem parent, string header, string? storedTheme)
    {
        var item = new MenuItem { Header = header };
        item.Click += (_, _) => ThemeRequested?.Invoke(this, storedTheme);
        parent.Items.Add(item);
    }

    private void ShowMenu()
    {
        var menu = new ContextMenu();

        var newBox = new MenuItem { Header = "New Box" };
        newBox.Click += (_, _) => NewBoxRequested?.Invoke(this, EventArgs.Empty);

        var reset = new MenuItem { Header = "Reset" };
        reset.Click += (_, _) => ResetRequested?.Invoke(this, EventArgs.Empty);

        var settings = new MenuItem { Header = "Settings" };
        settings.Click += (_, _) => SettingsRequested?.Invoke(this, EventArgs.Empty);

        var themeMenu = new MenuItem { Header = "Theme" };
        AddThemeItem(themeMenu, "System", null);
        AddThemeItem(themeMenu, "Dark", "dark");
        AddThemeItem(themeMenu, "Light", "light");

        var exit = new MenuItem { Header = "Exit" };
        exit.Click += (_, _) => ExitRequested?.Invoke(this, EventArgs.Empty);

        menu.Items.Add(newBox);
        menu.Items.Add(new Separator());
        menu.Items.Add(reset);
        menu.Items.Add(new Separator());
        menu.Items.Add(settings);
        menu.Items.Add(themeMenu);
        menu.Items.Add(new Separator());
        menu.Items.Add(exit);

        // Anchor the menu to the actual tray-icon rectangle. Because the app is DPI-aware, the shell
        // returns this rect already in logical (DIP) coordinates that match WPF's space — so it must NOT
        // be run through TransformFromDevice (that double-converts and flings the menu to the screen
        // center). The WPF input system doesn't track the cursor over the taskbar shell anyway, so the
        // icon rect is the reliable anchor. Fall back to the cursor only if the rect can't be read.
        Point anchor = new Point(double.NaN, double.NaN);
        if (Win32Apis.ShellNotifyIconGetRect(_host.Handle, _data.uID, out RECT icon))
        {
            anchor = new Point(icon.right, icon.top);
        }

        if (double.IsNaN(anchor.X))
        {
            Win32Apis.GetCursorPos(out ManualApis.POINT physical);
            anchor = new Point(physical.X, physical.Y);
            if (_host.CompositionTarget is { } ct)
            {
                anchor = ct.TransformFromDevice.Transform(anchor);
            }
        }

        // A ContextMenu overrides CustomPopupPlacementCallback with its own menu-placement logic, so the
        // custom callback is unreliable here (X only looked right because it lands near the cursor). Instead
        // use AbsolutePoint (screen coordinates) and set the offsets directly; refine them in Opened once
        // the menu is measured so we can right-align to the icon and overlap it by a few pixels.
        menu.Placement = PlacementMode.AbsolutePoint;
        menu.PlacementTarget = null;
        menu.HorizontalOffset = anchor.X;
        menu.VerticalOffset = anchor.Y;

        menu.Opened += (_, _) =>
        {
            menu.HorizontalOffset = anchor.X - menu.ActualWidth;
            menu.VerticalOffset = anchor.Y - menu.ActualHeight + IconVerticalOverlap;
        };

        // Open on the next dispatcher pass so it isn't dismissed by the same mouse message.
        Application.Current.Dispatcher.BeginInvoke(() => menu.IsOpen = true);
    }

    public void Dispose()
    {
        Win32Apis.ShellNotifyIcon(NIM_DELETE, ref _data);

        if (_iconHandle != IntPtr.Zero)
        {
            Win32Apis.DestroyIcon(_iconHandle);
        }

        _host.Dispose();
    }
}
