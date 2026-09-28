using System;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;
using Maktaby.Shell.Interop;
using Maktaby.Win32.NativeMethods;
using Maktaby.Native;
using static Maktaby.Native.Win32Constants;

namespace Maktaby.Views;

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

    // Left info column: selectable read-only boxes. Text is only rewritten when the content actually
    // changes — rebuilding every tick would destroy any in-progress selection.
    private System.Windows.Controls.TextBox _ancestryBox = null!;
    private System.Windows.Controls.TextBox _zOrderBox = null!;
    private System.Windows.Controls.TextBox _surfaceBox = null!;
    private System.Windows.Controls.StackPanel _infoPanel = null!;
    private string _lastAncestry = string.Empty;
    private string _lastZOrder = string.Empty;
    private string _lastSurface = string.Empty;

    public DesktopTreeDebugOverlay()
    {
        InitializeComponent();

        // Cover the primary work area (no negative origin on multi-monitor setups) in DIPs.
        // Sized from the live area (not a one-time SystemParameters snapshot) and re-synced
        // on every refresh tick so the overlay itself tracks display/scale changes.
        SyncToWorkArea();

        // NOTE: intentionally NOT click-through. Transparent areas still pass input through natively
        // (AllowsTransparency), while the info column is interactive so its text can be selected and
        // copied. Trade-off: desktop clicks under the column land on the overlay while it runs.

        var panel = new StackPanel { Margin = new Thickness(8) };
        _ancestryBox = MakeInfoBox(Brushes.Red);
        _zOrderBox = MakeInfoBox(Brushes.Cyan);
        _surfaceBox = MakeInfoBox(Brushes.Lime);
        panel.Children.Add(_ancestryBox);
        panel.Children.Add(new ScrollViewer
        {
            Content = _zOrderBox,
            MaxHeight = 300,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
        });
        panel.Children.Add(_surfaceBox);
        Canvas.SetLeft(panel, 8);
        Canvas.SetTop(panel, 8);
        _infoPanel = panel;
        Host.Children.Add(panel);

        _timer.Tick += (_, _) => Refresh();
    }

    /// <summary>Creates a read-only, copyable, monospace info box.</summary>
    private static System.Windows.Controls.TextBox MakeInfoBox(Brush foreground)
    {
        return new System.Windows.Controls.TextBox
        {
            IsReadOnly = true,
            BorderThickness = new Thickness(0),
            Background = new SolidColorBrush(Color.FromArgb(0xCC, 0, 0, 0)),
            Foreground = foreground,
            FontSize = 11,
            FontFamily = new FontFamily("Consolas"),
            TextWrapping = TextWrapping.Wrap,
            MaxWidth = 420,
            Padding = new Thickness(4),
            IsTabStop = false,
        };
    }

    /// <summary>Updates an info box only when the text changed, preserving user selections.</summary>
    private void SetInfo(System.Windows.Controls.TextBox box, ref string cache, string text)
    {
        if (cache == text)
        {
            return;
        }

        cache = text;
        box.Text = text;
    }

    public void Start() => _timer.Start();
    public void Stop() => _timer.Stop();

    /// <summary>Keeps the overlay covering the live primary work area across display/scale
    /// changes. Only assigns when actually moved/resized to avoid needless layout churn.</summary>
    private void SyncToWorkArea()
    {
        var wa = DesktopManager.GetPrimaryWorkAreaDip();
        if (wa.Width <= 0 || wa.Height <= 0)
        {
            return;
        }

        const double tolerance = 0.5;
        if (Math.Abs(Left - wa.X) > tolerance) Left = wa.X;
        if (Math.Abs(Top - wa.Y) > tolerance) Top = wa.Y;
        if (Math.Abs(Width - wa.Width) > tolerance) Width = wa.Width;
        if (Math.Abs(Height - wa.Height) > tolerance) Height = wa.Height;
    }

    private void Refresh()
    {
        SyncToWorkArea();
        Host.Children.Clear();
        Host.Children.Add(_infoPanel);
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
        string ancestryText = "ANCESTRY: (no cursor point)";
        if (Win32Apis.GetCursorPos(out POINT pt))
        {
            top = User32.WindowFromPoint(pt);
            DrawWindow(top, Brushes.Red, "TOPMOST @ CURSOR");
            ancestryText = BuildAncestry(top);
            ancestryText += "\n" + BuildTopmostRect(top);
        }

        SetInfo(_ancestryBox, ref _lastAncestry, ancestryText);
        SetInfo(_zOrderBox, ref _lastZOrder, BuildZOrder(surface));
        SetInfo(_surfaceBox, ref _lastSurface, BuildSurfaceInfo(surface, top));
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

    private string BuildSurfaceInfo(IntPtr surface, IntPtr topAtCursor)
    {
        if (surface == IntPtr.Zero)
        {
            return "SURFACE: not found (0)";
        }

        var sb = new StringBuilder();
        sb.AppendLine($"SURFACE 0x{(long)surface:X}");
        sb.AppendLine($"  parent : 0x{(long)User32.GetParent(surface):X} ({GetClassName(User32.GetParent(surface))})");
        int style = User32.GetWindowLong(surface, GWL_STYLE);
        int exStyle = User32.GetWindowLong(surface, GWL_EXSTYLE);
        sb.AppendLine($"  style  : 0x{style:X}  {((style & WS_CHILD) != 0 ? "WS_CHILD " : "")}{((style & WS_POPUP) != 0 ? "WS_POPUP " : "")}{((style & WS_VISIBLE) != 0 ? "WS_VISIBLE " : "")}{((style & WS_DISABLED) != 0 ? "WS_DISABLED " : "")}");
        sb.AppendLine($"  exstyle: 0x{exStyle:X}  {((exStyle & WS_EX_LAYERED) != 0 ? "WS_EX_LAYERED " : "")}{((exStyle & WS_EX_TRANSPARENT) != 0 ? "WS_EX_TRANSPARENT " : "")}");
        if (Win32Apis.GetWindowRect(surface, out RECT r))
        {
            sb.AppendLine($"  rect   : L{r.Left} T{r.Top} R{r.Right} B{r.Bottom}  ({r.Right - r.Left}x{r.Bottom - r.Top})");
        }
        sb.AppendLine($"  is TOPMOST @ CURSOR: {((surface == topAtCursor) ? "YES" : "no")}");

        return sb.ToString();
    }

    private void DrawTree(string className, Brush brush)
    {
        IntPtr root = User32.FindWindowEx(IntPtr.Zero, IntPtr.Zero, className, null);
        if (root == IntPtr.Zero)
        {
            return;
        }

        DrawNode(root, brush);
    }

    private void DrawNode(IntPtr hwnd, Brush brush)
    {
        DrawWindow(hwnd, brush, GetClassName(hwnd));
            User32.EnumChildWindows(hwnd, (child, _) =>
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

        if (!Win32Apis.GetWindowRect(hwnd, out RECT r))
        {
            return;
        }

        // GetWindowRect is in DEVICE pixels. PointFromScreen also expects device pixels and converts
        // them to this window's local DIP space (including the overlay's own origin) ? so pass the
        // raw rect corner. Only the width/height need the manual DPI division.
        var local = PointFromScreen(new Point(r.Left, r.Top));
        double w = (r.Right - r.Left) / _dpi.DpiScaleX;
        double h = (r.Bottom - r.Top) / _dpi.DpiScaleY;
        if (w <= 0 || h <= 0)
        {
            return;
        }

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

    /// <summary>
    /// Numeric bounds of the highlighted (topmost-at-cursor) window. Uses the live native rect
    /// (device pixels) converted to DIPs — the ground truth when WPF's cached
    /// Left/Top/Width/Height disagree with where the window actually is.
    /// </summary>
    private string BuildTopmostRect(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero || !Win32Apis.GetWindowRect(hwnd, out RECT r))
        {
            return "TOPMOST RECT: (unavailable)";
        }

        var dipPos = PointFromScreen(new Point(r.Left, r.Top));
        double w = (r.Right - r.Left) / _dpi.DpiScaleX;
        double h = (r.Bottom - r.Top) / _dpi.DpiScaleY;
        return $"TOPMOST RECT (dip): ({dipPos.X:0},{dipPos.Y:0} {w:0}x{h:0})  [px: L{r.Left} T{r.Top} R{r.Right} B{r.Bottom}]";
    }

    private string BuildAncestry(IntPtr hwnd)
    {
        var sb = new StringBuilder("ANCESTRY: ");
IntPtr cur = hwnd;
        for (int i = 0; i < 8 && cur != IntPtr.Zero; i++)
        {
            sb.Append(GetClassName(cur));
            if (i == 0)
            {
                sb.Append("  (topmost)");
            }

            sb.Append("  <  ");
            cur = User32.GetParent(cur);
        }

        return sb.ToString();
    }

    private string BuildZOrder(IntPtr surface)
    {
        var order = new System.Collections.Generic.List<string>();
        User32.EnumWindows((hwnd, _) =>
        {
            string cls = GetClassName(hwnd);
            string marker = hwnd == surface ? "  <-- OUR SURFACE" : string.Empty;
            order.Add($"{cls} 0x{(long)hwnd:X}{marker}");
            return true;
        }, IntPtr.Zero);

        // Cap so the box stays compact (it scrolls).
        const int cap = 25;
        var shown = order.Count > cap ? order.GetRange(0, cap) : order;
        return $"TOP-LEVEL Z-ORDER (top->bottom, {order.Count} total):\n" + string.Join("\n", shown);
    }

    private static string GetClassName(IntPtr hwnd)
    {
        var sb = new StringBuilder(256);
        return User32.GetClassName(hwnd, sb, sb.Capacity) != 0 ? sb.ToString() : "?";
    }
}
