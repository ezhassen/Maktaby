using DesktopBoxesUI.Core.Models;
using DesktopBoxesUI.ViewModels;
using DesktopBoxesUI.Win32.NativeMethods;
using DesktopBoxesUI.Win32.Services;
using Microsoft.Extensions.DependencyInjection;
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

    // Desktop root we must stay just above (Progman / the WorkerW hosting the shell). Set once the
    // window is glued; until then the WM_WINDOWPOSCHANGING guard leaves WPF's initial layout alone.
    private IntPtr _desktopRoot;

    // WPF windows are not created with CS_DBLCLKS, so WM_LBUTTONDBLCLK is never delivered. We detect a
    // double-click ourselves from two consecutive WM_LBUTTONDOWNs on empty desktop.
    private const int DoubleClickMs = 500;
    private int _lastDownTick;
    private int _lastDownX;
    private int _lastDownY;

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

    /// <summary>Re-covers the (possibly changed) primary work area after a display/DPI/resolution change.</summary>
    internal void Relayout()
    {
        Win32Apis.PositionSurfaceOverDesktop(new WindowInteropHelper(this).Handle);
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

        Win32Apis.DesktopSurfaceHandle = helper.Handle;
        Win32Apis.GlueToDesktopSurface(helper.Handle);
        _desktopRoot = Win32Apis.GetDesktopRootHandle();
        _listView = ExplorerDesktopService.FindDesktopListView();
    }

    private IntPtr GetListView()
    {
        if (_listView == IntPtr.Zero)
        {
            _listView = ExplorerDesktopService.FindDesktopListView();
        }

        return _listView;
    }
    private const int WM_NCHITTEST = 0x0084;
    private const int WM_MOUSEMOVE = 0x0200;
    private const int WM_LBUTTONDOWN = 0x0201;
    private const int WM_LBUTTONUP = 0x0202;
    private const int WM_LBUTTONDBLCLK = 0x0203;
    private const int WM_MOUSEWHEEL = 0x020A;
    private const int WM_WINDOWPOSCHANGING = 0x0046;
    private const int WM_MOUSEACTIVATE = 0x0021;
    private const int MA_NOACTIVATE = 3;
    private const int VK_RBUTTON = 0x02;
    private const int HTTRANSPARENT = -1;
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
    /// It forwards that input to the real shell list-view (so icon selection, marquee, wheel and
    /// double-click-to-open keep working) and only intercepts it for itself when the gesture is a
    /// double-click on EMPTY desktop (toggle hide-all). The right button is reported transparent so
    /// Explorer's context menu still reaches the desktop. While our own tab drag is over the surface
    /// the events are not forwarded so OLE drop handling proceeds.
    /// </summary>
    private IntPtr HwndHook(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WM_NCHITTEST)
        {
            // Right button passes through to Explorer so its context menu is shown.
            if ((Win32Apis.GetAsyncKeyState(VK_RBUTTON) & 0x8000) != 0)
            {
                handled = true;
                return (IntPtr)HTTRANSPARENT;
            }

            handled = true;
            return (IntPtr)HTCLIENT;
        }

        // Decline activation when a plain click lands on the surface. This is the real cause of the
        // surface jumping above app windows: a click activates it and promotes it to the foreground.
        // We only decline when we are NOT in the middle of our own drag — during an OLE drag the surface
        // must be allowed to activate so drag/drop can complete (returning MA_NOACTIVATE while dragging
        // previously broke drops). WS_EX_NOACTIVATE in glue covers the same case as a style, this is the
        // message-level backstop.
        if (msg == WM_MOUSEACTIVATE && !_dragging)
        {
            handled = true;
            return (IntPtr)MA_NOACTIVATE;
        }

        // WPF keeps trying to re-assert a normal top-level z-order and, on click/activation, raises the
        // surface above application windows. Intercept every reposition and force the insert-after back to
        // the real desktop root so we always stay just above the Explorer list-view and below app windows.
        // Lazily resolve _desktopRoot (it may be 0 if glue ran before the desktop was ready) and never
        // force with a zero handle — that previously produced HWND_TOP and broke the ordering.
        if (msg == WM_WINDOWPOSCHANGING)
        {
            if (_desktopRoot == IntPtr.Zero)
            {
                _desktopRoot = Win32Apis.GetDesktopRootHandle();
            }

            if (_desktopRoot != IntPtr.Zero)
            {
                var wp = System.Runtime.InteropServices.Marshal.PtrToStructure<WINDOWPOS>(lParam);
                wp.hwndInsertAfter = _desktopRoot;
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
        if (m is WM_MOUSEMOVE or WM_LBUTTONDOWN or WM_LBUTTONUP or WM_LBUTTONDBLCLK or WM_MOUSEWHEEL)
        {
            var listView = GetListView();
            if (listView == IntPtr.Zero)
            {
                return IntPtr.Zero;
            }

            // Double-click on EMPTY desktop toggles hide-all. WPF never sends WM_LBUTTONDBLCLK, so we
            // recognise it from two WM_LBUTTONDOWNs close in time and space. The first click is forwarded
            // (harmless on empty desktop); the second is intercepted so Explorer never sees it.
            if (m == WM_LBUTTONDOWN)
            {
                if (Win32Apis.GetCursorPos(out ManualApis.POINT pt)
                    && Win32Apis.IsDesktopEmptyPoint(listView, pt))
                {
                    int now = Environment.TickCount;
                    bool isDouble = _lastDownTick != 0
                        && now - _lastDownTick <= DoubleClickMs
                        && Math.Abs(_lastDownX - pt.X) <= 4
                        && Math.Abs(_lastDownY - pt.Y) <= 4;
                    if (isDouble)
                    {
                        _lastDownTick = 0;
                        handled = true;
                        App.Services.GetRequiredService<DesktopBoxesUI.DesktopManager>().ToggleHideAllBoxes();
                        return IntPtr.Zero;
                    }

                    _lastDownTick = now;
                    _lastDownX = pt.X;
                    _lastDownY = pt.Y;
                }
                else
                {
                    // A click on an icon (or elsewhere) must not pair with a later empty-desktop click.
                    _lastDownTick = 0;
                }
            }

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

    private void Surface_Drop(object sender, DragEventArgs e)
    {
        _dragging = false;

        // Internal tab move onto empty desktop: spin up a new container that holds the dragged box.
        if (e.Data.GetDataPresent(DndFormats.Box))
        {
            var box = (Box)e.Data.GetData(DndFormats.Box)!;
            var source = e.Data.GetData(DndFormats.SourceContainer) as ContainerViewModel;
            var p = e.GetPosition(null);
            var wa = SystemParameters.WorkArea;
            double left = Math.Max(wa.Left, Math.Min(p.X, wa.Right - NewBoxWidth));
            double top = Math.Max(wa.Top, Math.Min(p.Y, wa.Bottom - NewBoxHeight));

            _host.MoveBoxToNewContainer(box, source, left, top);
            e.Handled = true;
            _save();
            return;
        }

        var p2 = e.GetPosition(null);
        var wa2 = SystemParameters.WorkArea;
        double left2 = Math.Max(wa2.Left, Math.Min(p2.X, wa2.Right - NewBoxWidth));
        double top2 = Math.Max(wa2.Top, Math.Min(p2.Y, wa2.Bottom - NewBoxHeight));

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
