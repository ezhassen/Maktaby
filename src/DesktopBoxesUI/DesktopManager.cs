using DesktopBoxesUI.Core.Interfaces;
using DesktopBoxesUI.Core.Models;
using DesktopBoxesUI.Core.Services;
using DesktopBoxesUI.ViewModels;
using DesktopBoxesUI.Views;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Win32;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.IO;
using System.Linq;
using System.Runtime.Versioning;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Interop;
using DesktopBoxesUI.Win32.NativeMethods;

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
    private readonly MainViewModel _mainVm;
    private readonly IContainerService _containers;
    private readonly IPersistenceService _persistence;
    private readonly IDesktopService _desktop;
    private readonly IWindowPositioningService _positioning;
    private readonly IExplorerDesktopService _explorer;
    private readonly IRuleService _rules;
    private readonly IBoxService _boxRegistry;
    private readonly IFileRuleCoordinator _coordinator;

    private readonly Dictionary<System.Guid, Window> _windows = new();
    private DesktopSurface? _surface;
    private bool _allBoxesHidden;

    /// <summary>The work-area resolution the current container layout was computed against. When the
    /// display settings change we rescale every container proportionally against this baseline.</summary>
    private RectD _appliedResolution;

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
    }

    public async Task InitializeAsync()
    {
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
            AddWindow(_mainVm.Containers[0]);
        }

        _ = StreamRemainingContainersAsync();

        await ReconcileItemsAsync();

        _appliedResolution = GetPrimaryWorkAreaDip();
        SystemEvents.DisplaySettingsChanged += OnDisplaySettingsChanged;


        _coordinator.Start();
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
        }

        _surface?.Relayout();

        _ = SaveAsync();
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
    }

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
                AddWindow(vm);
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

    private void AddWindow(ContainerViewModel vm)
    {
        if (_windows.ContainsKey(vm.Id))
        {
            return;
        }

        Window window = new BoxContainerWindow(vm, _mainVm, _positioning, SaveAsyncFireAndForget);

        _windows[vm.Id] = window;
        window.Show();
    }

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
        foreach (var window in Application.Current.Windows.OfType<BoxContainerWindow>())
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
        foreach (var window in Application.Current.Windows.OfType<BoxContainerWindow>())
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
            AddWindow(vm);
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
                AddWindow(vm);
                await Task.Yield();
            }
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
            window.Close();
            _windows.Remove(id);
        }
    }

    public async Task ResetAsync()
    {
        CloseAll();
        foreach (var container in _containers.GetContainers().ToList())
        {
            _containers.RemoveContainer(container.Id);
        }

        _boxRegistry.Clear();
        await BuildDefaultContainerAsync();
        RegisterAllBoxes();
        EnsureSurface();

        _mainVm.Containers.CollectionChanged -= Containers_CollectionChanged;
        var builtVms = await Task.Run(() => _mainVm.BuildContainerViewModels(_containers.GetContainers()));
        foreach (var vm in builtVms)
        {
            _mainVm.Containers.Add(vm);
        }

        _mainVm.Containers.CollectionChanged += Containers_CollectionChanged;

        await AddWindowsAsync();
        await SaveAsync();
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

    private void EnsureSurface()
    {
        _surface ??= new DesktopSurface(_mainVm, SaveAsyncFireAndForget);
        _surface.DesktopDoubleClickAction = ToggleHideAllBoxes;
        if (!_surface.IsVisible)
        {
            _surface.Show();
        }
    }

    public void NewBox()
    {
        _mainVm.CreateBox();
        _ = SaveAsync();
    }

    public void NewBoxContainer()
    {
        var offset = _mainVm.Containers.Count * 24;
        _mainVm.CreateBoxContainerAt(60 + offset, 60 + offset);
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

    public void CloseAll()
    {
        _coordinator.Stop();
        SystemEvents.DisplaySettingsChanged -= OnDisplaySettingsChanged;

        foreach (var window in _windows.Values)
        {
            window.Close();
        }

        _windows.Clear();
        _surface?.Close();
        _surface = null;
    }

    public void RestoreIcons() => _explorer.SetDesktopIconsVisible(true);
}
