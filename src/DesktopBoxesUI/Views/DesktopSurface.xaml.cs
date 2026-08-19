using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using DesktopBoxesUI.ViewModels;
using DesktopBoxesUI.Win32.NativeMethods;

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
        }
    }

    private IntPtr HwndHook(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == 0x0084) // WM_NCHITTEST
        {
            // While the right mouse button is held, become transparent to hits so the
            // click falls through to the real desktop and its context menu is shown.
            if ((Win32Apis.GetAsyncKeyState(0x02) & 0x8000) != 0)
            {
                handled = true;
                return (IntPtr)(-1); // HTTRANSPARENT
            }
        }

        return IntPtr.Zero;
    }

    private void Surface_DragOver(object sender, DragEventArgs e)
    {
        e.Effects = DropHelper.GetEffect(e);
        e.Handled = true;
    }

    private void Surface_Drop(object sender, DragEventArgs e)
    {
        var p = e.GetPosition(null);
        var wa = SystemParameters.WorkArea;
        double left = Math.Max(wa.Left, Math.Min(p.X, wa.Right - NewBoxWidth));
        double top = Math.Max(wa.Top, Math.Min(p.Y, wa.Bottom - NewBoxHeight));

        var box = _host.CreateBoxAt(left, top);
        DropHelper.AddToBox(box, _host, e);

        if (e.Handled)
        {
            _save();
        }
    }
}
