using System;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;
using DesktopBoxesUI.Shell.Interop;
using DesktopBoxesUI.Win32.NativeMethods;
using Windows.Win32.Foundation;

namespace DesktopBoxesUI.Views;

/// <summary>
/// Click-through, top-most debug overlay that visualizes the real Explorer desktop window hierarchy
/// (Progman / WorkerW -> SHELLDLL_DefView -> SysListView32) plus our own DesktopSurface, so it is
/// possible to see what window is actually on top at any point on the empty desktop. A "TOPMOST @ CURSOR"
/// rectangle marks the window <c>WindowFromPoint</c> returns under the mouse — that is the window which
/// would receive a desktop click. Updated live on a timer.
/// </summary>
public sealed partial class DesktopTreeDebugOverlay : Window
{
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromMilliseconds(250) };

    // DPI scale of this overlay, refreshed each tick so device-pixel window rects are converted to the
    // DIP coordinates WPF renders in (otherwise rectangles render too large on scaled displays).
    private DpiScale _dpi = new(1, 1);

    public DesktopTreeDebugOverlay()
    {
        InitializeComponent();

        // Cover the primary work area (no negative origin on multi-monitor setups) in DIPs.
        Left = SystemParameters.WorkArea.Left;
        Top = SystemParameters.WorkArea.Top;
        Width = SystemParameters.WorkArea.Width;
        Height = SystemParameters.WorkArea.Height;

        Loaded += (_, _) =>
        {
            var hwnd = new WindowInteropHelper(this).Handle;
            Win32Apis.MakeClickThrough(hwnd);
        };

        _timer.Tick += (_, _) => Refresh();
    }

    public void Start() => _timer.Start();
    public void Stop() => _timer.Stop();

    private void Refresh()
    {
        Host.Children.Clear();
        _dpi = VisualTreeHelper.GetDpi(this);

        IntPtr surface = GetSurfaceHandle();

        // The desktop shell trees.
        DrawTree(ShellWindowClasses.Progman, Brushes.Cyan);
        DrawTree(ShellWindowClasses.WorkerW, Brushes.Orange);

        // Our surface: draw it directly (it may be a child of Progman/WorkerW, so it is not always
        // reachable via EnumWindows).
        if (surface != IntPtr.Zero)
        {
            DrawWindow(surface, Brushes.Lime, "OUR SURFACE");
        }

        // The window actually on top under the cursor: the real hit target for a desktop click.
        IntPtr top = IntPtr.Zero;
        if (Win32Apis.GetCursorPos(out ManualApis.POINT pt))
        {
            top = ManualApis.WindowFromPoint(pt);
            DrawWindow(top, Brushes.Red, "TOPMOST @ CURSOR");
            DrawAncestry(top);
        }

        // Top-level z-order summary so the surface's position relative to Progman/WorkerW is visible.
        DrawZOrder(surface);

        // Surface state: parent, style flags, rect, and whether it is the hit target. This is the
        // definitive read on whether the glue actually stuck.
        DrawSurfaceInfo(surface, top);
    }

    private static IntPtr GetSurfaceHandle()
    {
        if (Application.Current != null)
        {
            foreach (var w in Application.Current.Windows)
            {
                if (w is DesktopSurface surface)
                {
                    return new WindowInteropHelper(surface).Handle;
                }
            }
        }

        // Fallback once the surface has been reparented (so it may no longer be in Windows).
        return Win32Apis.DesktopSurfaceHandle;
    }

    private void DrawSurfaceInfo(IntPtr surface, IntPtr topAtCursor)
    {
        if (surface == IntPtr.Zero)
        {
            AddText("SURFACE: not found (0)", Brushes.Lime, 12, 230);
            return;
        }

        var sb = new StringBuilder();
        sb.AppendLine($"SURFACE 0x{(long)surface:X}");
        sb.AppendLine($"  parent : 0x{(long)ManualApis.GetParent(surface):X} ({GetClassName(ManualApis.GetParent(surface))})");
        int style = ManualApis.GetWindowLong(surface, ManualApis.GWL_STYLE);
        int exStyle = ManualApis.GetWindowLong(surface, ManualApis.GWL_EXSTYLE);
        sb.AppendLine($"  style  : 0x{style:X}  {((style & ManualApis.WS_CHILD) != 0 ? "WS_CHILD " : "")}{((style & ManualApis.WS_POPUP) != 0 ? "WS_POPUP " : "")}{((style & ManualApis.WS_VISIBLE) != 0 ? "WS_VISIBLE " : "")}{((style & ManualApis.WS_DISABLED) != 0 ? "WS_DISABLED " : "")}");
        sb.AppendLine($"  exstyle: 0x{exStyle:X}  {((exStyle & ManualApis.WS_EX_LAYERED) != 0 ? "WS_EX_LAYERED " : "")}{((exStyle & ManualApis.WS_EX_TRANSPARENT) != 0 ? "WS_EX_TRANSPARENT " : "")}");
        if (Win32Apis.GetWindowRect((HWND)surface, out RECT r))
        {
            sb.AppendLine($"  rect   : L{r.left} T{r.top} R{r.right} B{r.bottom}  ({r.right - r.left}x{r.bottom - r.top})");
        }
        sb.AppendLine($"  is TOPMOST @ CURSOR: {((surface == topAtCursor) ? "YES" : "no")}");

        AddText(sb.ToString(), Brushes.Lime, 12, 230);
    }

    private void AddText(string text, Brush brush, double x, double y)
    {
        double max = Math.Max(200, ActualWidth - 24);
        var tb = new System.Windows.Controls.TextBlock
        {
            Text = text,
            Foreground = brush,
            FontSize = 11,
            Background = Brushes.Black,
            TextWrapping = System.Windows.TextWrapping.Wrap,
            MaxWidth = max,
        };
        Canvas.SetLeft(tb, x);
        Canvas.SetTop(tb, y);
        Host.Children.Add(tb);
    }

    private void DrawTree(string className, Brush brush)
    {
        IntPtr root = ManualApis.FindWindowEx(IntPtr.Zero, IntPtr.Zero, className, null);
        if (root == IntPtr.Zero)
        {
            return;
        }

        DrawNode(root, brush);
    }

    private void DrawNode(IntPtr hwnd, Brush brush)
    {
        DrawWindow(hwnd, brush, GetClassName(hwnd));
            ManualApis.EnumChildWindows(hwnd, (child, _) =>
        {
            DrawNode(child, brush);
            return true;
        }, IntPtr.Zero);
    }

    private void DrawWindow(IntPtr hwnd, Brush brush, string tag)
    {
        if (hwnd == IntPtr.Zero)
        {
            return;
        }

        if (!Win32Apis.GetWindowRect((HWND)hwnd, out RECT r))
        {
            return;
        }

        // GetWindowRect is in device pixels; convert to the DIP space WPF renders in using this
        // overlay's DPI scale, then to local coordinates.
        double x = r.left / _dpi.DpiScaleX;
        double y = r.top / _dpi.DpiScaleY;
        double w = (r.right - r.left) / _dpi.DpiScaleX;
        double h = (r.bottom - r.top) / _dpi.DpiScaleY;
        if (w <= 0 || h <= 0)
        {
            return;
        }

        var local = PointFromScreen(new Point(x, y));
        var rect = new Rectangle
        {
            Width = w,
            Height = h,
            Stroke = brush,
            StrokeThickness = 2,
            Fill = Brushes.Transparent,
        };
        Canvas.SetLeft(rect, local.X);
        Canvas.SetTop(rect, local.Y);
        Host.Children.Add(rect);

        var tb = new System.Windows.Controls.TextBlock
        {
            Text = $"{tag} 0x{(long)hwnd:X}",
            Foreground = brush,
            FontSize = 10,
            Background = Brushes.Black,
        };
        Canvas.SetLeft(tb, local.X);
        Canvas.SetTop(tb, local.Y);
        Host.Children.Add(tb);
    }

    private void DrawAncestry(IntPtr hwnd)
    {
        var sb = new StringBuilder();
        IntPtr cur = hwnd;
        for (int i = 0; i < 8 && cur != IntPtr.Zero; i++)
        {
            sb.Append(GetClassName(cur));
            if (i == 0)
            {
                sb.Append("  (topmost)");
            }

            sb.Append("  <  ");
            cur = ManualApis.GetParent(cur);
        }

        double max = Math.Max(200, ActualWidth - 24);
        var tb = new System.Windows.Controls.TextBlock
        {
            Text = "ANCESTRY: " + sb,
            Foreground = Brushes.Red,
            FontSize = 11,
            Background = Brushes.Black,
            TextWrapping = System.Windows.TextWrapping.Wrap,
            MaxWidth = max,
        };
        Canvas.SetLeft(tb, 12);
        Canvas.SetTop(tb, 12);
        Host.Children.Add(tb);
    }

    private void DrawZOrder(IntPtr surface)
    {
        var order = new System.Collections.Generic.List<string>();
        ManualApis.EnumWindows((hwnd, _) =>
        {
            string cls = GetClassName(hwnd);
            string marker = hwnd == surface ? "  <-- OUR SURFACE" : string.Empty;
            order.Add($"{cls} 0x{(long)hwnd:X}{marker}");
            return true;
        }, IntPtr.Zero);

        // Cap so the list stays on-screen.
        const int cap = 60;
        var shown = order.Count > cap ? order.GetRange(0, cap) : order;
        string text = $"TOP-LEVEL Z-ORDER (top->bottom, {order.Count} total):\n" + string.Join("\n", shown);

        double max = Math.Max(200, ActualWidth - 24);
        var tb = new System.Windows.Controls.TextBlock
        {
            Text = text,
            Foreground = Brushes.Cyan,
            FontSize = 11,
            Background = Brushes.Black,
            TextWrapping = System.Windows.TextWrapping.Wrap,
            MaxWidth = max,
        };
        Canvas.SetLeft(tb, 12);
        Canvas.SetTop(tb, 40);
        Host.Children.Add(tb);
    }

    private static string GetClassName(IntPtr hwnd)
    {
        var sb = new StringBuilder(256);
        return ManualApis.GetClassName(hwnd, sb, sb.Capacity) != 0 ? sb.ToString() : "?";
    }
}
