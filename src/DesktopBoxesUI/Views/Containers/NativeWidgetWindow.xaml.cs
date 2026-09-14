using DesktopBoxes.WidgetSdk;
using DesktopBoxesUI.Core.Interfaces;
using DesktopBoxesUI.Core.Models;
using DesktopBoxesUI.Helpers;
using DesktopBoxesUI.ViewModels;
using DesktopBoxesUI.Win32.NativeMethods;
using Microsoft.Extensions.DependencyInjection;
using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using Wpf.Ui.Appearance;
using WindowsNative;
using static WindowsNative.Win32Constants;

namespace DesktopBoxesUI.Views.Containers;

/// <summary>Host window for a native plugin widget (<see cref="DesktopItemContainerType.NativeWidget"/>).
/// Mirrors <see cref="CssWidgetWindow"/>: borderless tool window glued to the desktop layer with an
/// external <see cref="WidgetChromeOverlay"/> (shared via <see cref="IWidgetChromeOwner"/>),
/// manifest-driven resize behavior, visibility-driven plugin suspend/resume, and hybrid
/// interaction detection (WPF routed events on the plugin visual merged with the plugin's
/// opt-in <see cref="INativeWidget"/> events).</summary>
public partial class NativeWidgetWindow : WidgetWindow, IWidgetChromeOwner
{
    public override ContainerViewModel ContainerViewModel { get; }
    Window IWidgetChromeOwner.Window => this;
    MenuItem? IWidgetChromeOwner.LockMenuItem => LockMenuItem;

    private readonly DesktopItemContainer _container;
    private readonly INativeWidgetService _widgetService;
    private INativeWidget? _plugin;
    private NativeWidgetInfo? _info;
    private int _loadGeneration;
    private WidgetChromeOverlay? _chromeOverlay;
    private HwndSource? _hwndSource;
    private bool _isHover;
    private bool _isActive;
    private readonly DesktopManager _desktopManager;
    private bool ShowChromeOnHover = false;

    /// <summary>Live plugin instance, if one is attached (used by auto-pause/diagnostics).</summary>
    internal INativeWidget? Widget => _plugin;
    /// <summary>Live chrome overlay, if one has been created (used by layout diagnostics).</summary>
    internal WidgetChromeOverlay? ChromeOverlay => _chromeOverlay;

    public NativeWidgetWindow(ContainerViewModel vm)
    {
        InitializeComponent();
        ContainerViewModel = vm;
        _container = vm.Model;
        _widgetService = App.Services.GetRequiredService<INativeWidgetService>();
        _desktopManager = App.Services.GetRequiredService<DesktopManager>();

        // Manifest-only resolve here (fast, loads no code); the plugin itself loads async.
        var info = TryResolveInfo();
        ApplyManifest(info?.Manifest);
        Title = string.IsNullOrWhiteSpace(info?.Manifest.Name) ? (info?.Slug ?? "Widget") : info.Manifest.Name;

        Left = _container.Bounds.X;
        Top = _container.Bounds.Y;
        Width = _container.Bounds.Width > 0 ? _container.Bounds.Width : (info?.Manifest.Width ?? 300);
        Height = _container.Bounds.Height > 0 ? _container.Bounds.Height : (info?.Manifest.Height ?? 220);

        LoadNativeWidgetAsync();

        Loaded += OnLoaded;
        IsVisibleChanged += OnIsVisibleChanged;
        StateChanged += OnStateChanged;
        try { ApplicationThemeManager.Changed += OnAppThemeChanged; } catch { }
        this.LockMenuItem.IsChecked = vm.IsLocked;
        Activated += (_, _) => { _isActive = true; UpdateChrome(); };
        Deactivated += (_, _) => { _isActive = false; _isHover = false; UpdateChrome(); };
    }

    private NativeWidgetInfo? TryResolveInfo()
    {
        try { return _widgetService.TryGetWidget(_container.NativeWidgetName ?? ""); }
        catch { return null; }
    }

    private void LoadNativeWidgetAsync()
    {
        int generation = Interlocked.Increment(ref _loadGeneration);
        _ = LoadNativeWidgetCoreAsync(generation);
    }

    private async Task LoadNativeWidgetCoreAsync(int generation)
    {
        ShowPlaceholder("Loading…", error: false);
        var info = TryResolveInfo();
        if (Volatile.Read(ref _loadGeneration) != generation) return;
        if (info is null)
        {
            ShowPlaceholder($"Native widget '{_container.NativeWidgetName}' not found");
            return;
        }
        if (info.LoadError is not null)
        {
            ShowPlaceholder(info.LoadError);
            return;
        }
        _info = info;
        Title = string.IsNullOrWhiteSpace(info.Manifest.Name) ? info.Slug : info.Manifest.Name;
        ApplyManifest(info.Manifest);

        // Fail closed: untrusted user content never compiles or loads. App widgets are
        // implicitly trusted; user widgets are trusted once per content hash via the
        // gallery Place prompt.
        bool trusted = false;
        try { trusted = _widgetService.IsTrusted(info); } catch { }
        if (!trusted)
        {
            ShowPlaceholder($"Untrusted plugin '{info.Slug}'. Place it from the widget gallery to review and trust it.");
            return;
        }

        INativeWidget plugin;
        try
        {
            // Compile off the UI thread (can take seconds), instantiate on it (WPF
            // requires STA for visual construction — Task.Run would throw).
            string assemblyPath = await Task.Run(() => _widgetService.GetAssemblyPath(info));
            if (Volatile.Read(ref _loadGeneration) != generation) return;
            plugin = _widgetService.CreateInstance(assemblyPath, info);
        }
        catch (NativeWidgetLoadException ex) { ShowPlaceholder(ex.Reason); return; }
        catch (Exception ex) { ShowPlaceholder(ex.Message); return; }
        if (Volatile.Read(ref _loadGeneration) != generation)
        {
            try { plugin.Dispose(); } catch { }
            return;
        }
        AttachPlugin(plugin);
    }

    private void ShowPlaceholder(string text)
    {
        ShowPlaceholder(text, error: true);
    }

    private void ShowPlaceholder(string text, bool error)
    {
        try
        {
            WidgetHost.Content = new TextBlock
            {
                Text = text,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(12),
                Foreground = error
                    ? System.Windows.Media.Brushes.IndianRed
                    : System.Windows.Media.Brushes.Gray,
            };
        }
        catch { }
    }

    private void AttachPlugin(INativeWidget plugin)
    {
        DetachPlugin();
        _plugin = plugin;
        FrameworkElement visual;
        try
        {
            visual = plugin.Visual;
            if (visual is null) throw new InvalidOperationException("Plugin returned a null visual.");
        }
        catch (Exception ex)
        {
            ShowPlaceholder(ex.Message);
            try { plugin.Dispose(); } catch { }
            _plugin = null;
            return;
        }
        try
        {
            // Host-detected half of the hybrid interaction model (works with zero plugin code).
            visual.MouseEnter += OnVisualMouseEnter;
            visual.MouseLeave += OnVisualMouseLeave;
            visual.PreviewMouseLeftButtonUp += OnVisualClicked;
            visual.GotFocus += OnVisualFocused;
            visual.LostFocus += OnVisualLostFocus;
            // Plugin-raised half (covers HWND-hosted content the WPF tree cannot see).
            plugin.Entered += OnPluginEntered;
            plugin.Left += OnPluginLeft;
            plugin.Clicked += OnPluginClicked;
            plugin.Focused += OnPluginFocused;
            plugin.Unfocused += OnPluginUnfocused;
            WidgetHost.Content = visual;
        }
        catch (Exception ex)
        {
            ShowPlaceholder(ex.Message);
            try { plugin.Dispose(); } catch { }
            _plugin = null;
            return;
        }
        ApplyThemeToPlugin();
        if (!IsVisible || WindowState == WindowState.Minimized)
        {
            try { plugin.Suspend(); } catch { }
        }
    }

    private void DetachPlugin()
    {
        var plugin = Interlocked.Exchange(ref _plugin, null);
        if (plugin is null) return;
        try
        {
            FrameworkElement? visual = null;
            try { visual = plugin.Visual; } catch { }
            if (visual is not null)
            {
                visual.MouseEnter -= OnVisualMouseEnter;
                visual.MouseLeave -= OnVisualMouseLeave;
                visual.PreviewMouseLeftButtonUp -= OnVisualClicked;
                visual.GotFocus -= OnVisualFocused;
                visual.LostFocus -= OnVisualLostFocus;
            }
            plugin.Entered -= OnPluginEntered;
            plugin.Left -= OnPluginLeft;
            plugin.Clicked -= OnPluginClicked;
            plugin.Focused -= OnPluginFocused;
            plugin.Unfocused -= OnPluginUnfocused;
        }
        catch { }
        try { WidgetHost.Content = null; } catch { }
        try { plugin.Dispose(); } catch { }
    }

    private void ApplyManifest(NativeWidgetManifest? manifest)
    {
        bool resizable = manifest?.Resizable ?? true;
        if (_container.IsLocked) resizable = false;
        ResizeMode = resizable ? ResizeMode.CanResize : ResizeMode.NoResize;
        if (_chromeOverlay is not null)
        {
            _chromeOverlay.ResizeMode = ResizeMode;
        }
    }

    private void ApplyThemeToPlugin()
    {
        var plugin = _plugin;
        if (plugin is null || _info?.Manifest.SupportsTheme != true) return;
        try { plugin.ApplyTheme(ResolveTheme()); } catch { }
    }

    private string? ResolveTheme()
    {
        string? global = null;
        try { global = App.Services.GetRequiredService<ISettingsService>().UserSettings.DefaultCSSWidgetsTheme?.Trim().ToLowerInvariant(); } catch { }
        if (global == "dark") return "dark";
        if (global == "light") return "light";
        try
        {
            var app = ApplicationThemeManager.GetAppTheme();
            if (app == ApplicationTheme.Dark) return "dark";
            if (app == ApplicationTheme.Light) return "light";
            return ApplicationThemeManager.GetSystemTheme() == SystemTheme.Dark ? "dark" : "light";
        }
        catch { return null; }
    }

    private void OnAppThemeChanged(ApplicationTheme current, System.Windows.Media.Color systemAccent)
    {
        if (_info?.Manifest.SupportsTheme != true) return;
        try { Dispatcher.BeginInvoke(ApplyThemeToPlugin); } catch { }
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        Attach();
    }
    bool _attached;
    private void Attach()
    {
        if (_attached) return;
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
            // Allow click on plugin content to activate the window.
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
            if (_chromeOverlay == null || (!_chromeOverlay.IsDragging && !_chromeOverlay.IsResizing))
                WindowDragController.KeepBelowApps(hwnd, lParam, _desktopManager);
        }
        return IntPtr.Zero;
    }

    // ---- interaction: WPF-routed half ----
    private void OnVisualMouseEnter(object sender, MouseEventArgs e) => SetHovered(true);
    private void OnVisualMouseLeave(object sender, MouseEventArgs e) => SetHovered(false);
    private void OnVisualClicked(object sender, MouseButtonEventArgs e) => ActivateWidget();
    private void OnVisualFocused(object sender, RoutedEventArgs e) => UpdateChrome();
    private void OnVisualLostFocus(object sender, RoutedEventArgs e) => UpdateChrome();

    // ---- interaction: plugin-raised half ----
    private void OnPluginEntered(object? sender, EventArgs e) => SetHovered(true);
    private void OnPluginLeft(object? sender, EventArgs e)
    {
        // Same guard as the CSS window: ignore leaves while the cursor is physically
        // still inside (plugins can fire them moving between inner elements).
        try
        {
            Win32Apis.GetCursorPos(out var pt);
            var hwnd = new WindowInteropHelper(this).Handle;
            if (hwnd != IntPtr.Zero && Win32Apis.GetWindowRect(hwnd, out var rect))
            {
                if (pt.X >= rect.Left && pt.X <= rect.Right && pt.Y >= rect.Top && pt.Y <= rect.Bottom)
                    return;
            }
        }
        catch { }
        SetHovered(false);
    }
    private void OnPluginClicked(object? sender, EventArgs e) => ActivateWidget();
    private void OnPluginFocused(object? sender, EventArgs e) => UpdateChrome();
    private void OnPluginUnfocused(object? sender, EventArgs e) => UpdateChrome();

    private void SetHovered(bool hover)
    {
        if (_isHover == hover) return;
        _isHover = hover;
        UpdateChrome();
    }

    private void ActivateWidget()
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

    public void SetHover(bool hover) { _isHover = hover; UpdateChrome(); }
    bool HeaderIsShown() => _chromeOverlay?.IsVisible == true;
    bool CanShowHeader()
    {
        bool show = (ShowChromeOnHover && _isHover) || _isActive || this.IsActive || this.IsFocused || (ShowChromeOnHover && IsMouseOver);
        return show;
    }
    public override void UpdateChrome()
    {
        EnsureChromeOverlay();
        if (_chromeOverlay!.IsDragging || _chromeOverlay.IsResizing) return;
        // Keep chrome visible while user is interacting with it (mouse down gap before
        // WM_ENTERSIZEMOVE/WM_NCLBUTTONDOWN sets IsDragging).
        if (_chromeOverlay.IsVisible && System.Windows.Input.Mouse.LeftButton == System.Windows.Input.MouseButtonState.Pressed) return;

        bool show = CanShowHeader();
        if (_chromeOverlay != null)
        {
            if (show)
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
            else if (_chromeOverlay.IsVisible) _chromeOverlay.Hide();
            else
            {
                // Keep a hidden overlay tracking the owner (a DPI transition remaps hidden
                // windows too). Invisible move, no model write — SyncFromOwner only aligns it.
                _chromeOverlay.SyncFromOwner();
            }
        }
    }

    private void EnsureChromeOverlay()
    {
        if (_chromeOverlay != null) return;
        _chromeOverlay = new WidgetChromeOverlay(this);
        _chromeOverlay.UpdateTitle(Title);
        _chromeOverlay.ResizeMode = ResizeMode;
        _chromeOverlay.Deactivated += _chromeOverlay_Deactivated;
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
        if (_chromeOverlay is null) return;
        var ohwnd = new WindowInteropHelper(_chromeOverlay).Handle;
        if (ohwnd == IntPtr.Zero) return;
        IntPtr ownerH = new WindowInteropHelper(this).Handle;
        if (ownerH == IntPtr.Zero) return;
        User32.SetWindowLongPtr(ohwnd, GWL_HWNDPARENT, ownerH);
    }

    private void Change_Click(object sender, RoutedEventArgs e)
    {
        var w = new WidgetsListWindow(selectMode: true, selectKind: WidgetGalleryKind.Native);
        if (w.ShowDialog() == true && w.SelectedNativeInfo is not null)
        {
            var info = w.SelectedNativeInfo;
            _container.NativeWidgetName = info.Slug;
            Title = string.IsNullOrWhiteSpace(info.Manifest.Name) ? info.Slug : info.Manifest.Name;
            Left = _container.Bounds.X;
            Top = _container.Bounds.Y;
            Width = _container.Bounds.Width > 0 ? _container.Bounds.Width : info.Manifest.Width;
            Height = _container.Bounds.Height > 0 ? _container.Bounds.Height : info.Manifest.Height;
            try { App.Services.GetRequiredService<DesktopManager>().SaveAsyncFireAndForget(); } catch { }
            LoadNativeWidgetAsync();
            UpdateChrome();
        }
    }

    private void Duplicate_Click(object sender, RoutedEventArgs e)
    {
        var main = App.Services.GetRequiredService<MainViewModel>();
        main.CreateNativeWidgetAt(
            _container.NativeWidgetName ?? "widget",
            _container.Bounds.X + 20, _container.Bounds.Y + 20,
            _container.Bounds.Width, _container.Bounds.Height);
        try { App.Services.GetRequiredService<DesktopManager>().SaveAsyncFireAndForget(); } catch { }
    }

    private void Lock_Click(object sender, RoutedEventArgs e)
    {
        _container.IsLocked = !_container.IsLocked;
        LockMenuItem.IsChecked = _container.IsLocked;
        ApplyManifest(_info?.Manifest);
        try { App.Services.GetRequiredService<DesktopManager>().SaveAsyncFireAndForget(); } catch { }
    }

    private async void Hide_Click(object sender, RoutedEventArgs e)
    {
        await ContainerHideHint.MaybeShowAsync(this);
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
        base.OnClosing(e);
        if (e.Cancel) return;
        var overlay = _chromeOverlay;
        _chromeOverlay = null;
        if (overlay != null)
        {
            try { overlay.Deactivated -= _chromeOverlay_Deactivated; } catch { }
            try { overlay.Owner = null; } catch { }
            try { overlay.Close(); } catch { }
        }
    }

    /// <summary>Detaches and disposes the plugin. Must run BEFORE <see cref="Window.Close"/>:
    /// mirrors the WebView2 shutdown ordering (control out of the visual tree first).
    /// Idempotent — also called from <see cref="OnClosed"/> as a fallback.</summary>
    public void PrepareForClose() => ShutdownPlugin();

    private void ShutdownPlugin()
    {
        Interlocked.Increment(ref _loadGeneration); // invalidate in-flight loads
        try { ApplicationThemeManager.Changed -= OnAppThemeChanged; } catch { }
        DetachPlugin();
    }

    protected override void OnClosed(EventArgs e)
    {
        ShutdownPlugin();
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
        if (!IsVisible) _plugin?.Suspend();
        else if (WindowState != WindowState.Minimized) _plugin?.Resume();
    }

    private void OnStateChanged(object? sender, EventArgs e)
    {
        if (WindowState == WindowState.Minimized) _plugin?.Suspend();
        else if (IsVisible) _plugin?.Resume();
    }
}
