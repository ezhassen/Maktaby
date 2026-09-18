using DesktopBoxesUI.Core.Models;
using DesktopBoxesUI.Helpers;
using DesktopBoxesUI.Win32.NativeMethods;
using DesktopBoxesUI.Win32.Services;
using Microsoft.Extensions.DependencyInjection;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using WindowsNative;
using static WindowsNative.Win32Constants;

namespace DesktopBoxesUI.Views.Containers;

public partial class WidgetChromeOverlay : Window
{
    #region Fields

    private readonly IWidgetChromeOwner _ownerWidget;
    private HwndSource? _hwndSource;
    private HwndSourceHook? _layerHook;
    private WindowDragController? _drag;
    private readonly DesktopManager _desktopManager;
    private bool _isDragging;
    private bool _isSyncing;

    #endregion

    #region Public Props

    public bool IsDragging => _drag?.IsDragging ?? _isDragging;

    public bool IsResizing => WindowDragController.IsNativeSizing;

    /// <summary>The live HWND once the source is initialized, else <see cref="IntPtr.Zero"/>.</summary>
    internal IntPtr Handle
    {
        get
        {
            try { return new WindowInteropHelper(this).Handle; }
            catch { return IntPtr.Zero; }
        }
    }

    /// <summary>
    /// Re-inserts the overlay directly above its owner (no move/size/activate). Called after the
    /// owner is shown or reactivated: those operations insert the owner at the top and would
    /// otherwise bury the overlay behind the widget. No-op when already correctly ordered.
    /// </summary>
    public void EnsureAboveOwner()
    {
        try
        {
            var owner = new WindowInteropHelper(_ownerWidget.Window).Handle;
            var self = Handle;
            if (owner == IntPtr.Zero || self == IntPtr.Zero || !Win32Apis.IsWindow(owner) || !Win32Apis.IsWindow(self))
            {
                return;
            }

            // "Already above" walks down past topmost/invisible popups (IME, open menus) to the
            // nearest ordinary window: testing the immediate neighbour misfires while such a
            // window floats between overlay and owner.
            if (DesktopLayer.NextOrdinaryBelow(self) == owner)
            {
                return;
            }

            User32.SetWindowPos(self, owner, 0, 0, 0, 0,
                SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE);
        }
        catch { }
    }

    #endregion

    #region Init, Load, close

    public WidgetChromeOverlay(IWidgetChromeOwner owner)
    {
        InitializeComponent();
        _ownerWidget = owner;
        Owner = owner.Window;
        ShowInTaskbar = false;
        Topmost = false;
        _desktopManager = App.Services.GetRequiredService<DesktopManager>();
        // Match owner size/pos/min initially — overlay must mirror owner's resize constraints
        Left = owner.Window.Left;
        Top = owner.Window.Top;
        Width = owner.Window.Width;
        Height = owner.Window.Height;
        MinWidth = owner.Window.MinWidth;
        MinHeight = owner.Window.MinHeight;
        ResizeMode = owner.Window.ResizeMode;
        DataContext = owner.Window.DataContext;
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
            _hwndSource = HwndSource.FromHwnd(hwnd);
            // Owned by the widget window (Owner set in ctor), so it always floats above it;
            // never activates itself (forwards activation to the owner) and pins below the
            // owner instead of below the desktop band.
            _layerHook = DesktopLayer.Attach(hwnd, new DesktopLayer.Options
            {
                Kind = DesktopLayer.Kind.Overlay,
                DesktopManager = _desktopManager,
                MouseActivateResult = 3, // MA_NOACTIVATE
                OnMouseActivate = () => { try { _ownerWidget.Window.Activate(); } catch { } },
                KeepBelowSuppressed = () => IsDragging || IsResizing,
                KeepBelowAnchor = () => _ownerWidget.Window.GetCriticalHandle(),
            });
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
                    this, monitor, dpi, snapping, positioning,
                    r => { _isDragging = true; Left = r.X; Top = r.Y; Width = r.Width; Height = r.Height; SyncOwnerToThis(); _isDragging = false; },
                    others => { foreach (var c in _hostContainers.GetContainers()) if (c.Id != _ownerWidget.ContainerViewModel.Id && c.IsVisible) others.Add(c.Bounds); },
                    () => { try { App.Services.GetRequiredService<DesktopManager>().SaveAsyncFireAndForget(); } catch { } },
                    () => HeaderBorder.ActualHeight, getIsLocked: () => _ownerWidget.ContainerViewModel.IsLocked,
                    handleHitTest: false);
                drag.Attach();
                _drag = drag;
            }
            catch { }
        }
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        SyncFromOwner();
        _ownerWidget.Window.LocationChanged += OwnerPosChanged;
        _ownerWidget.Window.SizeChanged += OwnerPosChanged;
        _ownerWidget.Window.Closed += OwnerClosed;
        LocationChanged += OverlayPosChanged;
        SizeChanged += OverlayPosChanged;
        HeaderBorder.MouseEnter += (_, _) => _ownerWidget.SetHover(true);
    }

    private void OnClosed(object? sender, EventArgs e)
    {
        _ownerWidget.Window.LocationChanged -= OwnerPosChanged;
        _ownerWidget.Window.SizeChanged -= OwnerPosChanged;
        _ownerWidget.Window.Closed -= OwnerClosed;
        LocationChanged -= OverlayPosChanged;
        SizeChanged -= OverlayPosChanged;
        try { PreviewMouseLeftButtonDown -= Header_PreviewMouseDown; } catch { }
        try { PreviewMouseMove -= Header_MouseMove; } catch { }
        try { PreviewMouseLeftButtonUp -= Header_MouseUp; } catch { }
        try { _drag?.Detach(); } catch { }
        DesktopLayer.Detach(_hwndSource, _layerHook);
        _layerHook = null;
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
        if (IsDragging || IsResizing)
        {
            // User is actively moving/resizing the overlay: keep the owner following live.
            // Model commits happen only on explicit gesture completions, never here.
            SyncOwnerToThis(commitModel: false);
            return;
        }
        // Outside gestures the overlay is a pure MIRROR of the owner: it must never push
        // geometry back. An overlay move out here can only be framework/DPI noise, and pushing
        // it into the owner would corrupt the owner and then echo back forever. Re-sync FROM
        // the owner instead so any drift snaps back instead of amplifying.
        SyncFromOwner();
    }


    public void SyncFromOwner()
    {
        if (_isDragging || _isSyncing) return;
        _isSyncing = true;

        MinWidth = _ownerWidget.Window.MinWidth;
        MinHeight = _ownerWidget.Window.MinHeight;

        // Copy DIPs directly and let WPF map them to physical pixels with the overlay's current
        // DPI context. Converting through a separately queried monitor DPI here can use a stale
        // scale mid-transition and shift the chrome (for example left on 100% -> 125%).
        Left = _ownerWidget.Window.Left;
        Top = _ownerWidget.Window.Top;
        Width = _ownerWidget.Window.Width;
        Height = _ownerWidget.Window.Height;

        ResizeMode = _ownerWidget.Window.ResizeMode;
        UpdateLockButtonAppearance();
        _isSyncing = false;
    }

    public void SyncOwnerToThis(bool commitModel = true)
    {
        if (_isSyncing) return;
        _isSyncing = true;

        _ownerWidget.Window.Left = Left;
        _ownerWidget.Window.Top = Top;
        _ownerWidget.Window.Width = Width;
        _ownerWidget.Window.Height = Height;

        if (commitModel && !_desktopManager.IsLayoutUpdateActive)
        {
            _ownerWidget.ContainerViewModel.Model.Bounds = RectD.FromXYWH(Left, Top, Width, Height);
        }
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
        const int WM_ENTERSIZEMOVE = 0x0231;
        const int WM_EXITSIZEMOVE = 0x0232;
        const int WM_NCLBUTTONDOWN = 0x00A1;
        const int WM_NCLBUTTONUP = 0x00A2;
        const int WM_LBUTTONUP = 0x0202;
        const int WM_CAPTURECHANGED = 0x0215;
        const int HTTRANSPARENT = -1;
        const int HTLEFT = 10, HTRIGHT = 11, HTTOP = 12, HTTOPLEFT = 13, HTTOPRIGHT = 14, HTBOTTOM = 15, HTBOTTOMLEFT = 16, HTBOTTOMRIGHT = 17;

        // Click-activation (MA_NOACTIVATE + owner activate) and the z-order pin live in the
        // DesktopLayer hook now; this hook keeps hit-testing and drag/resize tracking only.
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
                bool canResize = _ownerWidget.Window.ResizeMode != ResizeMode.NoResize
                                 && _ownerWidget.Window.ResizeMode != ResizeMode.CanMinimize
                                 && !_ownerWidget.ContainerViewModel.IsLocked;
                // Mirror owner's Min* to overlay so WindowDragController.WmSizing clamps correctly
                if (canResize)
                {
                    // Keep overlay's Min* in sync for native WmSizing clamp (already synced in SyncToOwner, but ensure live)
                    try { MinWidth = _ownerWidget.Window.MinWidth; MinHeight = _ownerWidget.Window.MinHeight; } catch { }
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
        return IntPtr.Zero;
    }

    private void Header_MouseDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        try { _ownerWidget.Window.Activate(); } catch { }
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
