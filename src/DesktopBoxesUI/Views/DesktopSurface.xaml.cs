using System.Windows;
using System.Windows.Interop;
using DesktopBoxesUI.Core.Models;
using DesktopBoxesUI.ViewModels;
using DesktopBoxesUI.Win32.NativeMethods;
using DesktopBoxesUI.Win32.Services;

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

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        var helper = new WindowInteropHelper(this);
        var source = HwndSource.FromHwnd(helper.Handle);
        if (source != null)
        {
            source.AddHook(HwndHook);
            source.AddHook(Win32Apis.MinimizePreventionHook);
        }

        Win32Apis.GlueToDesktopSurface(helper.Handle);
        Win32Apis.PreventMinimize(helper.Handle);
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

    /// <summary>Raises/lowers this surface above the Explorer list-view so it becomes (or stops being)
    /// the OLE drop target during our own tab drag.</summary>
    internal void SetAboveList(bool above)
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        Win32Apis.RaiseDesktopSurface(hwnd, GetListView(), above);
    }

    private IntPtr HwndHook(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == 0x0084) // WM_NCHITTEST
        {
            handled = true;

            // While the right mouse button is held, become transparent to hits so the click falls
            // through to the real desktop and its context menu is shown. For every other case (hover,
            // left-drag, drops) we must report HTCLIENT explicitly: because this is a layered,
            // fully-transparent window, the default hit-test would otherwise return HTTRANSPARENT and
            // the surface would never be the drop target.
            if ((Win32Apis.GetAsyncKeyState(0x02) & 0x8000) != 0)
            {
                return (IntPtr)(-1); // HTTRANSPARENT
            }

            return (IntPtr)1; // HTCLIENT
        }

        // Forward genuine mouse interaction to the real desktop shell view so the surface behaves as if
        // the click penetrated: icon selection, double-click-to-open, rubber-band marquee selection and
        // wheel scrolling all keep working even though we sit above the list-view. The right button is
        // not forwarded because WM_NCHITTEST already reports HTTRANSPARENT for it, letting the context
        // menu reach Explorer directly. Skipped while an external drag is over us so drops are not
        // disturbed.
        if (!_dragging)
        {
            uint m = (uint)msg;
            if (m is 0x0200 or 0x0201 or 0x0202 or 0x0203 or 0x020A) // MOUSEMOVE / LBUTTONDOWN / LBUTTONUP / LBUTTONDBLCLK / MOUSEWHEEL
            {
                var listView = GetListView();
                if (listView != IntPtr.Zero)
                {
                    ManualApis.PostMessage(listView, m, wParam, lParam);
                }

                handled = true;
                return IntPtr.Zero;
            }
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
