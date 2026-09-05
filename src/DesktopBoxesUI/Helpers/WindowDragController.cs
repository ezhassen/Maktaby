using DesktopBoxesUI.Core.Interfaces;
using DesktopBoxesUI.Core.Models;
using DesktopBoxesUI.Views;
using DesktopBoxesUI.Views.Containers;
using DesktopBoxesUI.Win32.NativeMethods;
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;

namespace DesktopBoxesUI.Helpers;

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
    private const int WmEnterSizeMove = 0x0231;
    private const int WmWindowPosChanging = 0x0046;
    private const int WmMouseActivate = 0x0021;
    private const int MaActivate = 1;
    private const int MaNoActivate = 3;
    private const uint SwpNoSendChanging = 0x0400;
    private const uint SwpNoZOrder = 0x0004;
    private const uint SwpNoActivate = 0x0010;
    private const uint SwpNoMove = 0x0002;
    private const uint SwpNoSize = 0x0001;
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
    private readonly DesktopManager _desktopManager;
    private readonly Action<RectD> _setBounds;
    private readonly Func<List<RectD>> _getOthers;
    private readonly Action _onChanged;
    private readonly Func<double> _getHeaderHeight;
    private readonly Func<bool>? _getIsLocked;
    private HwndSource? _source;
    private SnapOverlay? _overlay;
    private bool _dragging;
    private bool _dragPending;
    private Point _dragStartPoint;
    private Point _dragOffset;
    private bool _enableGuides = true;

    /// <summary>
    /// When false, snap guide overlays are suppressed (e.g. while a container is being dragged onto
    /// another to merge, so only the drop-target highlight shows).
    /// </summary>
    public bool EnableGuides
    {
        get => _enableGuides;
        set => _enableGuides = value;
    }

    private readonly bool _handleHitTest;
    private readonly bool _handleMouseActivate;
    private readonly bool _handleKeepBelow;

    public WindowDragController(
        Window window,
        DesktopManager desktopManager,
        IMonitorService monitor,
        IDpiService dpi,
        IWindowSnappingService snapping,
        IWindowPositioningService positioning,
        Action<RectD> setBounds,
        Func<List<RectD>> getOthers,
        Action onChanged,
        Func<double> getHeaderHeight, Func<bool>? getIsLocked = null,
        bool handleHitTest = true,
        bool handleMouseActivate = true,
        bool handleKeepBelow = true)
    {
        _window = window;
        _monitor = monitor;
        _dpi = dpi;
        _snapping = snapping;
        _positioning = positioning;
        _setBounds = setBounds;
        _getOthers = getOthers;
        _onChanged = onChanged;
        _getHeaderHeight = getHeaderHeight;
        _getIsLocked = getIsLocked;
        _desktopManager = desktopManager;
        _handleHitTest = handleHitTest;
        _handleMouseActivate = handleMouseActivate;
        _handleKeepBelow = handleKeepBelow;
    }

    public void Attach(bool glueToDesktop = true)
    {
        try
        {
            var hwnd = new WindowInteropHelper(_window).Handle;
            if (glueToDesktop)
            {
                // Own the box to the DesktopSurface when the custom surface is live (handle published);
                // owned windows always float above their owner, so a box can never sink below (or lose
                // clicks/activation to) the surface. Falls back to Progman when no surface exists.
                Win32Apis.GlueToDesktop(hwnd, Win32Apis.DesktopSurfaceHandle);
            }
            Win32Apis.PreventMinimize(hwnd);
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

    /// <summary>True while a native move/size modal loop (WM_ENTERSIZEMOVE..WM_EXITSIZEMOVE) is
    /// running for ANY container window. Item drag initiation in BoxControl checks this so a resize
    /// gesture can never morph into dragging a shortcut icon.</summary>
    public static bool IsNativeSizing;

    public static Window? DraggingSourceWindow;

    /// <summary>Bumped whenever a native shell context menu opens/closes in any window.
    /// Item-drag arming records the epoch at MouseDown and MouseMove requires a match, so a
    /// press eaten by the menu's modal loop can never start a drag from a stale origin.</summary>
    public static long InputEpoch;

    public static void InvalidateItemDrags() => Interlocked.Increment(ref InputEpoch);

    public static long CurrentInputEpoch => Volatile.Read(ref InputEpoch);

    /// <summary>Tick (<see cref="Environment.TickCount"/>) when the last native shell menu closed.
    /// The press that dismisses the menu is physically older but delivered to WPF afterwards —
    /// arming sites compare <see cref="System.Windows.Input.InputEventArgs.Timestamp"/> against
    /// this to recognise (and not arm from) that eaten press.</summary>
    public static int MenuCloseTick;

    public static void NoteMenuClosed()
    {
        MenuCloseTick = Environment.TickCount;
        InvalidateItemDrags();
    }

    public static bool IsFreshPress(System.Windows.Input.MouseButtonEventArgs e)
        => unchecked(e.Timestamp - Volatile.Read(ref MenuCloseTick)) > 0;

    private IntPtr HwndHook(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        switch (msg)
        {
            case WmMouseActivate:
                if (!_handleMouseActivate) return IntPtr.Zero;
                // Activate on any click but don't bring above normal apps — keep in desktop layer
                handled = true;
                return (IntPtr)MaActivate;

            case WmNcHitTest:
                if (!_handleHitTest) return IntPtr.Zero;
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
                IsNativeSizing = true;
                WmSizing(wParam, lParam, hwnd);
                return IntPtr.Zero;

            case WmEnterSizeMove:
                IsNativeSizing = true;
                return IntPtr.Zero;

            case WmExitSizeMove:
                IsNativeSizing = false;
                OnExitSizeMove(hwnd);
                return IntPtr.Zero;

            case WmWindowPosChanging:
                SuppressShellSnap(lParam);
                if (_handleKeepBelow) KeepBelowApps(hwnd, lParam, _desktopManager);
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
        var final = ClampResize(result.Rect, screen, edge, minW, minH);

        WriteRect(lParam, final);
        ShowGuides(result.Guides, hwnd);
    }

    private void OnExitSizeMove(IntPtr hwnd)
    {
        _overlay?.HideGuides();
        _setBounds(RectD.FromXYWH(_window.Left, _window.Top, _window.Width, _window.Height));
        _onChanged();
    }
    public bool CanMoveWindow()
    {
        if (_getIsLocked is null) return true;
        return !_getIsLocked();
    }

    private int HitTest(IntPtr lParam, IntPtr hwnd)
    {
        // Non-resizable windows (locked widgets or fixed-size dialogs) should not show resize handles
        if (_window.ResizeMode == ResizeMode.NoResize || _window.ResizeMode == ResizeMode.CanMinimize)
            return 1;
        if (!CanMoveWindow()) return 1;

        int x = (short)(lParam.ToInt32() & 0xFFFF);
        int y = (short)((lParam.ToInt32() >> 16) & 0xFFFF);

        var b = _positioning.GetBounds(hwnd);
        double scale = GetScale(hwnd);
        int e = (int)Math.Max(4, 8 * scale);

        double headerH = _getHeaderHeight() * scale;
        bool inHeader = y < b.Y + headerH;

        // Outer strips ALWAYS resize — including the top strip across the header. Previously the
        // header consumed the whole top band (top required !inHeader), so the window could not be
        // resized from its top edge at all. Edge/corner priority over header matches every native app.
        bool left = x <= b.X + e;
        bool right = x >= b.Right - e;
        bool top = y <= b.Y + e;
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

    internal static void SuppressShellSnap(IntPtr lParam)
    {
        var wp = Marshal.PtrToStructure<WindowPos>(lParam);
        wp.Flags |= SwpNoSendChanging;
        Marshal.StructureToPtr(wp, lParam, false);
    }

    internal static void KeepBelowApps(IntPtr hwnd, IntPtr lParam, DesktopManager desktopManager, IntPtr? parentWindowH = null)
    {
        var wp = Marshal.PtrToStructure<WindowPos>(lParam);
        bool noZOrder = (wp.Flags & SwpNoZOrder) != 0;
        bool noMove = (wp.Flags & SwpNoMove) != 0;
        bool noSize = (wp.Flags & SwpNoSize) != 0;
        // Only pure z-order activation (no move/size) should be kept in desktop layer.
        // Move/resize (WM_ENTERSIZEMOVE, WM_MOVING, WM_SIZING) clears NOMOVE/NOSIZE and must be allowed.
        // Note: noActivate is intentionally NOT checked — CssWidget overlay is WS_EX_NOACTIVATE and
        // activates owner via MA_NOACTIVATE, so pure z-order with SWP_NOACTIVATE must still be kept below.
        if (noZOrder || !noMove || !noSize) return;

        bool toTop = wp.HwndInsertAfter == (IntPtr)0; // HWND_TOP
        bool isAppWindow = wp.HwndInsertAfter != IntPtr.Zero && !desktopManager.IsDesktopWindow(wp.HwndInsertAfter);
        if (toTop || isAppWindow)
        {
            IntPtr surface;
            if (parentWindowH.HasValue)
            {
                surface = parentWindowH.Value;
            }
            else
            {
                surface = Win32Apis.DesktopSurfaceHandle;
                if (surface == IntPtr.Zero) surface = Win32Apis.GetDesktopAnchorHandle();
            }

            IntPtr topDesktop = surface;
            if (surface != IntPtr.Zero)
            {
                IntPtr cur = ManualApis.GetWindow(surface, ManualApis.GW_HWNDNEXT);
                IntPtr lastDesktop = surface;
                while (cur != IntPtr.Zero)
                {
                    if (desktopManager.IsDesktopWindow(cur)) lastDesktop = cur;
                    else break;
                    cur = ManualApis.GetWindow(cur, ManualApis.GW_HWNDNEXT);
                }
                topDesktop = lastDesktop;
            }
            wp.HwndInsertAfter = topDesktop;
            Marshal.StructureToPtr(wp, lParam, false);
        }
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

    /// <summary>
    /// Hard-stops a RESIZE at the monitor work area, anchoring the OPPOSITE edge. The old
    /// move-semantics clamp (clamp origin, preserve size) made left/bottom resizes run away: each
    /// clamped frame pushed the opposite edge outward by the clamped amount, and the next native
    /// frame inherited that bigger width/height — continuous growth at screen/taskbar edges.
    /// </summary>
    private static RectD ClampResize(RectD r, RectD wa, ResizeEdge edge, double minW, double minH)
    {
        bool mL = edge is ResizeEdge.Left or ResizeEdge.TopLeft or ResizeEdge.BottomLeft;
        bool mR = edge is ResizeEdge.Right or ResizeEdge.TopRight or ResizeEdge.BottomRight;
        bool mT = edge is ResizeEdge.Top or ResizeEdge.TopLeft or ResizeEdge.TopRight;
        bool mB = edge is ResizeEdge.Bottom or ResizeEdge.BottomLeft or ResizeEdge.BottomRight;

        double l = r.X, t = r.Y, rt = r.Right, b = r.Bottom;

        if (mL) l = Math.Max(wa.X, Math.Min(l, rt - minW));
        if (mR) rt = Math.Min(wa.Right, Math.Max(rt, l + minW));
        if (mT) t = Math.Max(wa.Y, Math.Min(t, b - minH));
        if (mB) b = Math.Min(wa.Bottom, Math.Max(b, t + minH));

        return RectD.FromXYWH(l, t, rt - l, b - t);
    }

    private (RectD Screen, List<RectD> Others) GetSnapTargets(RectD moving, IntPtr hwnd)
    {
        var scale = GetScale(hwnd);

        // App constraint: the working area is the PRIMARY screen (the DesktopSurface covers it) —
        // every box must stay contained within it. Snap AND clamp against that single work area
        // (taskbar excluded); multi-monitor support would relax this later.
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
        if (!_enableGuides)
        {
            _overlay?.HideGuides();
            return;
        }

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

    /// <summary>True once an actual move drag has started (past the movement threshold).</summary>
    public bool IsDragging => _dragging;

    public void BeginTitleDrag(MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Left)
        {
            return;
        }
        if (!CanMoveWindow()) return;
        // Don't start dragging yet: wait until the pointer actually moves. This lets a plain click or a
        // double-click (e.g. to rename the title) happen without hijacking the gesture.
        _dragPending = true;
        _dragStartPoint = e.GetPosition(_window);
    }

    public void TitleDrag(MouseEventArgs e)
    {
        if (!_dragPending && !_dragging)
        {
            return;
        }

        if (_dragPending)
        {
            if (e.LeftButton != MouseButtonState.Pressed)
            {
                _dragPending = false;
                return;
            }

            if (!_dragging)
            {
                var current = e.GetPosition(_window);
                if ((current - _dragStartPoint).Length < 4)
                {
                    return; // not moved far enough to begin a drag yet
                }

                _dragging = true;
                DraggingSourceWindow = _window;
                _dragOffset = current;
                _window.CaptureMouse();
                Mouse.OverrideCursor = Cursors.SizeAll;
            }
        }

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
        _dragPending = false;

        if (!_dragging)
        {
            return;
        }

        _dragging = false;
        var src = DraggingSourceWindow;
        if (DraggingSourceWindow == _window) DraggingSourceWindow = null;
        _window.ReleaseMouseCapture();
        _overlay?.HideGuides();
        Mouse.OverrideCursor = null;
        _setBounds(RectD.FromXYWH(_window.Left, _window.Top, _window.Width, _window.Height));
        _onChanged();
        (src as WidgetWindow)?.UpdateChrome();
        (_window as WidgetWindow)?.UpdateChrome();
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
