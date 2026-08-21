using System.Collections.ObjectModel;
using System.Linq;
using DesktopBoxesUI.Core.Models;

namespace DesktopBoxesUI.ViewModels;

/// <summary>
/// View-model for a single <see cref="Box"/> (one tab inside a <see cref="BoxContainer"/>). Geometry and
/// styling live on the owning <see cref="BoxContainerViewModel"/>; this only models the box's items
/// and behaviour.
/// </summary>
public sealed class BoxViewModel : ViewModelBase
{
    private readonly Box _box;
    private readonly IconImageService _icons;

    public BoxViewModel(Box box, IconImageService icons)
    {
        _box = box;
        _icons = icons;
        Items = new ObservableCollection<BoxItemViewModel>(box.Items.Select(i => new BoxItemViewModel(i, icons)));
    }

    public System.Guid Id => _box.Id;

    /// <summary>The underlying <see cref="Box"/> model (used when moving a box between containers).</summary>
    public Box Model => _box;

    public string Name
    {
        get => _box.Name;
        set
        {
            if (_box.Name != value)
            {
                _box.Name = value;
                OnPropertyChanged();
            }
        }
    }

    public BoxType BoxType
    {
        get => _box.BoxType;
        set
        {
            if (_box.BoxType != value)
            {
                _box.BoxType = value;
                OnPropertyChanged();
            }
        }
    }

    public ObservableCollection<BoxItemViewModel> Items { get; }

    public void AddItem(BoxItem item)
    {
        _box.Items.Add(item);
        Items.Add(new BoxItemViewModel(item, _icons));
    }

    public void RemoveItem(BoxItem item)
    {
        _box.Items.Remove(item);
        var existing = Items.FirstOrDefault(vm => vm.Model == item);
        if (existing != null)
        {
            Items.Remove(existing);
        }
    }
}
