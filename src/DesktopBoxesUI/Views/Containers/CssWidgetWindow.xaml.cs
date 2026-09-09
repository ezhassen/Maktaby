using DesktopBoxesUI.Controls.ContainersControls;
using DesktopBoxesUI.Core.Interfaces;
using DesktopBoxesUI.Core.Models;
using DesktopBoxesUI.Helpers;
using DesktopBoxesUI.ViewModels;
using DesktopBoxesUI.Win32.NativeMethods;
using WindowsNative;
using static WindowsNative.Win32Constants;
using Microsoft.Extensions.DependencyInjection;
using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;

namespace DesktopBoxesUI.Views.Containers;

public partial class CssWidgetWindow : WidgetWindow
{
    public override ContainerViewModel ContainerViewModel { get; }
    private readonly DesktopItemContainer _container;
    private readonly ICssWidgetService _widgetService;
    private CssWidgetControl? _widgetControl;
    //private WindowDragController? _drag;
    private bool _isHover;
    private bool _isActive;
    private HwndSource? _hwndSource;
    private CssWidgetChromeOverlay? _chromeOverlay;
    private readonly DesktopManager _desktopManager;
    private bool ShowChromeOnHover = false;
    //private bool MoveWindowByWidgetMouseDown = false;

    public CssWidgetWindow(ContainerViewModel vm)
    {
        InitializeComponent();
        //DataContext = vm;
        ContainerViewModel = vm;
        _container = vm.Model;
        _widgetService = App.Services.GetRequiredService<ICssWidgetService>();
        _desktopManager = App.Services.GetRequiredService<DesktopManager>();
        var manifest = LoadWidget();
        ApplyManifest(manifest);

        Left = _container.Bounds.X;
        Top = _container.Bounds.Y;
        Width = _container.Bounds.Width > 0 ? _container.Bounds.Width : (manifest?.Width ?? 300);
        Height = _container.Bounds.Height > 0 ? _container.Bounds.Height : (manifest?.Height ?? 220);

        Title = manifest?.Name ?? _container.CssWidgetName ?? "Widget";

        //UpdateChrome();
        Loaded += OnLoaded;
        SizeChanged += OnSizeChanged;
        LocationChanged += OnLocationChanged;
        IsVisibleChanged += OnIsVisibleChanged;
        StateChanged += OnStateChanged;
        this.LockMenuItem.IsChecked = vm.IsLocked;
        // Hover/focus over the WebView HWND does not raise WPF hover reliably; use the root border
        // as the single hover source for the whole widget. This avoids the header/host transition churn
        // that caused the chrome to flicker while the pointer crossed between the title bar and the WebView.
        //TitleArea.MouseLeftButtonDown += TitleArea_MouseDown;
        //TitleArea.MouseMove += TitleArea_MouseMove;
        //TitleArea.MouseLeftButtonUp += TitleArea_MouseUp;
        //PreviewMouseLeftButtonDown += TitleArea_MouseDown;
        //MouseMove += TitleArea_MouseMove;
        //MouseUp += TitleArea_MouseUp;

        //RootBorder.MouseEnter += (_, _) => { _isHover = true; UpdateChrome(); };
        //RootBorder.MouseLeave += (_, _) => { _isHover = false; UpdateChrome(); };

        //WidgetMenu.Closed += (_, _) => UpdateChrome();
        //WidgetMenu.Opened += (_, _) => UpdateChrome();
        Activated += (_, _) => { _isActive = true; UpdateChrome(); };
        Deactivated += (_, _) => { _isActive = false; _isHover = false; UpdateChrome(); };
    }

    private CssWidgetManifest? LoadWidget()
    {
        var slug = _container.CssWidgetName;
        var source = _container.CssWidgetSource ?? CssWidgetSource.User;
        if (string.IsNullOrWhiteSpace(slug)) return null;
        var info = _widgetService.TryGetWidget(slug, source);
        if (info is null)
        {
            var alt = source == CssWidgetSource.App ? CssWidgetSource.User : CssWidgetSource.App;
            info = _widgetService.TryGetWidget(slug, alt);
        }
        if (info is null)
        {
            WidgetHost.Content = new TextBlock
            {
                Text = $"Widget '{slug}' not found",
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                Foreground = System.Windows.Media.Brushes.IndianRed
            };
            return null;
        }
        // Unsubscribe previous control if any (e.g. Change Widget)
        if (_widgetControl != null)
        {
            _widgetControl.WidgetMouseEnter -= OnWidgetMouseEnter;
            _widgetControl.WidgetMouseLeave -= OnWidgetMouseLeave;
            _widgetControl.WidgetClicked -= OnWidgetClicked;
            //_widgetControl.WidgetMouseDown -= OnWidgetMouseDown;
        }
        _widgetControl = new CssWidgetControl();
        _widgetControl.WidgetMouseEnter += OnWidgetMouseEnter;
        _widgetControl.WidgetMouseLeave += OnWidgetMouseLeave;
        _widgetControl.WidgetClicked += OnWidgetClicked;
        //_widgetControl.WidgetMouseDown += OnWidgetMouseDown;
        WidgetHost.Content = _widgetControl;
        _widgetControl.LoadWidget(info.Slug, info.Source);
        return info.Manifest;
    }

    private void ApplyManifest(CssWidgetManifest? manifest)
    {
        bool resizable = manifest?.IsResizable ?? true;
        if (_container.IsLocked) resizable = false;
        ResizeMode = resizable ? ResizeMode.CanResize : ResizeMode.NoResize;
        if (_chromeOverlay is not null)
        {
            _chromeOverlay.ResizeMode = ResizeMode;
        }
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        Attach();
    }
    bool _attached;
    private void Attach()
    {
        if (_attached) return;
        //try
        //{
        var hwnd = new WindowInteropHelper(this).Handle;
        _hwndSource = HwndSource.FromHwnd(hwnd);
        Win32Apis.MakeToolWindow(hwnd);
        Win32Apis.RegisterBoxWindow(hwnd);
        // Own the box to the DesktopSurface when the custom surface is live (handle published);
        // owned windows always float above their owner, so a box can never sink below (or lose
        // clicks/activation to) the surface. Falls back to Progman when no surface exists.
        Win32Apis.GlueToDesktop(hwnd, Win32Apis.DesktopSurfaceHandle);
        Win32Apis.PreventMinimize(hwnd);
        _hwndSource.AddHook(HwndHook);
        _hwndSource.AddHook(Win32Apis.MinimizePreventionHook);
        //}
        //catch
        //{
        //    // Positioning can fail if the handle isn't ready yet; the window still shows.
        //}
        _attached = true;
    }

    private void Detach()
    {
        _attached = false;
        if (_hwndSource != null)
        {
            _hwndSource.RemoveHook(HwndHook);
            try { _hwndSource.RemoveHook(Win32Apis.MinimizePreventionHook); } catch { }
            try { _hwndSource.RemoveHook(HwndHook); } catch { }
            _hwndSource = null;
        }
    }


    public void ShowWidgetMenu()
    {
        //WidgetMenu.PlacementTarget = MenuButton;
        WidgetMenu.Placement = System.Windows.Controls.Primitives.PlacementMode.Mouse;
        WidgetMenu.IsOpen = true;
    }

    private IntPtr HwndHook(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        const int WM_MOUSEACTIVATE = 0x0021;
        const int WM_ACTIVATE = 0x0006;
        const int WM_WINDOWPOSCHANGING = 0x0046;
        const int MA_ACTIVATE = 1;
        if (msg == WM_MOUSEACTIVATE)
        {
            // Allow click on WebView2 HWND to activate the WidgetWindow (otherwise focus stays on previous window)
            handled = true;
            return (IntPtr)MA_ACTIVATE;
        }
        if (msg == WM_ACTIVATE)
        {
            int low = wParam.ToInt32() & 0xFFFF;
            _isActive = low != 0; // WA_INACTIVE = 0
            UpdateChrome();
        }
        if (msg == WM_WINDOWPOSCHANGING)
        {
            WindowDragController.SuppressShellSnap(lParam);
            // Don't enforce KeepBelowApps while overlay is dragging — TitleDrag moves overlay+owner via Left/Top
            // with SWP_NOZORDER; KeepBelowApps would see pure z-order change and incorrectly reparent, blocking move.
            // BoxContainerWindow's KeepBelowApps also skips during move (!noMove), same guard here.
            if (_chromeOverlay == null || (!_chromeOverlay.IsDragging && !_chromeOverlay.IsResizing))
                WindowDragController.KeepBelowApps(hwnd, lParam, _desktopManager);
        }
        return IntPtr.Zero;
    }

    private void OnWidgetMouseEnter(object? sender, EventArgs e)
    {
        if (!HeaderIsShown() && !_isHover)
        {
            //Debug.WriteLine("[CssWidgetWindow] OnWidgetMouseEnter to show");
            _isHover = true;
            UpdateChrome();
        }
    }
    private void OnWidgetMouseLeave(object? sender, EventArgs e)
    {
        //Debug.WriteLine("[CssWidgetWindow] OnWidgetMouseLeave");
        try
        {
            // If the native cursor is still inside this window's bounds, ignore the DOM 'leave' messages
            // (they can fire when moving between elements inside the WebView). Use physical coordinates
            // which match GetWindowRect and GetCursorPos.
            Win32Apis.GetCursorPos(out var pt);
            var hwnd = new WindowInteropHelper(this).Handle;
            if (hwnd != IntPtr.Zero && Win32Apis.GetWindowRect(hwnd, out var rect))
            {
                if (pt.X >= rect.Left && pt.X <= rect.Right && pt.Y >= rect.Top && pt.Y <= rect.Bottom)
                {
                    //Debug.WriteLine("[CssWidgetWindow] OnWidgetMouseLeave ignored — cursor still inside window");
                    return;
                }
            }
        }
        catch { }

        _isHover = false;
        UpdateChrome();
    }
    private void OnWidgetClicked(object? sender, EventArgs e)
    {
        try { Activate(); } catch { }
        try
        {
            var hwnd = new WindowInteropHelper(this).Handle;
            if (hwnd != IntPtr.Zero) Win32Apis.SetForegroundWindow(hwnd);
        }
        catch { }
        _isActive = true;
        UpdateChrome();
    }

    /*private void OnWidgetMouseDown(object? sender, EventArgs e)
    {
        if (!MoveWindowByWidgetMouseDown || _container.IsLocked) return;
        if (_drag?.IsDragging == true) return;
        // WebView2CompositionControl has no airspace but still captures mouse; DragMove() fails when
        // invoked from async WebMessage (button state lost). Use _drag title loop via synthetic
        // WM_NCLBUTTONDOWN so WindowDragController's snapping (WmMoving/WmSizing) still applies.
        try
        {
            var hwnd = new WindowInteropHelper(this).Handle;
            if (hwnd != IntPtr.Zero)
            {
                const int WM_NCLBUTTONDOWN = 0x00A1;
                const int HTCAPTION = 2;
                User32.SendMessage(hwnd, WM_NCLBUTTONDOWN, (IntPtr)HTCAPTION, IntPtr.Zero);
            }
        }
        catch { }
    }*/

    private void ApplyDraggedBounds(RectD bounds)
    {
        _container.Bounds = bounds;
        Left = bounds.X;
        Top = bounds.Y;
        Width = bounds.Width;
        Height = bounds.Height;
    }

    private void OnSizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (!IsLoaded) return;
        _container.Bounds = RectD.FromXYWH(Left, Top, ActualWidth, ActualHeight);
        try { App.Services.GetRequiredService<DesktopManager>().SaveAsyncFireAndForget(); } catch { }
    }

    private void OnLocationChanged(object? sender, EventArgs e)
    {
        if (!IsLoaded) return;
        _container.Bounds = RectD.FromXYWH(Left, Top, ActualWidth, ActualHeight);
    }

    public void SetHover(bool hover) { _isHover = hover; UpdateChrome(); }
    bool HeaderIsShown() => _chromeOverlay?.IsVisible == true;
    bool CanShowHeader()
    {
        // Check both _isActive (WM_ACTIVATE, set before WPF updates IsActive) and IsActive/IsFocused.
        // ShowChromeOnHover gates hover; when false only activation matters.
        bool show = (ShowChromeOnHover && _isHover) || _isActive || this.IsActive || this.IsFocused || (ShowChromeOnHover && IsMouseOver);
        return show;
    }
    public override void UpdateChrome()
    {
        EnsureChromeOverlay();
        //if (_drag is null) return;
        //if (_drag.IsDragging || WindowDragController.IsNativeSizing) return;
        if (_chromeOverlay!.IsDragging || _chromeOverlay.IsResizing) return;
        // Keep chrome visible while user is interacting with it (mouse down gap before
        // WM_ENTERSIZEMOVE/WM_NCLBUTTONDOWN sets IsDragging). Without this, the
        // WM_ACTIVATE transient during NOACTIVATE caption click would hide it.
        if (_chromeOverlay.IsVisible && System.Windows.Input.Mouse.LeftButton == System.Windows.Input.MouseButtonState.Pressed) return;

        bool show = CanShowHeader();
        // Chrome is now in overlay window glued above WebView (no airspace, title shows over WebView2 HwndHost)
        if (_chromeOverlay != null)
        {
            bool shouldShow = show;
            if (shouldShow)
            {
                _chromeOverlay.SyncFromOwner();
                _chromeOverlay.UpdateTitle(Title);
                if (!_chromeOverlay.IsVisible)
                {
                    if (_chromeOverlay.ResizeMode != ResizeMode) _chromeOverlay.ResizeMode = ResizeMode;
                    _chromeOverlay.Show();
                }
                EnsureOverlayAboveHost();
            }
            else if (!shouldShow && _chromeOverlay.IsVisible) _chromeOverlay.Hide();
        }
    }

    private void EnsureChromeOverlay()
    {
        if (_chromeOverlay != null) return;
        _chromeOverlay = new CssWidgetChromeOverlay(this);
        _chromeOverlay.UpdateTitle(Title);
        _chromeOverlay.ResizeMode = ResizeMode;
        _chromeOverlay.Deactivated += _chromeOverlay_Deactivated;
        // Visibility controlled by UpdateChrome (ShowChromeOnHover)
    }

    private void _chromeOverlay_Deactivated(object? sender, EventArgs e)
    {
        if (!this.IsActive)
        {
            UpdateChrome();
        }
    }

    internal void EnsureOverlayAboveHost()
    {
        //try
        //{
        if (_chromeOverlay is null) return;
        var ohwnd = new WindowInteropHelper(_chromeOverlay).Handle;
        if (ohwnd == IntPtr.Zero) return;
        IntPtr ownerH = new WindowInteropHelper(this).Handle;
        if (ownerH == IntPtr.Zero) return;
        User32.SetWindowLongPtr(ohwnd, GWL_HWNDPARENT, ownerH);

        //User32.SetWindowPos(ohwnd, ownerH, 0, 0, 0, 0,
        //    SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE);
        //_chromeOverlay.Activate();
        //}
        //catch { }
    }

    private void MenuButton_Click(object sender, RoutedEventArgs e)
    {
        //WidgetMenu.PlacementTarget = MenuButton;
        //WidgetMenu.IsOpen = true;
        ShowWidgetMenu();
    }

    private void Edit_Click(object sender, RoutedEventArgs e)
    {
        var slug = _container.CssWidgetName ?? "";
        var source = _container.CssWidgetSource ?? CssWidgetSource.User;
        if (source == CssWidgetSource.App)
        {
            var info = _widgetService.TryGetWidget(slug, source);
            if (info is not null)
            {
                var html = File.Exists(info.HtmlPath) ? File.ReadAllText(info.HtmlPath) : "";
                var css = File.Exists(info.CssPath) ? File.ReadAllText(info.CssPath) : "";
                var js = File.Exists(info.JsPath) ? File.ReadAllText(info.JsPath) : "";
                var newSlug = _widgetService.CreateUserWidget(slug + "_copy", html, css, js, info.Manifest);
                var w2 = new WidgetDataWindow(newSlug, isNew: false);
                w2.ShowDialog();
            }
            return;
        }
        var w = new WidgetDataWindow(slug, isNew: false);
        if (w.ShowDialog() == true)
        {
            _widgetControl?.LoadWidget(slug, source);
            foreach (var win in System.Windows.Application.Current.Windows.OfType<CssWidgetWindow>())
            {
                if (win != this && string.Equals(win._container.CssWidgetName, slug, StringComparison.OrdinalIgnoreCase)
                    && win._container.CssWidgetSource == source)
                    win._widgetControl?.LoadWidget(slug, source);
            }
        }
    }

    private void Change_Click(object sender, RoutedEventArgs e)
    {
        var w = new WidgetsListWindow(selectMode: true);
        if (w.ShowDialog() == true && w.SelectedInfo is not null)
        {
            _container.CssWidgetName = w.SelectedInfo.Slug;
            _container.CssWidgetSource = w.SelectedInfo.Source;
            var info = w.SelectedInfo;
            Title = info.Manifest.Name ?? info.Slug;
            //TitleText.Text = Title;
            _widgetControl?.LoadWidget(info.Slug, info.Source);
            ApplyManifest(info.Manifest);
            Left = _container.Bounds.X;
            Top = _container.Bounds.Y;
            Width = _container.Bounds.Width > 0 ? _container.Bounds.Width : (info.Manifest.Width ?? 300);
            Height = _container.Bounds.Height > 0 ? _container.Bounds.Height : (info.Manifest.Height ?? 220);
            try { App.Services.GetRequiredService<DesktopManager>().SaveAsyncFireAndForget(); } catch { }
            //reload
            var manifest = LoadWidget();
            ApplyManifest(manifest);
            UpdateChrome();
        }
    }

    private void Duplicate_Click(object sender, RoutedEventArgs e)
    {
        var main = App.Services.GetRequiredService<MainViewModel>();
        main.CreateCssWidgetAt(
            _container.CssWidgetName ?? "widget",
            _container.CssWidgetSource ?? CssWidgetSource.User,
            _container.Bounds.X + 20, _container.Bounds.Y + 20,
            _container.Bounds.Width, _container.Bounds.Height);
        try { App.Services.GetRequiredService<DesktopManager>().SaveAsyncFireAndForget(); } catch { }
    }

    private void Lock_Click(object sender, RoutedEventArgs e)
    {
        _container.IsLocked = !_container.IsLocked;
        LockMenuItem.IsChecked = _container.IsLocked;
        var m = _widgetService.TryGetWidget(_container.CssWidgetName ?? "", _container.CssWidgetSource ?? CssWidgetSource.User)?.Manifest;
        ApplyManifest(m);
        try { App.Services.GetRequiredService<DesktopManager>().SaveAsyncFireAndForget(); } catch { }
    }

    private void Hide_Click(object sender, RoutedEventArgs e)
    {
        _desktopManager.HideContainer(_container.Id);
        _desktopManager.SaveAsyncFireAndForget();
    }

    private void Delete_Click(object sender, RoutedEventArgs e)
    {
        var dm = App.Services.GetRequiredService<DesktopManager>();
        dm.RemoveContainer(_container.Id);
        Close();
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        // Overlay must be closed before owner handle is destroyed — otherwise overlay's
        // HwndHook (KeepBelowApps/SyncOwnerToThis) and OwnerClosed re-entrancy run on half-torn-down owner.
        var overlay = _chromeOverlay;
        _chromeOverlay = null;
        if (overlay != null)
        {
            try { overlay.Deactivated -= _chromeOverlay_Deactivated; } catch { }
            try { overlay.Owner = null; } catch { }
            try { overlay.Close(); } catch { }
        }
        base.OnClosing(e);
    }

    protected override void OnClosed(EventArgs e)
    {
        // Prevent CoreWebView2Controller.IsVisible race on shutdown:
        // detach events, collapse and dispose WebView before the Window visual tree is torn down
        if (_widgetControl != null)
        {
            try { _widgetControl.WidgetMouseEnter -= OnWidgetMouseEnter; } catch { }
            try { _widgetControl.WidgetMouseLeave -= OnWidgetMouseLeave; } catch { }
            try { _widgetControl.WidgetClicked -= OnWidgetClicked; } catch { }
            //try { _widgetControl.WidgetMouseDown -= OnWidgetMouseDown; } catch { }
            try { _widgetControl.CleanupForShutdown(); } catch { }
            try { WidgetHost.Content = null; } catch { }
            _widgetControl = null;
        }
        // Overlay already closed in OnClosing — defensive null check only
        _chromeOverlay = null;
        if (_hwndSource != null)
        {
            try { _hwndSource.RemoveHook(HwndHook); } catch { }
            _hwndSource = null;
        }
        var hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd != IntPtr.Zero) try { Win32Apis.UnregisterBoxWindow(hwnd); } catch { }
        try { Detach(); } catch { }
        try { base.OnClosed(e); } catch { }
    }

    private void OnIsVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (!IsVisible) _widgetControl?.Suspend();
        else if (WindowState != WindowState.Minimized) _widgetControl?.Resume();
    }

    private void OnStateChanged(object? sender, EventArgs e)
    {
        if (WindowState == WindowState.Minimized) _widgetControl?.Suspend();
        else if (IsVisible) _widgetControl?.Resume();
    }

    private void WidgetMenu_Opened(object sender, RoutedEventArgs e)
    {
        WidgetMenuEdit.Visibility = ContainerViewModel.CssWidgetSource == CssWidgetSource.App ? Visibility.Collapsed : Visibility.Visible;
    }
}
