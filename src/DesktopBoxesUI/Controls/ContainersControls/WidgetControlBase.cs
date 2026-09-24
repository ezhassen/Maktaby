using DesktopBoxesUI.Helpers;
using DesktopBoxesUI.Views.Containers;
using System.Windows;
using System.Windows.Controls;
using Wpf.Ui.Appearance;
using WPFShared.Helpers;

namespace DesktopBoxesUI.Controls.ContainersControls;

public abstract class WidgetControlBase : UserControl
{
    protected WidgetWindow? _ownerWindow;
    protected bool IsOwnerWindowClosing => _ownerWindow?.IsClosing == true;
    //protected bool IsOwnerWindowClosed => _ownerWindow?.IsClosed == true;
    protected bool _isInitialized;

    public WidgetControlBase()
    {
        this.Loaded += WidgetControl_Loaded;
        this.Unloaded += WidgetControl_Unloaded;
    }

    private void WidgetControl_Loaded(object sender, RoutedEventArgs e)
    {
        if (HelperUI.IsInDesignMode)
        {
            LoadDesignTimeData();
            return;
        }
        //
        _ownerWindow = Window.GetWindow(this) as WidgetWindow;
        //
        try { IsVisibleChanged -= OnIsVisibleChanged; } catch { }
        try { IsVisibleChanged += OnIsVisibleChanged; } catch { }
        if (!_isInitialized) InitializeCtrls();
        // Per-load work (view re-assert, VM re-attach): Loaded refires on every Hide→Show
        // cycle, tab drag and re-parenting — this is the only place that runs each time.
        OnReloadedCore();
        // Apply idle state based on current visibility
        if (!IsVisible) Suspend(); else Resume();
    }

    private void InitializeCtrls()
    {
        SubscribeTheme();
        InitializeCtrlsCore();
        _isInitialized = true;
    }
    protected abstract void InitializeCtrlsCore();

    /// <summary>Runs on every <c>Loaded</c> (first load plus every Hide→Show / re-parent).
    /// For per-load view work: re-assert visuals, re-attach long-lived VMs (detach-first).
    /// One-time work belongs in <see cref="InitializeCtrlsCore"/> instead.</summary>
    protected virtual void OnReloadedCore() { }
    /// <summary>Unload rarely means close (Hide/Show cycles, tab drags, re-parenting all fire
    /// Unloaded without destroying anything): default OFF so hiding a widget can never dispose
    /// its content. Opt in only where close==unload is proven for that control.</summary>
    protected bool CleanupForShutdownOnUnloaded = false;

    /// <summary>
    /// Call this on owner window closing/closed when CleanupForShutdownOnUnloaded==false
    /// </summary>
    public void CleanupForShutdown()
    {
        try { UnsubscribeTheme(); } catch { }
        CleanupForShutdownCore();
        _isInitialized = false;
    }

    /// <summary>
    /// ...
    /// </summary>
    protected abstract void CleanupForShutdownCore();

    private void WidgetControl_Unloaded(object sender, RoutedEventArgs e)
    {
        try { OnUnloadedCore(); } catch { }
        if (_ownerWindow != null)
        {
            _ownerWindow = null;
        }
        UnsubscribeTheme();
        try { IsVisibleChanged -= OnIsVisibleChanged; } catch { }
        if (CleanupForShutdownOnUnloaded) CleanupForShutdown();
    }

    /// <summary>Runs on every <c>Unloaded</c> (Hide, tab drag, re-parent — rarely a real close).
    /// Dismiss transient UI and detach long-lived VMs so the unloaded control can't pin them.
    /// Never dispose content here (see <see cref="CleanupForShutdownOnUnloaded"/>).</summary>
    protected virtual void OnUnloadedCore() { }

    public virtual void LoadDesignTimeData()
    {

    }

    private void OnIsVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (IsOwnerWindowClosing) return;
        if (!IsVisible) Suspend();
        else Resume();
    }

    private bool _themeSubscribed;

    private void SubscribeTheme()
    {
        if (_themeSubscribed) return;
        try { ApplicationThemeManager.Changed += OnAppThemeChanged; _themeSubscribed = true; } catch { }
    }

    private void UnsubscribeTheme()
    {
        try { ApplicationThemeManager.Changed -= OnAppThemeChanged; } catch { }
        _themeSubscribed = false;
    }

    private void OnAppThemeChanged(ApplicationTheme current, System.Windows.Media.Color systemAccent)
    {
        if (!ShouldSwitchTheme()) return;
        try { Dispatcher.BeginInvoke(() => ApplyTheme()); } catch { }
    }

    protected abstract void ApplyTheme();
    public void RefreshTheme() => ApplyTheme();

    protected virtual bool ShouldSwitchTheme() => true;

    public abstract bool IsSuspended { get; protected set; }
    public void Suspend()
    {
        if (IsSuspended || IsOwnerWindowClosing || !IsLoaded) return;
        // Don't perform teardown if we're being detached
        if (this.IsBeingDetached()) return;
        IsSuspended = SuspendCore();
    }

    protected abstract bool SuspendCore();
    public void Resume()
    {
        // Early-out is on !IsSuspended (nothing to resume), and on !IsLoaded: resuming an
        // unloaded control is meaningless, while _isInitialized stays true across Hide/Show
        // cycles where IsLoaded tracks reality.
        if (!IsSuspended || IsOwnerWindowClosing || !IsLoaded) return;
        // Don't perform teardown if we're being detached
        if (this.IsBeingDetached()) return;
        IsSuspended = !ResumeCore();
    }

    protected abstract bool ResumeCore();

}
