using DesktopBoxesUI.Controls;
using DesktopBoxesUI.Core.Interfaces;
using DesktopBoxesUI.Core.Models;
using DesktopBoxesUI.ViewModels;
using DesktopBoxesUI.Win32.NativeMethods;
using Microsoft.Extensions.DependencyInjection;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;

namespace DesktopBoxesUI.Views;

public partial class CssWidgetWindow : Controls.WidgetWindow
{
    private readonly ContainerViewModel _vm;
    private readonly DesktopItemContainer _container;
    private readonly ICssWidgetService _widgetService;
    private CssWidgetControl? _widgetControl;
    private WindowDragController? _drag;
    private bool _isHover;
    private bool _isActive;
    private HwndSource? _hwndSource;

    public CssWidgetWindow(ContainerViewModel vm)
    {
        InitializeComponent();
        _vm = vm;
        _container = vm.Model;
        _widgetService = App.Services.GetRequiredService<ICssWidgetService>();

        var manifest = LoadWidget();
        ApplyManifest(manifest);

        Left = _container.Bounds.X;
        Top = _container.Bounds.Y;
        Width = _container.Bounds.Width > 0 ? _container.Bounds.Width : (manifest?.Width ?? 300);
        Height = _container.Bounds.Height > 0 ? _container.Bounds.Height : (manifest?.Height ?? 220);

        Title = manifest?.Name ?? _container.CssWidgetName ?? "Widget";
        TitleText.Text = Title;

        LockMenuItem.IsChecked = _container.IsLocked;
        UpdateChrome();
        Loaded += OnLoaded;
        SizeChanged += OnSizeChanged;
        LocationChanged += OnLocationChanged;
        // Hover/focus over the WebView HWND does not raise WPF hover reliably; use the root border
        // as the single hover source for the whole widget. This avoids the header/host transition churn
        // that caused the chrome to flicker while the pointer crossed between the title bar and the WebView.
        TitleArea.MouseLeftButtonDown += TitleArea_MouseDown;
        TitleArea.MouseMove += TitleArea_MouseMove;
        TitleArea.MouseLeftButtonUp += TitleArea_MouseUp;
        PreviewMouseLeftButtonDown += TitleArea_MouseDown;
        MouseMove += TitleArea_MouseMove;
        MouseUp += TitleArea_MouseUp;

        RootBorder.MouseEnter += (_, _) => { _isHover = true; UpdateChrome(); };
        RootBorder.MouseLeave += (_, _) => { _isHover = false; UpdateChrome(); };

        WidgetMenu.Closed += (_, _) => UpdateChrome();
        WidgetMenu.Opened += (_, _) => UpdateChrome();
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
        }
        _widgetControl = new CssWidgetControl();
        _widgetControl.WidgetMouseEnter += OnWidgetMouseEnter;
        _widgetControl.WidgetMouseLeave += OnWidgetMouseLeave;
        _widgetControl.WidgetClicked += OnWidgetClicked;
        WidgetHost.Content = _widgetControl;
        _widgetControl.LoadWidget(info.Slug, info.Source);
        return info.Manifest;
    }

    private void ApplyManifest(CssWidgetManifest? manifest)
    {
        bool resizable = manifest?.IsResizable ?? true;
        if (_container.IsLocked) resizable = false;
        ResizeMode = resizable ? ResizeMode.CanResize : ResizeMode.NoResize;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd != IntPtr.Zero)
        {
            Win32Apis.MakeToolWindow(hwnd);
            Win32Apis.RegisterBoxWindow(hwnd);
            _hwndSource = HwndSource.FromHwnd(hwnd);
            _hwndSource?.AddHook(HwndHook);
        }
        var monitor = App.Services.GetRequiredService<IMonitorService>();
        var dpi = App.Services.GetRequiredService<IDpiService>();
        var snapping = App.Services.GetRequiredService<IWindowSnappingService>();
        var positioning = App.Services.GetRequiredService<IWindowPositioningService>();
        Action save = () => { try { App.Services.GetRequiredService<DesktopManager>().SaveAsyncFireAndForget(); } catch { } };
        _drag = new WindowDragController(
            this, App.Services.GetRequiredService<DesktopManager>(), monitor, dpi, snapping, positioning,
            r => ApplyDraggedBounds(r),
            () => App.Services.GetRequiredService<IContainerService>().GetContainers()
                    .Where(c => c.Id != _container.Id && c.IsVisible)
                    .Select(c => c.Bounds).ToList(),
            save,
            () => HeaderBorder.ActualHeight);
        _drag.Attach();
        UpdateChrome();
    }

    private IntPtr HwndHook(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        const int WM_MOUSEACTIVATE = 0x0021;
        const int WM_ACTIVATE = 0x0006;
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
            if (hwnd != IntPtr.Zero && Win32Apis.GetWindowRect((Windows.Win32.Foundation.HWND)hwnd, out var rect))
            {
                if (pt.X >= rect.left && pt.X <= rect.right && pt.Y >= rect.top && pt.Y <= rect.bottom)
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

    bool HeaderIsShown() => HeaderBorder.Visibility == Visibility.Visible;
    bool CanShowHeader()
    {
        bool show = _isHover || this.IsActive || this.IsFocused;//|| this.IsKeyboardFocused;// || WidgetMenu.IsOpen;
        if (!show) show = IsMouseOver;//|| IsKeyboardFocusWithin;
        return show;
    }
    private void UpdateChrome()
    {
        if (_drag is null) return;
        if (_drag.IsDragging || WindowDragController.IsNativeSizing) return;
        bool show = CanShowHeader();
        //if (!show) show = IsMouseOver || IsKeyboardFocusWithin;

        // Only toggle chrome when the pointer truly enters/leaves the widget bounds; avoid per-move
        // updates so the title bar does not flicker while the pointer crosses the transparent WebView.
        HeaderBorder.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        //bool canResize = ResizeMode == ResizeMode.CanResize || ResizeMode == ResizeMode.CanResizeWithGrip;
        RootBorder.BorderThickness = new Thickness(show ? 3 : 0);
    }

    private void TitleArea_MouseDown(object sender, MouseButtonEventArgs e)
    {
        if (_container.IsLocked) return;
        _drag?.BeginTitleDrag(e);
    }
    private void TitleArea_MouseMove(object sender, MouseEventArgs e)
    {
        if (_container.IsLocked) return;
        _drag?.TitleDrag(e);
        if (_drag?.IsDragging == true) UpdateChrome();
    }
    private void TitleArea_MouseUp(object sender, MouseButtonEventArgs e)
    {
        _drag?.EndTitleDrag(e);
        UpdateChrome();
    }

    private void MenuButton_Click(object sender, RoutedEventArgs e)
    {
        WidgetMenu.PlacementTarget = MenuButton;
        WidgetMenu.IsOpen = true;
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
            TitleText.Text = Title;
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

    private void Delete_Click(object sender, RoutedEventArgs e)
    {
        var dm = App.Services.GetRequiredService<DesktopManager>();
        dm.RemoveContainer(_container.Id);
        Close();
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
            try { _widgetControl.CleanupForShutdown(); } catch { }
            try { WidgetHost.Content = null; } catch { }
            _widgetControl = null;
        }
        if (_hwndSource != null)
        {
            try { _hwndSource.RemoveHook(HwndHook); } catch { }
            _hwndSource = null;
        }
        var hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd != IntPtr.Zero) try { Win32Apis.UnregisterBoxWindow(hwnd); } catch { }
        try { _drag?.Detach(); } catch { }
        try { base.OnClosed(e); } catch { }
    }
}
