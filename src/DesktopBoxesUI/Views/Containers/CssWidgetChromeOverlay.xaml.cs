using DesktopBoxesUI.Core.Models;
using DesktopBoxesUI.Helpers;
using DesktopBoxesUI.Win32.NativeMethods;
using Microsoft.Extensions.DependencyInjection;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;

namespace DesktopBoxesUI.Views.Containers;

public partial class CssWidgetChromeOverlay : Window
{
    #region Fields

    private readonly CssWidgetWindow _ownerWidget;
    private HwndSource? _hwndSource;
    private WindowDragController? _drag;
    private readonly DesktopManager _desktopManager;
    private bool _isDragging;
    private bool _isSyncing;

    #endregion

    #region Public Props

    public bool IsDragging => _drag?.IsDragging ?? _isDragging;

    public bool IsResizing => WindowDragController.IsNativeSizing;

    #endregion

    #region Init, Load, close

    public CssWidgetChromeOverlay(CssWidgetWindow owner)
    {
        InitializeComponent();
        _ownerWidget = owner;
        Owner = owner;
        ShowInTaskbar = false;
        Topmost = false;
        _desktopManager = App.Services.GetRequiredService<DesktopManager>();
        // Match owner size/pos/min initially — overlay must mirror owner's resize constraints
        Left = owner.Left;
        Top = owner.Top;
        Width = owner.Width;
        Height = owner.Height;
        MinWidth = owner.MinWidth;
        MinHeight = owner.MinHeight;
        ResizeMode = owner.ResizeMode;
        DataContext = owner.DataContext;
        SourceInitialized += OnSourceInitialized;
        Loaded += OnLoaded;
        Closed += OnClosed;
        // Forward menu click to owner
        MenuButton.Click += (_, _) => _ownerWidget.ShowWidgetMenu();
        // Preview drag handlers must exist before first Show (Loaded is too late — first header click would miss BeginTitleDrag)
        PreviewMouseLeftButtonDown += Header_PreviewMouseDown;
        PreviewMouseMove += Header_MouseMove;
        PreviewMouseLeftButtonUp += Header_MouseUp;
        HeaderBorder.MouseRightButtonDown += (ss, ee) => { if (!IsDragging && !IsResizing) _ownerWidget.ShowWidgetMenu(); };
        MouseEnter += (_, _) => _ownerWidget.SetHover(true);
        MouseLeave += (_, _) => _ownerWidget.SetHover(false);
    }

    private void OnSourceInitialized(object? sender, EventArgs e)
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd != IntPtr.Zero)
        {
            Win32Apis.MakeToolWindow(hwnd);
            // Must be non-activating: Show() must not steal activation from owner.
            // ShowActivated="False" in XAML already requests WS_EX_NOACTIVATE, but enforce it
            // explicitly for the wpftmp AnyCPU build where style changes can be reapplied.
            try
            {
                int ex = ManualApis.GetWindowLong(hwnd, ManualApis.GWL_EXSTYLE);
                ManualApis.SetWindowLong(hwnd, ManualApis.GWL_EXSTYLE, ex | ManualApis.WS_EX_NOACTIVATE);
                ManualApis.SetWindowPos(hwnd, IntPtr.Zero, 0, 0, 0, 0,
                    ManualApis.SWP_NOMOVE | ManualApis.SWP_NOSIZE | ManualApis.SWP_NOZORDER | ManualApis.SWP_FRAMECHANGED | ManualApis.SWP_NOACTIVATE);
            }
            catch { }
            _hwndSource = HwndSource.FromHwnd(hwnd);
            _hwndSource?.AddHook(HwndHook);
        }
        // Drag controller for overlay chrome (title/resize) — updates both windows
        // Created on SourceInitialized so first Show already has _drag ready (Loaded is too late for first header click)
        if (_drag == null)
        {
            try
            {
                var monitor = App.Services.GetRequiredService<Core.Interfaces.IMonitorService>();
                var dpi = App.Services.GetRequiredService<Core.Interfaces.IDpiService>();
                var snapping = App.Services.GetRequiredService<Core.Interfaces.IWindowSnappingService>();
                var positioning = App.Services.GetRequiredService<Core.Interfaces.IWindowPositioningService>();
                var _hostContainers = App.Services.GetRequiredService<Core.Interfaces.IContainerService>();
                var drag = new WindowDragController(
                    this, App.Services.GetRequiredService<DesktopManager>(), monitor, dpi, snapping, positioning,
                    r => { _isDragging = true; Left = r.X; Top = r.Y; Width = r.Width; Height = r.Height; SyncOwnerToThis(); _isDragging = false; },
                    () => _hostContainers.GetContainers()
                            .Where(c => c.Id != _ownerWidget.ContainerViewModel.Id && c.IsVisible)
                            .Select(c => c.Bounds).ToList(),
                    () => { try { App.Services.GetRequiredService<DesktopManager>().SaveAsyncFireAndForget(); } catch { } },
                    () => HeaderBorder.ActualHeight, getIsLocked: () => _ownerWidget.ContainerViewModel.IsLocked,
                    handleHitTest: false, handleMouseActivate: false, handleKeepBelow: false);
                drag.Attach(glueToDesktop: false);
                _drag = drag;
            }
            catch { }
        }
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        SyncFromOwner();
        _ownerWidget.LocationChanged += OwnerPosChanged;
        _ownerWidget.SizeChanged += OwnerPosChanged;
        _ownerWidget.Closed += OwnerClosed;
        LocationChanged += OverlayPosChanged;
        SizeChanged += OverlayPosChanged;
        HeaderBorder.MouseEnter += (_, _) => _ownerWidget.SetHover(true);
    }

    private void OnClosed(object? sender, EventArgs e)
    {
        _ownerWidget.LocationChanged -= OwnerPosChanged;
        _ownerWidget.SizeChanged -= OwnerPosChanged;
        _ownerWidget.Closed -= OwnerClosed;
        LocationChanged -= OverlayPosChanged;
        SizeChanged -= OverlayPosChanged;
        try { PreviewMouseLeftButtonDown -= Header_PreviewMouseDown; } catch { }
        try { PreviewMouseMove -= Header_MouseMove; } catch { }
        try { PreviewMouseLeftButtonUp -= Header_MouseUp; } catch { }
        try { _drag?.Detach(); } catch { }
        if (_hwndSource != null)
        {
            _hwndSource.RemoveHook(HwndHook);
            _hwndSource = null;
        }
    }

    private void OwnerClosed(object? sender, EventArgs e)
    {
        try { Close(); } catch { }
    }

    #endregion

    #region Owner Sync Props

    private void OwnerPosChanged(object? sender, EventArgs e)
    {
        if (_isDragging || _isSyncing) return;
        SyncFromOwner();
    }

    private void OverlayPosChanged(object? sender, EventArgs e)
    {
        if (_isSyncing) return;
        // Live sync owner during native drag — don't block on _isDragging or owner lags behind overlay.
        SyncOwnerToThis();
    }

    public void SyncFromOwner()
    {
        if (_isDragging || _isSyncing) return;
        _isSyncing = true;
        Left = _ownerWidget.Left;
        Top = _ownerWidget.Top;
        Width = _ownerWidget.Width;
        Height = _ownerWidget.Height;
        MinWidth = _ownerWidget.MinWidth;
        MinHeight = _ownerWidget.MinHeight;
        ResizeMode = _ownerWidget.ResizeMode;
        UpdateLockButtonAppearance();
        _isSyncing = false;
    }

    public void SyncOwnerToThis()
    {
        if (_isSyncing) return;
        _isSyncing = true;
        _ownerWidget.Left = Left;
        _ownerWidget.Top = Top;
        _ownerWidget.Width = Width;
        _ownerWidget.Height = Height;
        _ownerWidget.ContainerViewModel.Model.Bounds = RectD.FromXYWH(Left, Top, Width, Height);
        _isSyncing = false;
    }

    void UpdateLockButtonAppearance()
    {
        if (_ownerWidget.ContainerViewModel.IsLocked)
        {
            //ToggleLocked.Appearance = Wpf.Ui.Controls.ControlAppearance.Info;
            ToggleLocked.Icon = (Wpf.Ui.Controls.SymbolIcon)Application.Current.FindResource("ItemIcon_Locked");
        }
        else
        {
            //ToggleLocked.Appearance = Wpf.Ui.Controls.ControlAppearance.Transparent;
            ToggleLocked.Icon = (Wpf.Ui.Controls.SymbolIcon)Application.Current.FindResource("ItemIcon_UnLocked");
        }
    }

    #endregion

    private IntPtr HwndHook(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        const int WM_NCHITTEST = 0x0084;
        const int WM_MOUSEACTIVATE = 0x0021;
        const int WM_WINDOWPOSCHANGING = 0x0046;
        const int WM_ENTERSIZEMOVE = 0x0231;
        const int WM_EXITSIZEMOVE = 0x0232;
        const int WM_NCLBUTTONDOWN = 0x00A1;
        const int WM_NCLBUTTONUP = 0x00A2;
        const int WM_LBUTTONUP = 0x0202;
        const int WM_CAPTURECHANGED = 0x0215;
        const int MA_NOACTIVATE = 3;
        const int HTTRANSPARENT = -1;
        const int HTLEFT = 10, HTRIGHT = 11, HTTOP = 12, HTTOPLEFT = 13, HTTOPRIGHT = 14, HTBOTTOM = 15, HTBOTTOMLEFT = 16, HTBOTTOMRIGHT = 17;

        if (msg == WM_MOUSEACTIVATE)
        {
            handled = true;
            try { _ownerWidget.Activate(); } catch { }
            return (IntPtr)MA_NOACTIVATE;
        }
        if (msg == WM_NCLBUTTONDOWN) { _isDragging = true; return IntPtr.Zero; }
        if (msg == WM_NCLBUTTONUP || msg == WM_LBUTTONUP || msg == WM_CAPTURECHANGED) { if (_isDragging) { _isDragging = false; } return IntPtr.Zero; }
        if (msg == WM_ENTERSIZEMOVE) { _isDragging = true; return IntPtr.Zero; }
        if (msg == WM_EXITSIZEMOVE) { _isDragging = false; SyncOwnerToThis(); try { App.Services.GetRequiredService<DesktopManager>().SaveAsyncFireAndForget(); } catch { } return IntPtr.Zero; }
        if (msg == WM_NCHITTEST)
        {
            try
            {
                // Use lParam (physical screen coords) + DPI-aware bounds like WindowDragController.
                // Owner's old code mixed DIPs (Left/Top) with physical GetCursorPos, breaking at >100% DPI.
                int x = (short)(lParam.ToInt64() & 0xFFFF);
                int y = (short)((lParam.ToInt64() >> 16) & 0xFFFF);
                RectD b;
                try
                {
                    var positioning = App.Services.GetRequiredService<Core.Interfaces.IWindowPositioningService>();
                    b = positioning.GetBounds(hwnd);
                }
                catch
                {
                    // Fallback to WPF DIPs converted via DPI
                    double fallbackScale = 1.0;
                    try { fallbackScale = App.Services.GetRequiredService<Core.Interfaces.IDpiService>().GetDpiForWindow(hwnd) / 96.0; } catch { }
                    b = RectD.FromXYWH(Left * fallbackScale, Top * fallbackScale, Width * fallbackScale, Height * fallbackScale);
                }
                double scale = 1.0;
                try { scale = App.Services.GetRequiredService<Core.Interfaces.IDpiService>().GetDpiForWindow(hwnd) / 96.0; } catch { }
                int edge = (int)Math.Max(4, 8 * scale);
                double headerH = 28 * scale;
                try { headerH = HeaderBorder.ActualHeight * scale; if (headerH < 1) headerH = 28 * scale; } catch { }
                bool canResize = _ownerWidget.ResizeMode != ResizeMode.NoResize
                                 && _ownerWidget.ResizeMode != ResizeMode.CanMinimize
                                 && !_ownerWidget.ContainerViewModel.IsLocked;
                // Mirror owner's Min* to overlay so WindowDragController.WmSizing clamps correctly
                if (canResize)
                {
                    // Keep overlay's Min* in sync for native WmSizing clamp (already synced in SyncToOwner, but ensure live)
                    try { MinWidth = _ownerWidget.MinWidth; MinHeight = _ownerWidget.MinHeight; } catch { }
                }
                bool left = false, right = false, top = false, bottom = false;
                if (canResize)
                {
                    left = x <= b.X + edge;
                    right = x >= b.Right - edge;
                    top = y <= b.Y + edge;
                    bottom = y >= b.Bottom - edge;
                    if (left && top) { handled = true; return (IntPtr)HTTOPLEFT; }
                    if (right && top) { handled = true; return (IntPtr)HTTOPRIGHT; }
                    if (left && bottom) { handled = true; return (IntPtr)HTBOTTOMLEFT; }
                    if (right && bottom) { handled = true; return (IntPtr)HTBOTTOMRIGHT; }
                    if (left) { handled = true; return (IntPtr)HTLEFT; }
                    if (right) { handled = true; return (IntPtr)HTRIGHT; }
                    if (top) { handled = true; return (IntPtr)HTTOP; }
                    if (bottom) { handled = true; return (IntPtr)HTBOTTOM; }
                }

                // Title bar area — physical header strip across top (below the top edge resize zone)
                // For custom capture drag (BeginTitleDrag) we must return HTCLIENT, not HTCAPTION,
                // otherwise WM_NCLBUTTONDOWN is sent and WPF MouseLeftButtonDown never fires.
                // Native caption drag is intentionally disabled for the header; edges still return HT*.
                bool inHeader = y < b.Y + headerH && y >= b.Y + edge;
                if (inHeader || (y < b.Y + headerH && !(left || right || top || bottom)))
                {
                    handled = true;
                    return (IntPtr)1; // HTCLIENT — lets HeaderBorder.MouseLeftButtonDown -> BeginTitleDrag run
                }
                // Central transparent area — let click fall through to WebView below
                handled = true;
                return (IntPtr)HTTRANSPARENT;
            }
            catch { }
        }
        if (msg == WM_WINDOWPOSCHANGING)
        {
            WindowDragController.SuppressShellSnap(lParam);
            if (!IsDragging && !IsResizing)
            {
                //    WindowDragController.KeepBelowApps(hwnd, lParam, _desktopManager);
                WindowDragController.KeepBelowApps(hwnd, lParam, _desktopManager, parentWindowH: _ownerWidget.GetCriticalHandle());
            }
            // KeepBelowApps intentionally NOT called for overlay — it's owned by CssWidgetWindow (Owner set in ctor)
            // Owned windows are always above their owner; owner is kept below via CssWidgetWindow.KeepBelowApps.
            // Calling KeepBelowApps for owned WS_EX_NOACTIVATE overlay would set HwndInsertAfter to topDesktop (below owner)
            // and make chrome appear behind the widget, plus block TitleDrag.
        }
        return IntPtr.Zero;
    }

    private void Header_MouseDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        try { _ownerWidget.Activate(); } catch { }
        if (_ownerWidget.ContainerViewModel.IsLocked) return;
        _isDragging = true;
        _drag?.BeginTitleDrag(e);
        e.Handled = true;
    }

    private void Header_PreviewMouseDown(object sender, MouseButtonEventArgs e)
    {
        // Preview tunneling: HwndHook WM_NCHITTEST already filtered to header (HTCLIENT) vs center (HTTRANSPARENT),
        // so any Preview that reaches this window is on header. Only exclude MenuButton.
        if (_ownerWidget.ContainerViewModel.IsLocked) return;
        var src = e.OriginalSource as DependencyObject;
        if (src != null && IsDescendant(src, MenuButton)) return;
        if (src != null && IsDescendant(src, ToggleLocked)) return;
        Header_MouseDown(sender, e);
    }

    private static bool IsDescendant(DependencyObject child, DependencyObject parent)
    {
        while (child != null)
        {
            if (child == parent) return true;
            child = System.Windows.Media.VisualTreeHelper.GetParent(child);
        }
        return false;
    }

    private void Header_MouseMove(object sender, System.Windows.Input.MouseEventArgs e)
    {
        if (_drag == null) return;
        _drag.TitleDrag(e);
        if (_drag.IsDragging)
        {
            // TitleDrag moves overlay window directly; keep owner in sync live
            SyncOwnerToThis();
        }
    }

    private void Header_MouseUp(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        bool wasDragging = _drag?.IsDragging == true;
        _isDragging = false;
        _drag?.EndTitleDrag(e);
        if (wasDragging)
        {
            SyncOwnerToThis();
        }
    }

    private void MenuButton_Click(object sender, RoutedEventArgs e)
    {
        _ownerWidget.ShowWidgetMenu();
    }

    public void UpdateTitle(string title)
    {
        TitleText.Text = title;
    }

    private void ToggleLocked_Click(object sender, RoutedEventArgs e)
    {
        _ownerWidget.ContainerViewModel.IsLocked = !_ownerWidget.ContainerViewModel.IsLocked;
        if (_ownerWidget.LockMenuItem is not null) _ownerWidget.LockMenuItem.IsChecked = _ownerWidget.ContainerViewModel.IsLocked;
        UpdateLockButtonAppearance();
    }
}
