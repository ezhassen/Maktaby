using DesktopBoxesUI.Core.Interfaces;
using DesktopBoxesUI.Core.Models;
using DesktopBoxesUI.ViewModels;
using DesktopBoxesUI.Win32.NativeMethods;
using Microsoft.Extensions.DependencyInjection;
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;

namespace DesktopBoxesUI.Views;

/// <summary>
/// One borderless, transparent window per <see cref="Box"/>. Hosts the <see cref="Controls.BoxControl"/>
/// for its items. Movement is driven by dragging the title area; resizing is provided natively by
/// answering <c>WM_NCHITTEST</c> on the window edges (no per-item or grip hack). While moving or
/// resizing, the window snaps to the current screen bounds and to other Boxes' bounds, drawing guide
/// lines via a transparent <see cref="SnapOverlay"/>.
/// </summary>
[SupportedOSPlatform("windows10.0.14393")]
public partial class BoxWindow : Window
{
    private const int WmNcHitTest = 0x0084;
    private const int WmMovingMsg = 0x0216;
    private const int WmSizingMsg = 0x0214;
    private const int WmExitSizeMove = 0x0232;
    private const int WmWindowPosChanging = 0x0046;
    private const uint SwpNoSendChanging = 0x0400;
    private const int HtLeft = 10;
    private const int HtRight = 11;
    private const int HtTop = 12;
    private const int HtTopLeft = 13;
    private const int HtTopRight = 14;
    private const int HtBottom = 15;
    private const int HtBottomLeft = 16;
    private const int HtBottomRight = 17;

    /// <summary>Gap (in DIPs) kept between this Box and another Box when snapping.</summary>
    private const double SnapPaddingDip = 10;

    private readonly MainViewModel _host;
    private readonly IWindowPositioningService _positioning;
    private readonly IMonitorService _monitor;
    private readonly IDpiService _dpi;
    private readonly IWindowSnappingService _snapping;
    private readonly BoxViewModel _box;
    private readonly System.Action _save;

    private HwndSource? _source;
    private SnapOverlay? _overlay;
    private bool _dragging;
    private Point _dragOffset;

    public BoxWindow(BoxViewModel box, MainViewModel host, IWindowPositioningService positioning, System.Action save)
    {
        InitializeComponent();

        _box = box;
        _host = host;
        _positioning = positioning;
        _save = save;
        _monitor = App.Services.GetRequiredService<IMonitorService>();
        _dpi = App.Services.GetRequiredService<IDpiService>();
        _snapping = App.Services.GetRequiredService<IWindowSnappingService>();

        DataContext = box;
        BoxContent.Host = host;
        BoxContent.RequestSave = save;

        ApplyTransparency();

        Left = box.Left;
        Top = box.Top;
        Width = box.Width;
        Height = box.Height;

        Loaded += OnLoaded;
        LocationChanged += OnLocationChanged;
        Closed += OnClosed;
    }

    private void OnLoaded(object? sender, RoutedEventArgs e)
    {
        try
        {
            var hwnd = new WindowInteropHelper(this).Handle;
            Win32Apis.GlueToDesktop(hwnd);
            Win32Apis.PreventMinimize(hwnd);
            _positioning.SetBounds(hwnd, new RectD(_box.Left, _box.Top, _box.Width, _box.Height));
            _source = HwndSource.FromHwnd(hwnd);
            _source.AddHook(HwndHook);
            _source.AddHook(Win32Apis.MinimizePreventionHook);
        }
        catch
        {
            // Positioning can fail if the handle isn't ready yet; the window still shows.
        }
    }

    private void OnClosed(object? sender, EventArgs e)
    {
        if (_source != null)
        {
            _source.RemoveHook(HwndHook);
            _source = null;
        }

        _overlay?.Close();
        _overlay = null;
    }

    private IntPtr HwndHook(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        switch (msg)
        {
            case WmNcHitTest:
                int ht = HitTest(lParam, hwnd);
                if (ht != 0)
                {
                    handled = true;
                    return (IntPtr)ht;
                }

                return IntPtr.Zero;

            case WmMovingMsg:
                WmMoving(lParam, hwnd);
                return IntPtr.Zero;

            case WmSizingMsg:
                WmSizing(wParam, lParam, hwnd);
                return IntPtr.Zero;

            case WmExitSizeMove:
                OnExitSizeMove(hwnd);
                return IntPtr.Zero;

            case WmWindowPosChanging:
                SuppressShellSnap(lParam);
                return IntPtr.Zero;

            default:
                return IntPtr.Zero;
        }
    }

    private void WmMoving(IntPtr lParam, IntPtr hwnd)
    {
        var r = Marshal.PtrToStructure<NcRect>(lParam);
        var moving = RectD.FromXYWH(r.Left, r.Top, r.Right - r.Left, r.Bottom - r.Top);
        var (screen, boxes) = GetSnapTargets(moving, hwnd);
        double threshold = 8 * GetScale(hwnd);
        double padding = SnapPaddingDip * GetScale(hwnd);
        var result = _snapping.SnapMove(moving, new[] { screen }, boxes, threshold, padding);

        WriteRect(lParam, ClampToScreen(result.Rect, screen));
        ShowGuides(result.Guides, hwnd);
    }

    private void WmSizing(IntPtr wParam, IntPtr lParam, IntPtr hwnd)
    {
        var edge = MapEdge(wParam.ToInt32());
        var r = Marshal.PtrToStructure<NcRect>(lParam);
        var moving = RectD.FromXYWH(r.Left, r.Top, r.Right - r.Left, r.Bottom - r.Top);
        var (screen, boxes) = GetSnapTargets(moving, hwnd);
        double threshold = 8 * GetScale(hwnd);
        double padding = SnapPaddingDip * GetScale(hwnd);
        double minW = MinWidth * GetScale(hwnd);
        double minH = MinHeight * GetScale(hwnd);

        var result = _snapping.SnapResize(moving, edge, new SizeD(minW, minH), new[] { screen }, boxes, threshold, padding);

        WriteRect(lParam, ClampToScreen(result.Rect, screen));
        ShowGuides(result.Guides, hwnd);
    }

    private void OnExitSizeMove(IntPtr hwnd)
    {
        _overlay?.HideGuides();

        // Keep the model in sync. OnLocationChanged already tracks Left/Top during a move, but the
        // box size is only finalized here (the OS has applied the resized rect to this window).
        _box.Left = Left;
        _box.Top = Top;
        _box.Width = Width;
        _box.Height = Height;
        _save();
    }

    private int HitTest(IntPtr lParam, IntPtr hwnd)
    {
        int x = (short)(lParam.ToInt32() & 0xFFFF);
        int y = (short)((lParam.ToInt32() >> 16) & 0xFFFF);

        var b = _positioning.GetBounds(hwnd);
        double scale = GetScale(hwnd);
        int e = (int)Math.Max(4, 8 * scale);

        double headerH = HeaderBorder.ActualHeight * scale;
        bool inHeader = y < b.Y + headerH;

        bool left = !inHeader && x <= b.X + e;
        bool right = !inHeader && x >= b.Right - e;
        bool top = !inHeader && y <= b.Y + e;
        bool bottom = y >= b.Bottom - e;

        if (!(left || right || top || bottom))
        {
            return 0;
        }

        if (left && top) return HtTopLeft;
        if (left && bottom) return HtBottomLeft;
        if (right && top) return HtTopRight;
        if (right && bottom) return HtBottomRight;
        if (left) return HtLeft;
        if (right) return HtRight;
        if (top) return HtTop;
        if (bottom) return HtBottom;
        return 0;
    }

    private static ResizeEdge MapEdge(int wParam) => wParam switch
    {
        1 => ResizeEdge.Left,
        2 => ResizeEdge.Right,
        3 => ResizeEdge.Top,
        4 => ResizeEdge.TopLeft,
        5 => ResizeEdge.TopRight,
        6 => ResizeEdge.Bottom,
        7 => ResizeEdge.BottomLeft,
        8 => ResizeEdge.BottomRight,
        _ => ResizeEdge.None,
    };

    /// <summary>Stops the shell from treating this window's drag as an Aero Snap / Snap-Assist target.</summary>
    private static void SuppressShellSnap(IntPtr lParam)
    {
        var wp = Marshal.PtrToStructure<WindowPos>(lParam);
        wp.Flags |= SwpNoSendChanging;
        Marshal.StructureToPtr(wp, lParam, false);
    }

    private double GetScale(IntPtr hwnd) => _dpi.GetDpiForWindow(hwnd) / 96.0;

    /// <summary>Keeps the rect fully within the screen bounds (primary monitor work area).</summary>
    private static RectD ClampToScreen(RectD rect, RectD screen)
    {
        double x = rect.Width >= screen.Width
            ? (screen.X + screen.Right - rect.Width) / 2d
            : Math.Max(screen.X, Math.Min(rect.X, screen.Right - rect.Width));
        double y = rect.Height >= screen.Height
            ? (screen.Y + screen.Bottom - rect.Height) / 2d
            : Math.Max(screen.Y, Math.Min(rect.Y, screen.Bottom - rect.Height));

        return RectD.FromXYWH(x, y, rect.Width, rect.Height);
    }

    private (RectD Screen, List<RectD> Boxes) GetSnapTargets(RectD moving, IntPtr hwnd)
    {
        var scale = GetScale(hwnd);
        // Boxes are confined to the primary screen, so the screen snap target is always the primary
        // work area (not the monitor the box happens to be on).
        var screen = _monitor.GetPrimaryWorkArea();
        var boxes = new List<RectD>();

        foreach (var other in _host.Boxes)
        {
            if (other == _box)
            {
                continue;
            }

            double l = other.Left * scale;
            double t = other.Top * scale;
            double r = (other.Left + other.Width) * scale;
            double b = (other.Top + other.Height) * scale;
            boxes.Add(RectD.FromXYWH(l, t, r - l, b - t));
        }

        return (screen, boxes);
    }

    private void ShowGuides(IReadOnlyList<GuideLine> guides, IntPtr hwnd)
    {
        if (guides.Count == 0)
        {
            _overlay?.HideGuides();
            return;
        }

        _overlay ??= new SnapOverlay();

        // Anchor the overlay to this Box's own monitor so the guide coordinates (computed in this
        // Box's DPI space) line up with what is on screen. A full-virtual-screen overlay would map
        // incorrectly on mixed-DPI setups.
        var bounds = _positioning.GetBounds(hwnd);
        var center = new PointD((bounds.X + bounds.Right) / 2, (bounds.Y + bounds.Bottom) / 2);
        var work = _monitor.GetWorkAreaContaining(center);
        double scale = GetScale(hwnd);
        _overlay.Left = work.X / scale;
        _overlay.Top = work.Y / scale;
        _overlay.Width = work.Width / scale;
        _overlay.Height = work.Height / scale;

        if (_overlay.Visibility != Visibility.Visible)
        {
            _overlay.Show();
        }

        double dipScale = 1.0 / scale;
        _overlay.SetGuides(guides, dipScale, work.X, work.Y);
    }

    private static void WriteRect(IntPtr lParam, RectD rect)
    {
        var r = new NcRect
        {
            Left = (int)Math.Round(rect.X),
            Top = (int)Math.Round(rect.Y),
            Right = (int)Math.Round(rect.Right),
            Bottom = (int)Math.Round(rect.Bottom),
        };
        Marshal.StructureToPtr(r, lParam, false);
    }

    /// <summary>
    /// Applies the box fill opacity from <see cref="BoxViewModel.Transparency"/>, falling back to
    /// the global default when it is null. A translucent fill lets the desktop show through.
    /// </summary>
    private void ApplyTransparency()
    {
        var settings = App.Services.GetRequiredService<ISettingsService>();
        double? global = settings.GetValue<double>(SettingsKeys.DefaultBoxTransparency);
        var effective = _box.Transparency ?? global ?? SettingsKeys.DefaultBoxTransparencyValue;
        var opacity = Math.Clamp(1.0 - effective, 0.0, 1.0);

        RootBorder.Background = new SolidColorBrush(Color.FromRgb(0x2D, 0x2D, 0x30)) { Opacity = opacity };
        HeaderBorder.Background = new SolidColorBrush(Color.FromRgb(0x3F, 0x3F, 0x46)) { Opacity = opacity };
    }

    private void OnLocationChanged(object? sender, EventArgs e)
    {
        _box.Left = Left;
        _box.Top = Top;
    }

    private void TitleArea_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (TitleEdit.Visibility == Visibility.Visible)
        {
            return;
        }

        if (e.ChangedButton == MouseButton.Left)
        {
            // Drag ourselves (mouse capture) instead of DragMove(): that keeps the OS out of the
            // move loop, so Aero Snap / Snap-Assist never engages. We still snap via our own service.
            _dragging = true;
            _dragOffset = e.GetPosition(this);
            TitleArea.CaptureMouse();
        }
    }

    private void TitleArea_MouseMove(object sender, MouseEventArgs e)
    {
        if (!_dragging)
        {
            return;
        }

        var hwnd = new WindowInteropHelper(this).Handle;
        double scale = GetScale(hwnd);

        // Keep everything in WPF DIPs: cursor position relative to this window plus the window's
        // own DIP location. Avoids PointToScreen, whose units don't match Window.Left under DPI scaling.
        var rel = e.GetPosition(this);
        double dipLeft = Left + (rel.X - _dragOffset.X);
        double dipTop = Top + (rel.Y - _dragOffset.Y);

        var moving = RectD.FromXYWH(dipLeft * scale, dipTop * scale, Width * scale, Height * scale);
        var (screen, boxTargets) = GetSnapTargets(moving, hwnd);
        double threshold = 8 * scale;
        double padding = SnapPaddingDip * scale;
        var result = _snapping.SnapMove(moving, new[] { screen }, boxTargets, threshold, padding);

        var clamped = ClampToScreen(result.Rect, screen);
        Left = clamped.X / scale;
        Top = clamped.Y / scale;
        ShowGuides(result.Guides, hwnd);
    }

    private void TitleArea_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (!_dragging)
        {
            return;
        }

        _dragging = false;
        TitleArea.ReleaseMouseCapture();
        _overlay?.HideGuides();
        _save();
    }

    private void TitleText_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount == 2)
        {
            BeginEdit();
        }
    }

    private void BeginEdit()
    {
        TitleText.Visibility = Visibility.Collapsed;
        TitleEdit.Visibility = Visibility.Visible;
        TitleEdit.Focus();
        TitleEdit.SelectAll();
    }

    private void CommitEdit()
    {
        TitleEdit.Visibility = Visibility.Collapsed;
        TitleText.Visibility = Visibility.Visible;
        _save();
    }

    private void TitleEdit_LostFocus(object sender, RoutedEventArgs e) => CommitEdit();

    private void TitleEdit_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            CommitEdit();
            e.Handled = true;
        }
        else if (e.Key == Key.Escape)
        {
            TitleEdit.Text = _box.Name;
            CommitEdit();
            e.Handled = true;
        }
    }

    private void MenuButton_Click(object sender, RoutedEventArgs e)
    {
        BoxMenu.PlacementTarget = MenuButton;
        BoxMenu.IsOpen = true;
    }

    private void MenuRename_Click(object sender, RoutedEventArgs e) => BeginEdit();

    private void MenuNew_Click(object sender, RoutedEventArgs e)
    {
        _host.CreateBox(Left + 30, Top + 30);
        _save();
    }

    private void MenuDelete_Click(object sender, RoutedEventArgs e)
    {
        _host.Boxes.Remove(_box);
        _save();
        Close();
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NcRect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WindowPos
    {
        public IntPtr Hwnd;
        public IntPtr HwndInsertAfter;
        public int X;
        public int Y;
        public int CX;
        public int CY;
        public uint Flags;
    }
}
