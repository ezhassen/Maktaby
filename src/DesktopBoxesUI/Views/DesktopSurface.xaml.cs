using DesktopBoxesUI.Core.Models;
using DesktopBoxesUI.ViewModels;
using DesktopBoxesUI.Win32.NativeMethods;
using DesktopBoxesUI.Win32.Services;
using Microsoft.Extensions.DependencyInjection;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Interop;

namespace DesktopBoxesUI.Views;

/// <summary>
/// Full-screen, transparent, click-through-visually-but-hit-testable window sitting behind all Box
/// windows. It owns the empty desktop area: dropping an item (from another Box, the Start Menu, or
/// File Explorer) onto empty space creates a new Box at the cursor and drops the item into it.
/// </summary>
public sealed partial class DesktopSurface : Window
{
    private const double NewBoxWidth = 240;
    private const double NewBoxHeight = 200;

    private readonly MainViewModel _host;
    private readonly System.Action _save;
    private bool _dragging;
    private IntPtr _listView;

    // Desktop-layer anchor we must stay just above: the top-level Progman / WorkerW hosting the live
    // desktop content (the SysListView32 list-view, or its SHELLDLL_DefView when icons are hidden).
    // Set once the window is glued; until then the WM_WINDOWPOSCHANGING guard leaves WPF's initial
    // layout alone. Re-resolved lazily (see HwndHook) so Explorer restarts and icon show/hide toggles
    // — which swap the inner topmost window — are picked up without any polling.
    private IntPtr _anchor;

    // --- Temporary z-order diagnostics (written to %TEMP%\dbx_surface.log; remove once stable) ---
    private bool _wasTopmost;
    private int _lastFightLogTick;

    private static void Trace(string message)
    {
        try
        {
            File.AppendAllText(
                Path.Combine(Path.GetTempPath(), "dbx_surface.log"),
                $"[{DateTime.Now:HH:mm:ss.fff}] {message}{Environment.NewLine}");
        }
        catch
        {
            // Diagnostics must never break the surface.
        }
    }

    private static string Describe(IntPtr hwnd)
        => hwnd == IntPtr.Zero ? "<none>" : $"{Win32Apis.GetWindowClass(hwnd)}(0x{hwnd.ToInt64():X})";

    public DesktopSurface(MainViewModel host, System.Action save)
    {
        InitializeComponent();
        _host = host;
        _save = save;

        // Cover the primary work area (excludes the taskbar) so the taskbar stays usable.
        Left = SystemParameters.WorkArea.Left;
        Top = SystemParameters.WorkArea.Top;
        Width = SystemParameters.WorkArea.Width;
        Height = SystemParameters.WorkArea.Height;

        Loaded += OnLoaded;
    }

    /// <summary>Re-covers the (possibly changed) primary work area after a display/DPI/resolution change
    /// and re-resolves the desktop anchor.</summary>
    internal void Relayout()
    {
        Win32Apis.PositionSurfaceOverDesktop(new WindowInteropHelper(this).Handle);
        _anchor = Win32Apis.GetDesktopAnchorHandle();
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        var helper = new WindowInteropHelper(this);
        var source = HwndSource.FromHwnd(helper.Handle);
        if (source != null)
        {
            source.AddHook(HwndHook);
            source.AddHook(Win32Apis.MinimizePreventionHook);
        }

        // WPF registers each window with its own class WITHOUT CS_DBLCLKS, so Windows never
        // synthesizes WM_LBUTTONDBLCLK for us (which historically forced manual double-click timing
        // in the hook). Each HwndWrapper class is unique to this window, so adding the style here
        // enables system double-click detection — correct timing/distance for free.
        int classStyle = ManualApis.GetClassLong(helper.Handle, ManualApis.GCL_STYLE);
        ManualApis.SetClassLong(helper.Handle, ManualApis.GCL_STYLE, classStyle | ManualApis.CS_DBLCLKS);

        Win32Apis.DesktopSurfaceHandle = helper.Handle;
        Win32Apis.GlueToDesktopSurface(helper.Handle);
        _anchor = Win32Apis.GetDesktopAnchorHandle();
        _listView = ExplorerDesktopService.FindDesktopListView();

        // Any box that attached before this handle was published fell back to a Progman owner and can
        // end up below us; re-own them now so owned-above-owner holds for every box from here on.
        foreach (var window in Application.Current.Windows.OfType<Window>().ToList())
        {
            if (window is BoxContainerWindow box)
            {
                var boxHwnd = new WindowInteropHelper(box).Handle;
                if (boxHwnd != IntPtr.Zero)
                {
                    Win32Apis.GlueToDesktop(boxHwnd, helper.Handle);
                }
            }
        }

        Trace($"glued: surface={Describe(helper.Handle)} anchor={Describe(_anchor)} "
            + $"prev={Describe(Win32Apis.GetWindow(helper.Handle, ManualApis.GW_HWNDPREV))} "
            + $"next={Describe(Win32Apis.GetWindow(helper.Handle, ManualApis.GW_HWNDNEXT))} "
            + $"topmost={(ManualApis.GetWindowLong(helper.Handle, ManualApis.GWL_EXSTYLE) & ManualApis.WS_EX_TOPMOST) != 0}");
    }

    private IntPtr GetListView()
    {
        if (_listView == IntPtr.Zero)
        {
            _listView = ExplorerDesktopService.FindDesktopListView();
        }

        return _listView;
    }

    // The SHELLDLL_DefView hosting the list-view — the window that owns the desktop context menu.
    private IntPtr _defView;

    private IntPtr GetDefView()
    {
        if (_defView == IntPtr.Zero || !Win32Apis.IsWindow(_defView))
        {
            _defView = ExplorerDesktopService.FindDesktopSHELLDLL_DefView();
        }

        return _defView;
    }
    private const int WM_NCHITTEST = 0x0084;
    private const int WM_CONTEXTMENU = 0x007B;
    private const int WM_MOUSEMOVE = 0x0200;
    private const int WM_LBUTTONDOWN = 0x0201;
    private const int WM_LBUTTONUP = 0x0202;
    private const int WM_LBUTTONDBLCLK = 0x0203;
    private const int WM_RBUTTONDOWN = 0x0204;
    private const int WM_RBUTTONUP = 0x0205;
    private const int WM_MOUSEWHEEL = 0x020A;
    private const int WM_WINDOWPOSCHANGING = 0x0046;
    private const int WM_SETCURSOR = 0x0020;
    private const int HTCLIENT = 1;

    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    private struct WINDOWPOS
    {
        public IntPtr hwnd;
        public IntPtr hwndInsertAfter;
        public int x;
        public int y;
        public int cx;
        public int cy;
        public uint flags;
    }

    /// <summary>
    /// The surface sits above the Explorer list-view and owns every mouse event on the empty desktop.
    /// It forwards that input to the real shell list-view (so icon selection, marquee, wheel,
    /// right-click context menu and double-click-to-open keep working) and only intercepts it for
    /// itself when the gesture is a double-click on EMPTY desktop (toggle hide-all). While our own tab
    /// drag is over the surface the events are not forwarded so OLE drop handling proceeds.
    /// </summary>
    private IntPtr HwndHook(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WM_NCHITTEST)
        {
            // Always claim the hit (HTCLIENT). The old HTTRANSPARENT-on-right-button trick cannot work:
            // HTTRANSPARENT only hands a hit to windows in the SAME thread, and Explorer's desktop
            // list-view lives in another process — so right-clicks silently vanished. Every button now
            // reaches us and is forwarded explicitly in the mouse-message block below.
            handled = true;
            return (IntPtr)HTCLIENT;
        }

        // The surface has no interactive edges, so the correct cursor over it is always the standard
        // arrow. Answering WM_SETCURSOR ourselves also guarantees the cursor normalises as soon as the
        // pointer leaves a box edge onto empty desktop.
        if (msg == WM_SETCURSOR)
        {
            Win32Apis.SetCursor(Win32Apis.LoadArrowCursor());
            handled = true;
            return (IntPtr)1;
        }

        // NOTE: no WM_MOUSEACTIVATE handling here. Returning MA_NOACTIVATE suppresses the activation an
        // OLE drop target needs (that broke drag/drop), and allowing activation is worse: with
        // WS_EX_NOACTIVATE restored after the focus experiment, DefWindowProc's default MA_ACTIVATE
        // never fires because the style declines mouse activation up front. The surface deliberately
        // never becomes the foreground window — empty-desktop input is forwarded to Explorer instead.

        // WPF keeps trying to re-assert a normal top-level z-order and, on click/activation, raises the
        // surface above application windows. Intercept every reposition and force the insert-after back
        // to the resolved desktop anchor so we always stay just above the live desktop content (the
        // Explorer list-view / its DefView host) and below app windows. The anchor is re-resolved lazily
        // when it dies or fails validation — never force with a zero or non-desktop handle, that would
        // let the surface escape to a high z-position.
        if (msg == WM_WINDOWPOSCHANGING)
        {
            var wp = System.Runtime.InteropServices.Marshal.PtrToStructure<WINDOWPOS>(lParam);

            if (_anchor == IntPtr.Zero || !Win32Apis.IsWindow(_anchor) || !Win32Apis.IsDesktopLayerTopLevel(_anchor))
            {
                _anchor = Win32Apis.GetDesktopAnchorHandle();
            }

            bool topmost = (ManualApis.GetWindowLong(hwnd, ManualApis.GWL_EXSTYLE) & ManualApis.WS_EX_TOPMOST) != 0;
            if (topmost != _wasTopmost)
            {
                _wasTopmost = topmost;
                Trace($"WS_EX_TOPMOST is now {topmost}");
            }

            if (_anchor != IntPtr.Zero)
            {
                if (wp.hwndInsertAfter != _anchor)
                {
                    // Something (WPF layout, activation, the shell) is trying to place us elsewhere —
                    // that "elsewhere" is exactly how the surface escapes above app windows. Log it
                    // (throttled) so we can see the culprit, then pin it back.
                    int now = Environment.TickCount;
                    if (now - _lastFightLogTick > 1000)
                    {
                        _lastFightLogTick = now;
                        Trace($"z-fight: requested insertAfter={Describe(wp.hwndInsertAfter)} -> pinning above {Describe(_anchor)}");
                    }
                }

                wp.hwndInsertAfter = _anchor;
                System.Runtime.InteropServices.Marshal.StructureToPtr(wp, lParam, true);
            }

            return IntPtr.Zero;
        }

        // Our own tab drag is over the surface: let OLE drop handling proceed, don't forward.
        if (_dragging)
        {
            return IntPtr.Zero;
        }

        uint m = (uint)msg;
        if (m is WM_MOUSEMOVE or WM_LBUTTONDOWN or WM_LBUTTONUP or WM_LBUTTONDBLCLK
            or WM_RBUTTONDOWN or WM_RBUTTONUP or WM_MOUSEWHEEL)
        {
            var listView = GetListView();
            if (listView == IntPtr.Zero)
            {
                return IntPtr.Zero;
            }

            // Right-click is handled entirely here, NOT forwarded. Reason: a forwarded
            // WM_RBUTTONDOWN makes the list-view SetCapture(), which reroutes the matching
            // WM_RBUTTONUP straight to SysListView32 — it never returns to us, and Explorer pairs our
            // stale-position posted DOWN with the real UP, placing the menu via that stale
            // GetMessagePos (the wrong-monitor bug). Instead: swallow the press; on the release post
            // an explicit WM_CONTEXTMENU with the exact cursor point.
            if (m == WM_RBUTTONDOWN)
            {
                handled = true;
                return IntPtr.Zero;
            }

            if (m == WM_RBUTTONUP)
            {
                // The shell shows a popup only while its thread is foreground (mirrors a physical
                // desktop right-click, where the desktop takes foreground); without this the menu can
                // refuse to open or dismiss instantly.
                //Win32Apis.SetForegroundWindow(listView);//SetForegroundWindow is not needed

                var defView = GetDefView();
                bool havePt = Win32Apis.GetCursorPos(out ManualApis.POINT pt);
                Trace($"RBUTTONUP -> CONTEXTMENU defView={Describe(defView)} pt=({pt.X},{pt.Y})");
                if (defView != IntPtr.Zero && havePt)
                {
                    ManualApis.PostMessage(defView, WM_CONTEXTMENU, listView, (IntPtr)(pt.X | (pt.Y << 16)));
                }

                handled = true;
                return IntPtr.Zero;
            }            // Double-click on EMPTY desktop toggles hide-all. CS_DBLCLKS is enabled on our window
            // class (see OnLoaded), so the system synthesizes this message with proper timing. A
            // double-click over an ICON falls through to the generic forward below, keeping the
            // native open behaviour intact.
            if (m == WM_LBUTTONDBLCLK)
            {
                if (Win32Apis.GetCursorPos(out ManualApis.POINT pt)
                    && Win32Apis.IsDesktopEmptyPoint(listView, pt))
                {
                    Trace("empty-desktop double-click -> ToggleHideAllBoxes");
                    App.Services.GetRequiredService<DesktopBoxesUI.DesktopManager>().ToggleHideAllBoxes();
                    handled = true;
                    return IntPtr.Zero;
                }
            }

            //TODO(coordinate-mapping): lParam holds SCREEN pixels but receivers treat mouse lParam as
            // CLIENT coords (desktop list-view client origin = virtual-screen origin, non-zero on
            // multi-monitor). Converting via ScreenToClient fixed phantom icon selections when icons
            // are SHOWN but broke other input when icons are HIDDEN — reverted pending investigation.
            ManualApis.PostMessage(listView, m, wParam, lParam);
            handled = true;
            return IntPtr.Zero;
        }

        return IntPtr.Zero;
    }

    private void Surface_DragOver(object sender, DragEventArgs e)
    {
        _dragging = true;
        e.Effects = DropHelper.GetEffect(e);
        e.Handled = true;
    }

    private void Surface_DragLeave(object sender, DragEventArgs e)
    {
        _dragging = false;
    }

    /// <summary>
    /// Drop point in screen DIPs. <see cref="DragEventArgs.GetPosition"/> now throws when
    /// <c>relativeTo</c> is null, so derive the point from the physical cursor position instead —
    /// that API is explicitly reliable during an active drag, and dividing by the surface's DPI gives
    /// logical coordinates matching the WorkArea clamping done by callers.
    /// </summary>
    private Point GetScreenPoint()
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        double scale = hwnd != IntPtr.Zero ? Win32Apis.GetDpiForWindow((Windows.Win32.Foundation.HWND)hwnd) / 96.0 : 1.0;
        if (Win32Apis.GetCursorPos(out ManualApis.POINT pt))
        {
            return new Point(pt.X / scale, pt.Y / scale);
        }

        return new Point(SystemParameters.WorkArea.Left, SystemParameters.WorkArea.Top);
    }

    private void Surface_Drop(object sender, DragEventArgs e)
    {
        // Exceptions thrown inside an OLE IDropTarget::Drop callback are swallowed by the drag modal
        // loop (no global handler catches them), so wrap everything and surface failures in the trace.
        try
        {
            DropCore(e);
        }
        catch (Exception ex)
        {
            _dragging = false;
            Trace($"Surface_Drop EXCEPTION: {ex}");
        }
    }

    private void DropCore(DragEventArgs e)
    {
        _dragging = false;

        try
        {
            Trace($"drop: formats=[{string.Join(", ", e.Data.GetFormats())}]");
        }
        catch
        {
            // Format enumeration is best-effort diagnostics only.
        }

        // Internal tab move onto empty desktop: spin up a new container that holds the dragged box.
        if (e.Data.GetDataPresent(DndFormats.Box))
        {
            var box = (Box)e.Data.GetData(DndFormats.Box)!;
            var source = e.Data.GetData(DndFormats.SourceContainer) as ContainerViewModel;
            var p = GetScreenPoint();
            var wa = SystemParameters.WorkArea;
            double left = Math.Max(wa.Left, Math.Min(p.X, wa.Right - NewBoxWidth));
            double top = Math.Max(wa.Top, Math.Min(p.Y, wa.Bottom - NewBoxHeight));

            Trace("drop: internal box tab move");
            _host.MoveBoxToNewContainer(box, source, left, top);
            e.Handled = true;
            _save();
            return;
        }

        var p2 = GetScreenPoint();
        var wa2 = SystemParameters.WorkArea;
        double left2 = Math.Max(wa2.Left, Math.Min(p2.X, wa2.Right - NewBoxWidth));
        double top2 = Math.Max(wa2.Top, Math.Min(p2.Y, wa2.Bottom - NewBoxHeight));

        Trace("drop: external item -> new box");
        var container = _host.CreateBoxAt(left2, top2);
        if (container.ActiveBox != null)
        {
            DropHelper.AddToBox(container.ActiveBox, _host, e);
        }

        if (e.Handled)
        {
            _save();
        }
    }
}
