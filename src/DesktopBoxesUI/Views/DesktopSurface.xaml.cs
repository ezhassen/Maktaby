using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
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

        Win32Apis.GlueToDesktop(helper.Handle);
        Win32Apis.PreventMinimize(helper.Handle);
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

        // Forward genuine left-clicks to the real desktop shell view so it behaves as if the click
        // penetrated: any open context menu is dismissed and icon selection is cleared. Handled at the
        // message level (not via WPF mouse events) because this layered window with a manual HwndSource
        // hook does not reliably route MouseLeftButtonDown through the visual tree. Skipped while an
        // external drag is over us so drops are not disturbed.
        if ((msg == 0x0201 || msg == 0x0202) && !_dragging) // WM_LBUTTONDOWN / WM_LBUTTONUP
        {
            var listView = ExplorerDesktopService.FindDesktopListView();
            if (listView != IntPtr.Zero)
            {
                ManualApis.PostMessage(listView, (uint)msg, wParam, lParam);
            }

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
        var p = e.GetPosition(null);
        var wa = SystemParameters.WorkArea;
        double left = Math.Max(wa.Left, Math.Min(p.X, wa.Right - NewBoxWidth));
        double top = Math.Max(wa.Top, Math.Min(p.Y, wa.Bottom - NewBoxHeight));

        var container = _host.CreateBoxAt(left, top);
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
