using DesktopBoxesUI.Core.Interfaces;
using DesktopBoxesUI.Core.Models;
using DesktopBoxesUI.ViewModels;
using System.Collections.ObjectModel;
using System.Linq;
using System.Windows.Input;

namespace DesktopBoxesUI.ViewModels;

/// <summary>
/// Application-level view-model. Holds the collection of desktop <see cref="ContainerViewModel"/>s and
/// the "New Box" / "New Box Container" commands. The persistence / Explorer-icon orchestration lives
/// in <see cref="DesktopManager"/>; this view-model only models the containers the user sees.
/// </summary>
public sealed class MainViewModel : ViewModelBase
{
    private readonly IContainerService _containers;
    private readonly IBoxService _boxService;
    private readonly IconImageService _icons;
    private readonly IFileRuleCoordinator _coordinator;

    public MainViewModel(IContainerService containers, IBoxService boxService, IconImageService icons, IFileRuleCoordinator coordinator)
    {
        _containers = containers;
        _boxService = boxService;
        _icons = icons;
        _coordinator = coordinator;
        CreateBoxCommand = new RelayCommand(_ => CreateBox());
        CreateBoxContainerCommand = new RelayCommand(_ => CreateBoxContainer());
    }

    public ObservableCollection<ContainerViewModel> Containers { get; } = new();

    public ICommand CreateBoxCommand { get; }

    public ICommand CreateBoxContainerCommand { get; }

    public void LoadFromContainers(System.Collections.Generic.IEnumerable<DesktopItemContainer> containers)
    {
        Containers.Clear();
        foreach (var container in containers)
        {
            Containers.Add(new ContainerViewModel(container, _icons, _boxService));
        }
    }

    /// <summary>
    /// Constructs the <see cref="ContainerViewModel"/> tree (including every <see cref="BoxItemViewModel"/>
    /// and its async icon-load kick-off) without adding anything to <see cref="Containers"/>. Safe to call
    /// on a background thread so the caller can keep the UI responsive, then add the built VMs on the UI
    /// thread.
    /// </summary>
    public System.Collections.Generic.List<ContainerViewModel> BuildContainerViewModels(
        System.Collections.Generic.IEnumerable<DesktopItemContainer> containers)
    {
        var list = new System.Collections.Generic.List<ContainerViewModel>();
        foreach (var container in containers)
        {
            list.Add(new ContainerViewModel(container, _icons, _boxService));
        }

        return list;
    }

    /// <summary>Creates a BoxContainer with a single box (no tab strip).</summary>
    public ContainerViewModel CreateBox(double left = 60, double top = 60)
    {
        var offset = Containers.Count * 24;
        return CreateBoxAt(left + offset, top + offset);
    }

    /// <summary>Creates a BoxContainer (single box) at an exact position (used when dropping onto empty desktop).</summary>
    public ContainerViewModel CreateBoxAt(double left, double top)
    {
        var box = _boxService.CreateBox("New Box", left, top, 240, 200);
        return RegisterContainer(new BoxContainer { Boxes = { box }, SelectedIndex = 0 }, left, top);
    }

    /// <summary>Creates a BoxContainer pre-seeded with two tabs (so the tab strip is visible).</summary>
    public ContainerViewModel CreateBoxContainerAt(double left, double top)
    {
        var tab1 = _boxService.CreateBox("Tab 1", left, top, 240, 200);
        var tab2 = _boxService.CreateBox("Tab 2", left, top, 240, 200);
        return RegisterContainer(new BoxContainer { Boxes = { tab1, tab2 }, SelectedIndex = 0 }, left, top);
    }

    public ContainerViewModel CreateBoxContainer(double left = 60, double top = 60)
    {
        var offset = Containers.Count * 24;
        return CreateBoxContainerAt(left + offset, top + offset);
    }

    private ContainerViewModel RegisterContainer(BoxContainer boxContainer, double left, double top)
    {
        var container = _containers.CreateContainer(DesktopItemContainerType.BoxContainer, left, top, 240, 200, childContainer: boxContainer);
        var vm = MakeVm(container);
        Containers.Add(vm);
        return vm;
    }

    private ContainerViewModel MakeVm(DesktopItemContainer container) =>
        new(container, _icons, _boxService);

    /// <summary>Removes a container from both the UI collection and the backing store.</summary>
    public void RemoveContainer(ContainerViewModel vm)
    {
        _containers.RemoveContainer(vm.Id);
        Containers.Remove(vm);
    }

    /// <summary>Finds the box (across all containers and tabs) that contains the given item.</summary>
    public BoxViewModel? FindBoxContaining(BoxItemViewModel item)
    {
        foreach (var container in Containers)
        {
            if (container.BoxContainerVm != null)
            {
                foreach (var tab in container.BoxContainerVm.Tabs)
                {
                    if (tab.Items.Contains(item))
                    {
                        return tab;
                    }
                }
            }
        }

        return null;
    }

    /// <summary>Moves a single box from <paramref name="source"/> into <paramref name="target"/>.</summary>
    public void MoveBoxToContainer(Box box, ContainerViewModel source, ContainerViewModel target, int index = -1)
    {
        if (source == target || source.BoxContainerVm == null || target.BoxContainerVm == null)
        {
            return;
        }

        var sourceTabs = source.BoxContainerVm.Tabs;
        int idx = sourceTabs.IndexOf(sourceTabs.FirstOrDefault(t => t.Model == box)!);
        if (idx < 0)
        {
            return;
        }

        source.BoxContainerVm.RemoveTab(idx, allowDefault: true);
        target.BoxContainerVm.InsertBox(box, index);

        // Make the moved box the active tab in its new container.
        var tvm = target.BoxContainerVm;
        int active = index >= 0 && index < tvm.Tabs.Count ? index : tvm.Tabs.Count - 1;
        tvm.SelectedIndex = System.Math.Clamp(active, 0, tvm.Tabs.Count - 1);

        if (source.BoxContainerVm.Tabs.Count == 0)
        {
            RemoveContainer(source);
        }
    }

    /// <summary>Moves a box out of <paramref name="source"/> and into a brand-new container created at the drop point.</summary>
    public void MoveBoxToNewContainer(Box box, ContainerViewModel? source, double left, double top)
    {
        if (source == null || source.BoxContainerVm == null)
        {
            return;
        }

        var sourceVm = source.BoxContainerVm;
        var sourceTabs = sourceVm.Tabs;
        int idx = sourceTabs.IndexOf(sourceTabs.FirstOrDefault(t => t.Model == box)!);
        if (idx < 0)
        {
            return;
        }

        sourceVm.RemoveTab(idx, allowDefault: true);

        var childContainer = new BoxContainer { Boxes = { box }, SelectedIndex = 0 };
        var container = _containers.CreateContainer(DesktopItemContainerType.BoxContainer, left, top, 240, 200, childContainer: childContainer);
        var vm = new ContainerViewModel(container, _icons, _boxService);
        Containers.Add(vm);

        if (sourceVm.Tabs.Count == 0)
        {
            RemoveContainer(source);
        }
    }

    /// <summary>Merges every box from <paramref name="source"/> into <paramref name="target"/>, then removes the source.</summary>
    public void MergeContainers(ContainerViewModel source, ContainerViewModel target)
    {
        if (source == target || source.BoxContainerVm == null || target.BoxContainerVm == null)
        {
            return;
        }

        var models = source.BoxContainerVm.Tabs.Select(t => t.Model).ToList();
        foreach (var model in models)
        {
            var sourceTabs = source.BoxContainerVm.Tabs;
            int idx = sourceTabs.IndexOf(sourceTabs.First(t => t.Model == model));
            if (idx >= 0)
            {
                source.BoxContainerVm.RemoveTab(idx, allowDefault: true);
            }

            target.BoxContainerVm.InsertBox(model);
        }

        RemoveContainer(source);
    }

    /// <summary>Renames a tracked item. Updates the view-model (display name + icon) on success.</summary>
    public async Task<bool> RenameItem(BoxItemViewModel vm, string newName)
    {
        if (vm is null)
        {
            return false;
        }

        bool ok = await _coordinator.RenameItemAsync(vm.Model, newName);
        if (ok)
        {
            vm.DisplayName = vm.Model.DisplayName;
            vm.ReloadIcon();
        }

        return ok;
    }

    /// <summary>Deletes a tracked item (Recycle Bin unless <paramref name="permanent"/>). The view-model is
    /// removed from its box by the coordinator's model update.</summary>
    public Task<bool> DeleteItem(BoxItemViewModel vm, bool permanent)
    {
        if (vm is null)
        {
            return Task.FromResult(false);
        }

        return _coordinator.DeleteItemAsync(vm.Model, permanent);
    }
}
