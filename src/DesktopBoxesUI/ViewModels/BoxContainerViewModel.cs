using System.Collections.ObjectModel;
using System.Linq;
using DesktopBoxesUI.Core.Interfaces;
using DesktopBoxesUI.Core.Models;

namespace DesktopBoxesUI.ViewModels;

/// <summary>
/// View-model for the tabbed set of <see cref="Box"/>es inside a <see cref="BoxContainer"/>. Geometry and
/// styling live on the owning <see cref="DesktopItemContainer"/> (via <see cref="ContainerViewModel"/>);
/// this view-model only manages the tabs and the active box. The window's title bar and menu bind to
/// <see cref="ActiveBox"/> (the selected tab).
/// </summary>
public sealed class BoxContainerViewModel : ViewModelBase
{
    private readonly BoxContainer _model;
    private readonly IconImageService _icons;
    private readonly IBoxService _boxService;
    private int _selectedIndex;

    public BoxContainerViewModel(BoxContainer model, IconImageService icons, IBoxService boxService)
    {
        _model = model;
        _icons = icons;
        _boxService = boxService;
        Tabs = new ObservableCollection<BoxViewModel>(model.Boxes.Select(b => new BoxViewModel(b, icons)));
        _selectedIndex = model.Boxes.Count == 0 ? 0 : System.Math.Clamp(model.SelectedIndex, 0, model.Boxes.Count - 1);
    }

    public ObservableCollection<BoxViewModel> Tabs { get; }

    public int SelectedIndex
    {
        get => _selectedIndex;
        set
        {
            if (_selectedIndex == value)
            {
                return;
            }

            _selectedIndex = value;
            _model.SelectedIndex = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(SelectedBox));
            SelectedIndexChanged?.Invoke();
        }
    }

    /// <summary>The box shown by the active tab, or null when there are no tabs.</summary>
    public BoxViewModel? SelectedBox => Tabs.Count > 0 ? Tabs[System.Math.Min(_selectedIndex, Tabs.Count - 1)] : null;

    /// <summary>The box driving the body/title/menu (alias of <see cref="SelectedBox"/>).</summary>
    public BoxViewModel? ActiveBox => SelectedBox;

    /// <summary>True only when more than one tab exists (the strip is hidden for a single box).</summary>
    public bool ShowTabs => Tabs.Count > 1;

    public event System.Action? SelectedIndexChanged;

    /// <summary>Appends a new empty tab and selects it.</summary>
    public BoxViewModel AddTab(string name)
    {
        var box = _boxService.CreateBox(name, 0, 0, 0, 0);
        _model.Boxes.Add(box);
        var vm = new BoxViewModel(box, _icons);
        Tabs.Add(vm);
        SelectedIndex = Tabs.Count - 1;
        OnPropertyChanged(nameof(ShowTabs));
        return vm;
    }

    public void RemoveTab(int index, bool allowDefault = false)
    {
        if (index < 0 || index >= Tabs.Count)
        {
            return;
        }

        // The default box (fed by the default rule) cannot be deleted by the user.
        if (!allowDefault && Tabs[index].IsDefault)
        {
            return;
        }

        var doomed = _model.Boxes[index];
        // User-initiated delete: preserve DesktopItems by moving them into the default box.
        // Moves (allowDefault:true) keep the box intact, so no migration there.
        if (!allowDefault && doomed.BoxType == BoxType.DesktopItems && doomed.Items.Count > 0)
        {
            MoveDesktopItemsToDefault(doomed);
        }

        Tabs.RemoveAt(index);
        _model.Boxes.RemoveAt(index);

        // A deleted box must stop receiving auto-routed files. (Moves re-register via InsertBox.)
        _boxService.RemoveBox(doomed.Id);

        if (Tabs.Count == 0)
        {
            _selectedIndex = 0;
        }
        else
        {
            _selectedIndex = System.Math.Clamp(_selectedIndex, 0, Tabs.Count - 1);
        }

        OnPropertyChanged(nameof(SelectedIndex));
        OnPropertyChanged(nameof(SelectedBox));
        OnPropertyChanged(nameof(ShowTabs));
        SelectedIndexChanged?.Invoke();
    }

    /// <summary>Moves an existing <see cref="Box"/> (e.g. dragged from another container) into this container.</summary>
    public void InsertBox(Box box, int index = -1)
    {
        _boxService.AddBox(box);
        var vm = new BoxViewModel(box, _icons);
        if (index >= 0 && index < _model.Boxes.Count)
        {
            _model.Boxes.Insert(index, box);
            Tabs.Insert(index, vm);
        }
        else
        {
            _model.Boxes.Add(box);
            Tabs.Add(vm);
        }

        OnPropertyChanged(nameof(ShowTabs));
    }

    /// <summary>
    /// Reorders an existing tab from <paramref name="from"/> to the insertion index <paramref name="to"/>,
    /// where <paramref name="to"/> is expressed in the coordinate space that excludes the dragged tab
    /// (i.e. the position among the remaining tabs).
    /// </summary>
    public void MoveTab(int from, int to)
    {
        if (from < 0 || from >= Tabs.Count || to < 0 || to > Tabs.Count - 1)
        {
            return;
        }

        var vm = Tabs[from];
        var model = _model.Boxes[from];
        Tabs.RemoveAt(from);
        _model.Boxes.RemoveAt(from);

        int insert = System.Math.Clamp(to, 0, Tabs.Count);

        Tabs.Insert(insert, vm);
        _model.Boxes.Insert(insert, model);

        _selectedIndex = insert;
        OnPropertyChanged(nameof(SelectedIndex));
        OnPropertyChanged(nameof(SelectedBox));
        OnPropertyChanged(nameof(ShowTabs));
        SelectedIndexChanged?.Invoke();
    }

    /// <summary>
    /// Selects the tab at <paramref name="index"/>, always raising change notifications. Used after an
    /// insertion (e.g. a dropped tab) has shifted existing indices — the selected *box* can change even
    /// when the numeric index is unchanged, so we must notify regardless of the prior value.
    /// </summary>
    public void SelectIndex(int index)
    {
        int clamped = System.Math.Clamp(index, 0, Tabs.Count - 1);
        _selectedIndex = clamped;
        _model.SelectedIndex = clamped;
        OnPropertyChanged(nameof(SelectedIndex));
        OnPropertyChanged(nameof(SelectedBox));
        SelectedIndexChanged?.Invoke();
    }

    private void MoveDesktopItemsToDefault(Box doomed)
    {
        if (doomed.IsDefault || doomed.Items.Count == 0)
        {
            return;
        }

        var defaultBox = _boxService.GetBoxes().FirstOrDefault(b => b.IsDefault);
        if (defaultBox is null || defaultBox.Id == doomed.Id)
        {
            return;
        }

        // Preserve items by moving them into the default box. The default box's
        // BoxViewModel (if loaded) mirrors via CollectionChanged, so UI updates automatically.
        foreach (var item in doomed.Items.ToList())
        {
            defaultBox.Items.Add(item);
        }
    }
}
