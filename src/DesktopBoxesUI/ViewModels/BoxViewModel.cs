using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.Linq;
using DesktopBoxesUI.Core.Models;
using DesktopBoxesUI.Core.Services;

namespace DesktopBoxesUI.ViewModels;

/// <summary>
/// View-model for a single <see cref="Box"/> (one tab inside a <see cref="BoxContainer"/>). Geometry and
/// styling live on the owning <see cref="BoxContainerViewModel"/>; this only models the box's items
/// and behaviour. The <see cref="Items"/> collection mirrors the underlying <see cref="Box.Items"/>
/// model collection (which the file-rule coordinator updates), so a single source of truth is kept.
/// </summary>
public sealed class BoxViewModel : ViewModelBase
{
    private readonly Box _box;
    private readonly IconImageService _icons;

    public BoxViewModel(Box box, IconImageService icons)
    {
        _box = box;
        _icons = icons;
        Items = new ObservableCollection<BoxItemViewModel>(
            box.Items.Where(i => !ShellItemFilter.IsExcluded(i.Path)).Select(i => new BoxItemViewModel(i, icons)));
        _box.Items.CollectionChanged += OnBoxItemsChanged;
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

    /// <summary>True for the built-in default box (fed by the default rule; cannot be deleted).</summary>
    public bool IsDefault => _box.IsDefault;

    public ObservableCollection<BoxItemViewModel> Items { get; }

    /// <summary>Adds an item to the underlying model; the <see cref="Items"/> view collection mirrors it.</summary>
    public void AddItem(BoxItem item) => _box.Items.Add(item);

    /// <summary>Inserts an item at <paramref name="index"/> in the underlying model (used for drag-reordering).</summary>
    public void InsertItem(BoxItem item, int index)
    {
        int count = _box.Items.Count;
        if (index < 0)
        {
            index = 0;
        }

        if (index > count)
        {
            index = count;
        }

        _box.Items.Insert(index, item);
    }

    /// <summary>Removes an item from the underlying model; the <see cref="Items"/> view collection mirrors it.</summary>
    public void RemoveItem(BoxItem item) => _box.Items.Remove(item);

    private void OnBoxItemsChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.NewItems != null)
        {
            for (int k = 0; k < e.NewItems.Count; k++)
            {
                if (e.NewItems[k] is BoxItem item && !ShellItemFilter.IsExcluded(item.Path))
                {
                    int at = e.NewStartingIndex + k;
                    if (at >= 0 && at <= Items.Count)
                    {
                        Items.Insert(at, new BoxItemViewModel(item, _icons));
                    }
                    else
                    {
                        Items.Add(new BoxItemViewModel(item, _icons));
                    }
                }
            }
        }

        if (e.OldItems != null)
        {
            foreach (BoxItem item in e.OldItems)
            {
                var existing = Items.FirstOrDefault(vm => vm.Model == item);
                if (existing != null)
                {
                    Items.Remove(existing);
                }
            }
        }
    }
}
