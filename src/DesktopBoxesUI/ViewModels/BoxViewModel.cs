using System.Collections.ObjectModel;
using System.Linq;
using DesktopBoxesUI.Core.Models;

namespace DesktopBoxesUI.ViewModels;

/// <summary>
/// View-model for a <see cref="Box"/>. Exposes geometry (bound to a borderless Box window) and the
/// items it contains. Writes to the underlying model so layout can be persisted.
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

    public Guid Id => _box.Id;

    public ObservableCollection<BoxItemViewModel> Items { get; }

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

    public double Left
    {
        get => _box.Left;
        set
        {
            if (_box.Left != value)
            {
                _box.Left = value;
                OnPropertyChanged();
            }
        }
    }

    public double Top
    {
        get => _box.Top;
        set
        {
            if (_box.Top != value)
            {
                _box.Top = value;
                OnPropertyChanged();
            }
        }
    }

    public double Width
    {
        get => _box.Width;
        set
        {
            if (_box.Width != value)
            {
                _box.Width = value;
                OnPropertyChanged();
            }
        }
    }

    public double Height
    {
        get => _box.Height;
        set
        {
            if (_box.Height != value)
            {
                _box.Height = value;
                OnPropertyChanged();
            }
        }
    }

    /// <summary>
    /// Fill transparency (0 = opaque, 1 = transparent). Null falls back to the global default.
    /// </summary>
    public double? Transparency
    {
        get => _box.Transparency;
        set
        {
            if (_box.Transparency != value)
            {
                _box.Transparency = value;
                OnPropertyChanged();
            }
        }
    }

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
