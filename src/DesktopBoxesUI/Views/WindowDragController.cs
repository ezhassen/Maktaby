using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using DesktopBoxesUI.Core.Interfaces;
using DesktopBoxesUI.Core.Models;
using DesktopBoxesUI.Win32.NativeMethods;

namespace DesktopBoxesUI.Views;

/// <summary>
/// Shared window behaviour for the desktop containers: native borderless move/resize via
/// <c>WM_NCHITTEST</c>, Win+D friendliness, and snap-to-screen / snap-to-other-containers with guide
/// lines. <see cref="BoxContainerWindow"/> (for every container type) uses this so the
/// hit-test / snap logic lives in exactly one place. Geometry is supplied through callbacks so the
/// helper stays agnostic of which view-model owns the bounds.
/// </summary>
[SupportedOSPlatform("windows10.0.14393")]
internal sealed class WindowDragController
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

    private const double SnapPaddingDip = 10;

    private readonly Window _window;
    private readonly IMonitorService _monitor;
    private readonly IDpiService _dpi;
    private readonly IWindowSnappingService _snapping;
    private readonly IWindowPositioningService _positioning;
    private readonly Func<RectD> _getBounds;
    private readonly Action<RectD> _setBounds;
    private readonly Func<List<RectD>> _getOthers;
    private readonly Action _onChanged;
    private readonly Func<double> _getHeaderHeight;

    private HwndSource? _source;
    private SnapOverlay? _overlay;
    private bool _dragging;
    private Point _dragOffset;

    public WindowDragController(
        Window window,
        IMonitorService monitor,
        IDpiService dpi,
        IWindowSnappingService snapping,
        IWindowPositioningService positioning,
        Func<RectD> getBounds,
        Action<RectD> setBounds,
        Func<List<RectD>> getOthers,
        Action onChanged,
        Func<double> getHeaderHeight)
    {
        _window = window;
        _monitor = monitor;
        _dpi = dpi;
        _snapping = snapping;
        _positioning = positioning;
        _getBounds = getBounds;
        _setBounds = setBounds;
        _getOthers = getOthers;
        _onChanged = onChanged;
        _getHeaderHeight = getHeaderHeight;
    }

    public void Attach()
    {
        try
        {
            var hwnd = new WindowInteropHelper(_window).Handle;
            Win32Apis.GlueToDesktop(hwnd);
            Win32Apis.PreventMinimize(hwnd);
            _positioning.SetBounds(hwnd, _getBounds());
            _source = HwndSource.FromHwnd(hwnd);
            _source.AddHook(HwndHook);
            _source.AddHook(Win32Apis.MinimizePreventionHook);
        }
        catch
        {
            // Positioning can fail if the handle isn't ready yet; the window still shows.
        }
    }

    public void Detach()
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
        var (screen, others) = GetSnapTargets(moving, hwnd);
        double threshold = 8 * GetScale(hwnd);
        double padding = SnapPaddingDip * GetScale(hwnd);
        var result = _snapping.SnapMove(moving, new[] { screen }, others, threshold, padding);

        WriteRect(lParam, ClampToScreen(result.Rect, screen));
        ShowGuides(result.Guides, hwnd);
    }

    private void WmSizing(IntPtr wParam, IntPtr lParam, IntPtr hwnd)
    {
        var edge = MapEdge(wParam.ToInt32());
        var r = Marshal.PtrToStructure<NcRect>(lParam);
        var moving = RectD.FromXYWH(r.Left, r.Top, r.Right - r.Left, r.Bottom - r.Top);
        var (screen, others) = GetSnapTargets(moving, hwnd);
        double threshold = 8 * GetScale(hwnd);
        double padding = SnapPaddingDip * GetScale(hwnd);
        double minW = _window.MinWidth * GetScale(hwnd);
        double minH = _window.MinHeight * GetScale(hwnd);

        var result = _snapping.SnapResize(moving, edge, new SizeD(minW, minH), new[] { screen }, others, threshold, padding);

        WriteRect(lParam, ClampToScreen(result.Rect, screen));
        ShowGuides(result.Guides, hwnd);
    }

    private void OnExitSizeMove(IntPtr hwnd)
    {
        _overlay?.HideGuides();
        _setBounds(RectD.FromXYWH(_window.Left, _window.Top, _window.Width, _window.Height));
        _onChanged();
    }

    private int HitTest(IntPtr lParam, IntPtr hwnd)
    {
        int x = (short)(lParam.ToInt32() & 0xFFFF);
        int y = (short)((lParam.ToInt32() >> 16) & 0xFFFF);

        var b = _positioning.GetBounds(hwnd);
        double scale = GetScale(hwnd);
        int e = (int)Math.Max(4, 8 * scale);

        double headerH = _getHeaderHeight() * scale;
        bool inHeader = y < b.Y + headerH;

        bool left = !inHeader && x <= b.X + e;
        bool right = !inHeader && x >= b.Right - e;
        bool top = !inHeader && y <= b.Y + e;
        bool bottom = y >= b.Bottom - e;

        if (!(left || right || top || bottom))
        {
            // Client area: let normal mouse handling (and our BeginTitleDrag on the header) work.
            return 1;
        }

        if (left && top) return HtTopLeft;
        if (left && bottom) return HtBottomLeft;
        if (right && top) return HtTopRight;
        if (right && bottom) return HtBottomRight;
        if (left) return HtLeft;
        if (right) return HtRight;
        if (top) return HtTop;
        if (bottom) return HtBottom;
        return 1;
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

    private static void SuppressShellSnap(IntPtr lParam)
    {
        var wp = Marshal.PtrToStructure<WindowPos>(lParam);
        wp.Flags |= SwpNoSendChanging;
        Marshal.StructureToPtr(wp, lParam, false);
    }

    private double GetScale(IntPtr hwnd) => _dpi.GetDpiForWindow(hwnd) / 96.0;

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

    private (RectD Screen, List<RectD> Others) GetSnapTargets(RectD moving, IntPtr hwnd)
    {
        var scale = GetScale(hwnd);
        var screen = _monitor.GetPrimaryWorkArea();
        var others = new List<RectD>();

        foreach (var other in _getOthers())
        {
            double l = other.X * scale;
            double t = other.Y * scale;
            double r = (other.X + other.Width) * scale;
            double b = (other.Y + other.Height) * scale;
            others.Add(RectD.FromXYWH(l, t, r - l, b - t));
        }

        return (screen, others);
    }

    private void ShowGuides(IReadOnlyList<GuideLine> guides, IntPtr hwnd)
    {
        if (guides.Count == 0)
        {
            _overlay?.HideGuides();
            return;
        }

        _overlay ??= new SnapOverlay();

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

    // --- Title-bar drag (mouse capture, our own snap loop) ---

    public void BeginTitleDrag(MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Left)
        {
            return;
        }

        _dragging = true;
        _dragOffset = e.GetPosition(_window);
        _window.CaptureMouse();
    }

    public void TitleDrag(MouseEventArgs e)
    {
        if (!_dragging)
        {
            return;
        }

        var hwnd = new WindowInteropHelper(_window).Handle;
        double scale = GetScale(hwnd);

        var rel = e.GetPosition(_window);
        double dipLeft = _window.Left + (rel.X - _dragOffset.X);
        double dipTop = _window.Top + (rel.Y - _dragOffset.Y);

        var moving = RectD.FromXYWH(dipLeft * scale, dipTop * scale, _window.Width * scale, _window.Height * scale);
        var (screen, others) = GetSnapTargets(moving, hwnd);
        double threshold = 8 * scale;
        double padding = SnapPaddingDip * scale;
        var result = _snapping.SnapMove(moving, new[] { screen }, others, threshold, padding);

        var clamped = ClampToScreen(result.Rect, screen);
        _window.Left = clamped.X / scale;
        _window.Top = clamped.Y / scale;
        ShowGuides(result.Guides, hwnd);
    }

    public void EndTitleDrag(MouseButtonEventArgs e)
    {
        if (!_dragging)
        {
            return;
        }

        _dragging = false;
        _window.ReleaseMouseCapture();
        _overlay?.HideGuides();
        _setBounds(RectD.FromXYWH(_window.Left, _window.Top, _window.Width, _window.Height));
        _onChanged();
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
