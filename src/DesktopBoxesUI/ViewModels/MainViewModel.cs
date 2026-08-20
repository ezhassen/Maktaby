using System.Collections.ObjectModel;
using System.Linq;
using System.Windows.Input;
using DesktopBoxesUI.Core.Interfaces;
using DesktopBoxesUI.Core.Models;
using DesktopBoxesUI.ViewModels;

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

    public MainViewModel(IContainerService containers, IBoxService boxService, IconImageService icons)
    {
        _containers = containers;
        _boxService = boxService;
        _icons = icons;
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
}
