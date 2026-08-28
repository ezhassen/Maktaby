using DesktopBoxesUI.Core.Interfaces;
using DesktopBoxesUI.Core.Models;
using DesktopBoxesUI.ViewModels;
using DesktopBoxesUI.Views;
using DesktopBoxesUI.Win32.NativeMethods;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Win32;
using System.Collections.Specialized;
using System.IO;
using System.Runtime.Versioning;
using System.Windows;
using System.Windows.Interop;

namespace DesktopBoxesUI;

/// <summary>
/// Orchestrates the live container windows. One window per <see cref="ContainerViewModel"/>
/// (i.e. per <see cref="DesktopItemContainer"/>): a <see cref="BoxContainerWindow"/> for
/// <see cref="DesktopItemContainerType.BoxContainer"/>, a <see cref="BoxContainerWindow"/> for
/// <see cref="DesktopItemContainerType.Custom"/>. Keeps the windows in sync with the view-model
/// collection, owns loading/saving the snapshot (including desktop resolution for rescaling), and
/// hides/restores Explorer's desktop icons. Dependencies come from the composition root.
/// </summary>
[SupportedOSPlatform("windows10.0.14393")]
public sealed class DesktopManager
{
    #region fields

    private readonly MainViewModel _mainVm;
    private readonly IContainerService _containers;
    private readonly IPersistenceService _persistence;
    private readonly IDesktopService _desktop;
    private readonly IWindowPositioningService _positioning;
    private readonly IExplorerDesktopService _explorer;
    private readonly IRuleService _rules;
    private readonly IBoxService _boxRegistry;
    private readonly IFileRuleCoordinator _coordinator;
    private readonly IMouseMonitor _mouseMonitor;

    private readonly Dictionary<System.Guid, Window> _windows = new();
    private DesktopSurface? _surface;
    private bool _allBoxesHidden;

    /// <summary>When true all app functionality is suspended: boxes, surface, watchers and shell
    /// hooks are stopped. Only the tray icon remains. <see cref="ToggleDisableAsync"/> flips it.</summary>
    public bool IsDisabled { get; private set; }

    /// <summary>The work-area resolution the current container layout was computed against. When the
    /// display settings change we rescale every container proportionally against this baseline.</summary>
    private RectD _appliedResolution;

    #endregion

    #region Init

    public DesktopManager(IServiceProvider provider)
    {
        _mainVm = provider.GetRequiredService<MainViewModel>();
        _containers = provider.GetRequiredService<IContainerService>();
        _persistence = provider.GetRequiredService<IPersistenceService>();
        _desktop = provider.GetRequiredService<IDesktopService>();
        _positioning = provider.GetRequiredService<IWindowPositioningService>();
        _explorer = provider.GetRequiredService<IExplorerDesktopService>();
        _rules = provider.GetRequiredService<IRuleService>();
        _boxRegistry = provider.GetRequiredService<IBoxService>();
        _coordinator = provider.GetRequiredService<IFileRuleCoordinator>();
        _mouseMonitor = provider.GetRequiredService<IMouseMonitor>();
    }

    public async Task InitializeAsync()
    {
        //the init can be called multiple times so
        _coordinator.Stop();
        _mouseMonitor.Stop();
        //
        await _explorer.SetDesktopIconsVisibleAsync(false);
        var snapshot = await _persistence.LoadSnapshotAsync();
        if (snapshot is not { Containers.Count: > 0 })
        {
            await BuildDefaultContainerAsync();
            RegisterAllBoxes();
            _rules.EnsureDefaultRule(DefaultBoxId());
        }
        else
        {
            RescaleIfNeeded(snapshot);
            foreach (var container in snapshot.Containers)
            {
                _containers.AddContainer(container);
            }

            RegisterAllBoxes();

            if (snapshot.Rules?.Count > 0)
            {
                _rules.LoadRules(snapshot.Rules);
            }

            EnsureDefaultRuleAndBox();
        }

        // Build the container view-models off the UI thread (this also kicks off the per-item icon loads)
        // so the splash keeps animating, then add the already-built VMs on the UI thread. The
        // CollectionChanged handler is detached so we don't synchronously spin up windows during the load.
        _mainVm.Containers.CollectionChanged -= Containers_CollectionChanged;
        _mainVm.Containers.Clear();
        var builtVms = await Task.Run(() => _mainVm.BuildContainerViewModels(_containers.GetContainers()));
        foreach (var vm in builtVms)
        {
            _mainVm.Containers.Add(vm);
        }

        _mainVm.Containers.CollectionChanged += Containers_CollectionChanged;

        EnsureSurface();

        // Show the first container right away so the desktop isn't empty, then lazily stream the
        // remaining container windows in the background (after the splash closes) for an instant startup.
        // This defers window creation only — item drag/drop logic is untouched.
        if (_mainVm.Containers.Count > 0)
        {
            AddWindow(_mainVm.Containers[0], showActivated: false);//focusWorkaround: true
        }

        _ = StreamRemainingContainersAsync();

        await ReconcileItemsAsync();

        _appliedResolution = GetPrimaryWorkAreaDip();
        SystemEvents.DisplaySettingsChanged -= OnDisplaySettingsChanged;
        SystemEvents.DisplaySettingsChanged += OnDisplaySettingsChanged;

        //
        _coordinator.Start();
        if (GlobalFeaturesSwitches.UseGlobalMouseHookInsteadOfCustomSurface == true) _mouseMonitor.Start();
    }

    /// <summary>
    /// Toggles between fully operational and suspended. When disabled, all boxes, the surface,
    /// shell watchers and mouse hooks are stopped — only the tray icon remains. When re-enabled
    /// the app is restored via <see cref="InitializeAsync"/>.
    /// </summary>
    public async Task ToggleDisableAsync()
    {
        if (IsDisabled)
        {
            // --- Re-enable: full initialization restores boxes, surface and watchers ---
            IsDisabled = false;
            App.SyncDesktopIconSizeWatcher();
            await InitializeAsync();
        }
        else
        {
            // --- Disable: save state, stop everything, close all windows except tray host ---
            IsDisabled = true;

            SaveSync();
            _mouseMonitor.Stop();
            App.Services.GetRequiredService<Core.Interfaces.IDesktopIconSizeService>().Stop();
            CloseAll();
            RestoreIcons();
        }
    }

    #endregion


    #region Scale

    /// <summary>
    /// If the saved desktop resolution differs from the current one, proportionally rescale every
    /// container's bounds so the layout is preserved. BoxContainer-type bounds live on the wrapped
    /// <see cref="BoxContainer"/>; Custom-type bounds live on the container itself.
    /// </summary>
    private void RescaleIfNeeded(DesktopSnapshot snapshot)
    {
        if (snapshot.DesktopResolution.Width <= 0 || snapshot.DesktopResolution.Height <= 0)
        {
            return;
        }

        var current = GetPrimaryWorkAreaDip();

        double maxRight = 0;
        double maxBottom = 0;
        foreach (var container in snapshot.Containers)
        {
            maxRight = Math.Max(maxRight, container.Bounds.Right);
            maxBottom = Math.Max(maxBottom, container.Bounds.Bottom);
        }

        // Older snapshots stored DesktopResolution in PHYSICAL pixels while bounds are DIPs. If the
        // stored resolution is clearly larger than the current DIP work area yet the bounds already
        // fit inside it, the bounds are correctly placed in DIP space — just re-baseline (the
        // resolution is rewritten correctly on the next save) instead of wrongly shrinking them.
        bool storedLooksPhysical = snapshot.DesktopResolution.Width > current.Width * 1.15;
        if (storedLooksPhysical && maxRight <= current.Width && maxBottom <= current.Height)
        {
            return;
        }

        if (current.Width == snapshot.DesktopResolution.Width &&
            current.Height == snapshot.DesktopResolution.Height)
        {
            return;
        }

        double sx = current.Width / snapshot.DesktopResolution.Width;
        double sy = current.Height / snapshot.DesktopResolution.Height;

        foreach (var container in snapshot.Containers)
        {
            var b = container.Bounds;
            container.Bounds = RectD.FromXYWH(b.X * sx, b.Y * sy, b.Width * sx, b.Height * sy);
        }
    }

    /// <summary>
    /// The primary work area in WPF logical (DIP) coordinates. Container <c>Bounds</c> are always stored
    /// in this same space (the WPF window geometry), so this is the correct basis for the persisted
    /// <see cref="DesktopSnapshot.DesktopResolution"/> and for rescaling — never the raw physical pixels
    /// from <see cref="IMonitorService.GetPrimaryWorkArea"/>.
    /// </summary>
    private static RectD GetPrimaryWorkAreaDip()
    {
        var wa = SystemParameters.WorkArea;
        return RectD.FromXYWH(wa.X, wa.Y, wa.Width, wa.Height);
    }

    /// <summary>
    /// Reacts to a screen/DPI/resolution change by proportionally rescaling the container layout against the
    /// previously applied work area, pushing the new geometry to the live windows and re-laying the surface.
    /// Runs on the UI thread (marshalled via the dispatcher if the system event arrives off-thread).
    /// </summary>
    private void OnDisplaySettingsChanged(object? sender, EventArgs e)
    {
        var app = Application.Current;
        if (app is null)
        {
            return;
        }

        if (app.Dispatcher.CheckAccess())
        {
            RescaleToCurrent();
        }
        else
        {
            app.Dispatcher.InvokeAsync(RescaleToCurrent);
        }
    }

    private void RescaleToCurrent()
    {
        var current = GetPrimaryWorkAreaDip();
        if (current.Width <= 0 || current.Height <= 0 ||
            _appliedResolution.Width <= 0 || _appliedResolution.Height <= 0)
        {
            return;
        }

        if (current.Width == _appliedResolution.Width && current.Height == _appliedResolution.Height)
        {
            return;
        }

        double sx = current.Width / _appliedResolution.Width;
        double sy = current.Height / _appliedResolution.Height;

        foreach (var vm in _mainVm.Containers)
        {
            vm.Left *= sx;
            vm.Top *= sy;
            vm.Width *= sx;
            vm.Height *= sy;
        }

        _appliedResolution = current;

        foreach (var window in _windows.Values)
        {
            if (window is BoxContainerWindow boxWindow)
            {
                boxWindow.ApplyGeometry();
            }
            else if (window is CssWidgetWindow widgetWindow)
            {
                widgetWindow.Left = widgetWindow.DataContext is ContainerViewModel vm ? vm.Left : widgetWindow.Left;
                widgetWindow.Top = widgetWindow.DataContext is ContainerViewModel wvm ? wvm.Top : widgetWindow.Top;
                // Size also scaled via vm already; window will follow via binding or we set explicitly
                if (window.DataContext is ContainerViewModel cvm)
                {
                    window.Width = cvm.Width;
                    window.Height = cvm.Height;
                }
            }
        }

        _surface?.Relayout();

        _ = SaveAsync();
    }

    #endregion

    #region Items management


    /// <summary>
    /// Enumerates the live desktop on a dedicated STA thread. The Shell COM walk
    /// (<c>IShellFolder.EnumObjects</c>, <c>IShellItem</c> display names) must run on an STA thread and is
    /// CPU/IO heavy, so running it off the WPF UI thread keeps startup responsive. Only plain
    /// <see cref="BoxItem"/> models cross the thread boundary — no COM objects are marshalled.
    /// </summary>
    private Task<List<BoxItem>> EnumerateDesktopAsync(CancellationToken cancellationToken = default)
    {
        var tcs = new TaskCompletionSource<List<BoxItem>>(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try
            {
                var items = new List<BoxItem>();
                var enumerator = _desktop.GetDesktopItemsAsync(cancellationToken).GetAsyncEnumerator(cancellationToken);
                try
                {
                    while (enumerator.MoveNextAsync().AsTask().GetAwaiter().GetResult())
                    {
                        items.Add(enumerator.Current);
                    }
                }
                finally
                {
                    enumerator.DisposeAsync().AsTask().GetAwaiter().GetResult();
                }

                tcs.TrySetResult(items);
            }
            catch (Exception ex)
            {
                tcs.TrySetException(ex);
            }
        });

        thread.SetApartmentState(ApartmentState.STA);
        thread.IsBackground = true;
        thread.Start();
        return tcs.Task;
    }

    /// <summary>
    /// Resolves the id of the default box (the one the default rule feeds). Falls back to the first
    /// box found if no box is flagged.
    /// </summary>
    private System.Guid? DefaultBoxId()
    {
        foreach (var container in _containers.GetContainers())
        {
            if (container.ChildContainer is null)
            {
                continue;
            }

            foreach (var box in container.ChildContainer.Boxes)
            {
                if (box.IsDefault)
                {
                    return box.Id;
                }
            }
        }

        foreach (var container in _containers.GetContainers())
        {
            if (container.ChildContainer is { Boxes.Count: > 0 })
            {
                return container.ChildContainer.Boxes[0].Id;
            }
        }

        return null;
    }

    /// <summary>
    /// Makes sure a default rule exists and that the box it targets is flagged as the default box
    /// (so it cannot be deleted).
    /// </summary>
    private void EnsureDefaultRuleAndBox()
    {
        var id = DefaultBoxId();
        _rules.EnsureDefaultRule(id);

        if (id is { } boxId)
        {
            foreach (var container in _containers.GetContainers())
            {
                if (container.ChildContainer is null)
                {
                    continue;
                }

                foreach (var box in container.ChildContainer.Boxes)
                {
                    if (box.Id == boxId)
                    {
                        box.IsDefault = true;
                    }
                }
            }
        }
    }

    /// <summary>
    /// Reconciles the persisted <see cref="BoxItem"/>s with the live desktop: items that no longer exist
    /// on the desktop are removed from every <see cref="BoxType.DesktopItems"/> box, and desktop items that
    /// aren't tracked in any box yet get a <see cref="BoxItem"/> created and routed (by rule, falling back to
    /// the default box). Keeps the snapshot truthful after files were created/deleted outside the app.
    /// </summary>
    private async Task ReconcileItemsAsync()
    {
        List<BoxItem> current;
        try
        {
            current = await EnumerateDesktopAsync();
        }
        catch
        {
            return;
        }

        // Drop entries whose underlying desktop item has disappeared. Path-backed items (files, folders,
        // shortcuts, virtual desktop items that resolve to a path) are matched by path or PIDL. Items that
        // carry only a PIDL (e.g. UWP apps dragged from the Start Menu, which are not desktop items) cannot
        // be verified against the desktop enumeration, so they are preserved.
        foreach (var container in _containers.GetContainers())
        {
            if (container.ChildContainer is null)
            {
                continue;
            }

            foreach (var box in container.ChildContainer.Boxes)
            {
                if (box.BoxType != BoxType.DesktopItems)
                {
                    continue;
                }

                foreach (var existing in box.Items.ToList())
                {
                    if (string.IsNullOrEmpty(existing.Path))
                    {
                        continue;
                    }

                    if (!current.Any(c => BoxItem.RefersToSame(c, existing)))
                    {
                        box.Items.Remove(existing);
                    }
                }
            }
        }

        // Create entries for desktop items that aren't represented in any box yet.
        var defaultId = DefaultBoxId();
        foreach (var item in current)
        {
            if (IsAlreadyTracked(item))
            {
                continue;
            }

            var targetId = !string.IsNullOrEmpty(item.Path)
                ? _rules.MatchTargetBoxId(Path.GetFileName(item.Path))
                : null;
            targetId ??= defaultId;
            if (targetId is null)
            {
                continue;
            }

            var box = _boxRegistry.GetBox(targetId.Value);
            if (box is null || box.BoxType != BoxType.DesktopItems)
            {
                continue;
            }

            box.Items.Add(item);
        }

        await SaveAsync();
    }

    private bool IsAlreadyTracked(BoxItem item)
    {
        foreach (var container in _containers.GetContainers())
        {
            if (container.ChildContainer is null)
            {
                continue;
            }

            foreach (var box in container.ChildContainer.Boxes)
            {
                if (box.Items.Any(i => BoxItem.RefersToSame(i, item)))
                {
                    return true;
                }
            }
        }

        return false;
    }


    private async Task BuildDefaultContainerAsync()
    {
        var items = new List<BoxItem>();
        try
        {
            for (int attempt = 0; attempt < 5 && items.Count == 0; attempt++)
            {
                if (attempt > 0)
                {
                    await Task.Delay(400);
                }

                items = await EnumerateDesktopAsync();
            }
        }
        catch
        {
            // Shell enumeration can fail on exotic sessions; fall back to an empty default container.
        }

        var box = new Box
        {
            Name = "Desktop",
            BoxType = BoxType.DesktopItems,
            IsDefault = true,
        };
        foreach (var item in items)
        {
            box.Items.Add(item);
        }

        var boxContainer = new BoxContainer
        {
            Boxes = { box },
            SelectedIndex = 0,
        };

        _containers.CreateContainer(DesktopItemContainerType.BoxContainer, 60, 60, 300, 460, childContainer: boxContainer);

        // FolderPortal for Downloads at top-right
        string downloadsPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");
        if (string.IsNullOrWhiteSpace(downloadsPath) || !Directory.Exists(downloadsPath))
        {
            try
            {
                var up = Environment.GetEnvironmentVariable("USERPROFILE");
                if (!string.IsNullOrWhiteSpace(up))
                {
                    var alt = Path.Combine(up, "Downloads");
                    if (Directory.Exists(alt)) downloadsPath = alt;
                }
            }
            catch { }
        }
        if (!string.IsNullOrWhiteSpace(downloadsPath))
        {
            string dlName = Path.GetFileName(downloadsPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
            if (string.IsNullOrEmpty(dlName)) dlName = "Downloads";
            var dlBox = new Box
            {
                Name = dlName,
                BoxType = BoxType.FolderPortal,
                FolderPath = downloadsPath,
                FolderPortalViewMode = FolderPortalViewMode.Details,
                FolderSortBy = FolderSortMode.DateModified,
                FolderSortAscending = false,
            };
            var dlContainer = new BoxContainer
            {
                Boxes = { dlBox },
                SelectedIndex = 0,
            };
            var wa = GetPrimaryWorkAreaDip();
            double dlW = 380;
            double dlH = 420;
            double dlLeft = wa.Right - dlW - 20;
            double dlTop = wa.Y + 20;
            // Clamp in case work area is smaller than assumed
            dlLeft = Math.Max(wa.X, Math.Min(dlLeft, wa.Right - dlW));
            dlTop = Math.Max(wa.Y, Math.Min(dlTop, wa.Bottom - dlH));
            _containers.CreateContainer(DesktopItemContainerType.BoxContainer, dlLeft, dlTop, dlW, dlH, childContainer: dlContainer);
        }
    }

    #endregion

    #region Window Management

    private void Containers_CollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.Action == NotifyCollectionChangedAction.Reset)
        {
            CloseAll();
            return;
        }

        if (e.NewItems != null)
        {
            foreach (ContainerViewModel vm in e.NewItems)
            {
                AddWindow(vm, showActivated: true);
            }
        }

        if (e.OldItems != null)
        {
            foreach (ContainerViewModel vm in e.OldItems)
            {
                RemoveWindow(vm.Id);
            }
        }
    }

    private void AddWindow(ContainerViewModel vm, bool showActivated = true)//, bool focusWorkaround = false
    {
        if (_windows.ContainsKey(vm.Id))
        {
            return;
        }

        Window window;
        if (vm.Type == DesktopItemContainerType.CssWidget)
        {
            window = new CssWidgetWindow(vm)
            {
                ShowActivated = showActivated
            };
        }
        else
        {
            window = new BoxContainerWindow(vm, _mainVm, _positioning, SaveAsyncFireAndForget)
            {
                ShowActivated = showActivated
            };
        }

        _windows[vm.Id] = window;
        // RegisterBoxWindow is done inside each window's OnLoaded for CssWidget; keep for BoxContainer compat
        try { Win32Apis.RegisterBoxWindow(new WindowInteropHelper(window).Handle); } catch { }
        //IntPtr? foregroundWindowHwnd = null;
        //if (!showActivated && focusWorkaround)
        //{
        //    foregroundWindowHwnd = ManualApis.GetForegroundWindow();
        //}
        window.Show();

        /*if (!showActivated && focusWorkaround && foregroundWindowHwnd is not null)
        {
            window.ContentRendered += (_, _) =>
            {
                //Win32Apis.NudgeCursor();
                NudgeWindowActivation(window, foregroundWindowHwnd.Value);
            };
            //window.Focus();
        }*/
    }

    /*public void NudgeWindowActivation(Window window, IntPtr prevForegroundWindow)
    {
        var widgetHwnd = new WindowInteropHelper(window).Handle;
        //var previousHwnd = ManualApis.GetForegroundWindow();

        //Debug.WriteLine($"Previous: 0x{previousHwnd.ToInt64():X}");

        ManualApis.SetForegroundWindow(widgetHwnd);
        //window.Activate();

        //Debug.WriteLine(
        //    $"Widget active: {ManualApis.GetForegroundWindow() == widgetHwnd}");

        if (prevForegroundWindow != IntPtr.Zero &&
            prevForegroundWindow != widgetHwnd)
        {
            //Win32.NativeMethods.Win32Apis.GlueToDesktop(widgetHwnd, Win32Apis.DesktopSurfaceHandle);
            ManualApis.SetActiveWindow(prevForegroundWindow);
            ManualApis.SetForegroundWindow(prevForegroundWindow);
        }
    }*/

    /// <summary>True when every open <see cref="BoxContainerWindow"/> is currently hidden via
    /// <see cref="HideAllBoxes"/>. Session-only (not persisted across restarts).</summary>
    public bool AllBoxesHidden => _allBoxesHidden;

    /// <summary>Raised whenever the all-boxes-hidden state changes (argument is the new state), so the
    /// tray menu check box stays in sync no matter which trigger performed the toggle.</summary>
    public event EventHandler<bool>? AllBoxesHiddenChanged;

    /// <summary>Toggles between hiding and showing all open box windows.</summary>
    public void ToggleHideAllBoxes()
    {
        if (_allBoxesHidden)
        {
            ShowAllBoxes();
        }
        else
        {
            HideAllBoxes();
        }
    }

    /// <summary>
    /// Temporarily hides every open box window. The <see cref="Win32Apis.MinimizePreventionHook"/> would
    /// otherwise re-show them, so each hide is wrapped in <see cref="Win32Apis.AllowHide"/>. The desktop
    /// surface and tray host are deliberately left visible so the toggle can be reversed.
    /// </summary>
    public void HideAllBoxes()
    {
        foreach (var window in _windows.Values)
        {
            var hwnd = new WindowInteropHelper(window).Handle;
            Win32Apis.AllowHide(hwnd);
            window.Hide();
            Win32Apis.DisallowHide(hwnd);
        }

        _allBoxesHidden = true;
        AllBoxesHiddenChanged?.Invoke(this, true);
    }

    /// <summary>Re-shows every box window previously hidden by <see cref="HideAllBoxes"/>.</summary>
    public void ShowAllBoxes()
    {
        foreach (var window in _windows.Values)
        {
            window.Show();
        }

        _allBoxesHidden = false;
        AllBoxesHiddenChanged?.Invoke(this, false);
    }

    /// <summary>
    /// Shows every container window, yielding to the dispatcher between each so the UI thread stays
    /// responsive while a potentially large number of <see cref="BoxContainerWindow"/>s is constructed.
    /// </summary>
    private async Task AddWindowsAsync()
    {
        foreach (var vm in _mainVm.Containers)
        {
            AddWindow(vm, showActivated: false);
            await Task.Yield();
        }
    }

    /// <summary>
    /// Lazily creates the remaining container windows in the background (the caller shows the first one
    /// synchronously). Creation is spread across dispatcher turns so the UI stays responsive and the splash
    /// can close — the containers stream in after startup instead of blocking it.
    /// </summary>
    private async Task StreamRemainingContainersAsync()
    {
        try
        {
            // Let the first frame render (and the splash close) before continuing.
            await Task.Yield();

            var rest = _mainVm.Containers.Skip(1).ToList();
            foreach (var vm in rest)
            {
                AddWindow(vm, showActivated: false);
                await Task.Yield();
            }
            //await Task.Delay(TimeSpan.FromSeconds(1));
            //_surface!.Focus();
            //_surface!.Activate();
        }
        catch
        {
            // Best-effort background streaming; a window failure must never break startup.
        }
    }

    private void RemoveWindow(System.Guid id)
    {
        if (_windows.TryGetValue(id, out var window))
        {
            var handle = new WindowInteropHelper(window).Handle;
            Win32Apis.UnregisterBoxWindow(handle);
            //Win32Apis.AllowHide(handle);
            //window.Close();
            window.CloseWindowEx(handle);
            _windows.Remove(id);
        }
    }
    #endregion


    #region Boxes

    public async Task ResetAsync()
    {
        CloseAll();
        //
        foreach (var container in _containers.GetContainers().ToList())
        {
            _containers.RemoveContainer(container.Id);
        }
        _persistence.DeleteSnapshotFile();

        _boxRegistry.Clear();

        await InitializeAsync();
        //await BuildDefaultContainerAsync();
        //RegisterAllBoxes();
        //EnsureSurface();

        //_mainVm.Containers.CollectionChanged -= Containers_CollectionChanged;
        //var builtVms = await Task.Run(() => _mainVm.BuildContainerViewModels(_containers.GetContainers()));
        //foreach (var vm in builtVms)
        //{
        //    _mainVm.Containers.Add(vm);
        //}

        //_mainVm.Containers.CollectionChanged += Containers_CollectionChanged;

        //await AddWindowsAsync();
        //await SaveAsync();
    }

    public void CloseAll()
    {
        _coordinator.Stop();
        SystemEvents.DisplaySettingsChanged -= OnDisplaySettingsChanged;

        foreach (var window in _windows)
        {
            //Win32Apis.AllowHide(new WindowInteropHelper(window).Handle);
            //window.Close();
            RemoveWindow(window.Key);
        }

        _windows.Clear();
        if (_surface is not null)
        {
            //Win32Apis.AllowHide(new WindowInteropHelper(_surface).Handle);
            _surface?.CloseWindowEx();
            _surface = null;
        }

        // Clear the published handle so nothing can target a dead window.
        Win32Apis.DesktopSurfaceHandle = IntPtr.Zero;
    }

    /// <summary>Registers every box from every container into <see cref="IBoxService"/> so the rule
    /// coordinator can resolve a box by id (snapshot-loaded boxes are not created via the service).</summary>
    private void RegisterAllBoxes()
    {
        foreach (var container in _containers.GetContainers())
        {
            if (container.ChildContainer is null)
            {
                continue;
            }

            foreach (var box in container.ChildContainer.Boxes)
            {
                _boxRegistry.AddBox(box);
            }
        }
    }


    public void NewBox()
    {
        _mainVm.CreateBox();
        _ = SaveAsync();
    }

    public void NewFolderPortal()
    {
        var offset = _mainVm.Containers.Count * 24;
        _mainVm.CreateFolderPortalAt(60 + offset, 60 + offset);
        _ = SaveAsync();
    }

    public void NewBoxContainer()
    {
        var offset = _mainVm.Containers.Count * 24;
        _mainVm.CreateBoxContainerAt(60 + offset, 60 + offset);
        _ = SaveAsync();
    }

    public void NewCssWidget(string slug, CssWidgetSource source)
    {
        var offset = _mainVm.Containers.Count * 24;
        // Use manifest default size if available
        var svc = App.Services.GetRequiredService<ICssWidgetService>();
        var info = svc.TryGetWidget(slug, source);
        double w = info?.Manifest.Width ?? 300;
        double h = info?.Manifest.Height ?? 220;
        _mainVm.CreateCssWidgetAt(slug, source, 60 + offset, 60 + offset, w, h);
        _ = SaveAsync();
    }

    public void RegisterContainer(DesktopItemContainer container)
    {
        _containers.AddContainer(container);
        var vm = new ContainerViewModel(container, App.Services.GetRequiredService<IconImageService>(), App.Services.GetRequiredService<IBoxService>());
        _mainVm.Containers.Add(vm);
        _ = SaveAsync();
    }

    public void RemoveContainer(Guid id)
    {
        var vm = _mainVm.Containers.FirstOrDefault(c => c.Id == id);
        if (vm is not null) _mainVm.RemoveContainer(vm);
        else _containers.RemoveContainer(id);
        _ = SaveAsync();
    }

    public DesktopSnapshot GetCurrentDesktopSnapshot()
    {
        var current = GetPrimaryWorkAreaDip();
        return new DesktopSnapshot
        {
            DesktopResolution = new SizeD(current.Width, current.Height),
            Containers = _containers.GetContainers().ToList(),
            Rules = _rules.GetRules().ToList(),
        };
    }

    #endregion

    #region Snapshot Save

    /// <summary>
    /// Sync save for app exit and other sync only operations. (No Task)
    /// </summary>
    public void SaveSync()
    {
        _persistence.SaveSnapshot(GetCurrentDesktopSnapshot());
    }
    public async Task SaveAsync()
    {
        await _persistence.SaveSnapshotAsync(GetCurrentDesktopSnapshot());
    }

    /// <summary>
    /// SaveAsync FireAndForget
    /// </summary>
    public void SaveAsyncFireAndForget() => _ = SaveAsync();

    /// <summary>
    /// Copies the current live snapshot file to <paramref name="destinationPath"/> (the "Backup" action
    /// in Settings). The live state is flushed first so the backup is up to date.
    /// </summary>
    public async Task BackupAsync(string destinationPath)
    {
        await SaveAsync();
        var src = _persistence.SnapshotFilePath;
        if (File.Exists(src))
        {
            var dir = Path.GetDirectoryName(destinationPath);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir)) Directory.CreateDirectory(dir);
            File.Copy(src, destinationPath, overwrite: true);
        }
    }

    /// <summary>
    /// Loads a snapshot from <paramref name="sourcePath"/>, replaces the live snapshot with it, and
    /// re-initializes the boxes (the "Restore" action in Settings).
    /// </summary>
    public async Task RestoreAsync(string sourcePath)
    {
        var snapshot = await _persistence.LoadFromFileAsync(sourcePath);
        if (snapshot is null)
        {
            return;
        }
        //
        CloseAll();
        foreach (var container in _containers.GetContainers().ToList())
        {
            _containers.RemoveContainer(container.Id);
        }
        _boxRegistry.Clear();

        //
        await _persistence.SaveSnapshotAsync(snapshot);
        await InitializeAsync();
    }

    #endregion

    #region Surface

    private void EnsureSurface()
    {
        if (GlobalFeaturesSwitches.UseGlobalMouseHookInsteadOfCustomSurface == false)
        {
            _surface ??= new DesktopSurface(_mainVm, SaveAsyncFireAndForget);
            if (!_surface.IsVisible)
            {
                _surface.Show();
            }
        }
        else
        {
            if (_surface is not null)
            {
                // Boxes may be OWNED by the surface (GlueToDesktop with the published handle), and
                // destroying an owner destroys its owned windows. Re-glue every box back to Progman
                // and unpublish the handle BEFORE closing the surface.
                Win32Apis.DesktopSurfaceHandle = IntPtr.Zero;
                foreach (var window in _windows.Values)
                {
                    var hwnd = new WindowInteropHelper(window).Handle;
                    if (hwnd != IntPtr.Zero)
                    {
                        Win32Apis.GlueToDesktop(hwnd);
                    }
                }

                _surface.CloseWindowEx();
                _surface = null;
            }
        }
    }

    /// <summary>
    /// Rebuilds the desktop-layer bindings after Explorer crashed/restarted: the old Progman/WorkerW
    /// windows (and every handle we cached against them) are gone, so the surface must be recreated
    /// or re-glued above the NEW anchor and every box re-owned. Triggered from App's
    /// "TaskbarCreated" broadcast listener.
    /// </summary>
    public void RecoverAfterShellRestart()
    {
        Serilog.Log.Information("Explorer restarted — recovering desktop layer");

        var customSurface = GlobalFeaturesSwitches.UseGlobalMouseHookInsteadOfCustomSurface == false;

        // Surface: it may have died together with its old owner window — recreate when needed,
        // otherwise just re-glue (owner + NOACTIVATE + insert-above-anchor) and refresh caches.
        if (customSurface)
        {
            if (_surface is not null)
            {
                var old = new WindowInteropHelper(_surface).Handle;
                if (old == IntPtr.Zero || !Win32Apis.IsWindow(old))
                {
                    Serilog.Log.Information("Desktop surface died with Explorer — recreating");
                    _surface = null;
                }
            }

            _surface ??= new DesktopSurface(_mainVm, SaveAsyncFireAndForget);
            if (!_surface.IsVisible)
            {
                _surface.Show();
            }

            var sHwnd = new WindowInteropHelper(_surface).Handle;
            _surface.InvalidateShellHandles();
            if (sHwnd != IntPtr.Zero && Win32Apis.IsWindow(sHwnd))
            {
                Win32Apis.GlueToDesktopSurface(sHwnd);
                _surface.Relayout();
            }
        }
        else
        {
            Win32Apis.DesktopSurfaceHandle = IntPtr.Zero;
        }

        // Boxes & Widgets: their owner handle pointed at the dead explorer/surface window — re-own them all
        // (to the surface in custom-surface mode, Progman otherwise) so owned-above-owner holds again.
        IntPtr owner = customSurface ? Win32Apis.DesktopSurfaceHandle : IntPtr.Zero;
        foreach (var window in _windows.Values)
        {
            var hwnd = new WindowInteropHelper(window).Handle;
            if (hwnd != IntPtr.Zero && Win32Apis.IsWindow(hwnd))
            {
                Win32Apis.GlueToDesktop(hwnd, owner);
            }
        }
    }
    #endregion

    #region Desktop Manage

    /// <summary>
    /// Show Desktop icons
    /// </summary>
    public void RestoreIcons() => _explorer.SetDesktopIconsVisible(true);

    #endregion
}
public static class DesktopManagerExtensions
{
    public static void CloseWindowEx(this Window window, nint? handle = null)
    {
        if (handle is null) handle = new WindowInteropHelper(window).Handle;
        window.Close();
    }

}