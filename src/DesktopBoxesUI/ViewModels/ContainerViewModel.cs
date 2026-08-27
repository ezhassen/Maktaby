using DesktopBoxesUI.Core.Interfaces;
using DesktopBoxesUI.Core.Models;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Windows.Input;

namespace DesktopBoxesUI.ViewModels;

/// <summary>
/// View-model for a <see cref="DesktopItemContainer"/> — the single placed-on-desktop entity. It owns the
/// geometry and styling (bounds, lock, visibility, transparency) for every container type, and for a
/// <see cref="DesktopItemContainerType.BoxContainer"/> also exposes the wrapped <see cref="BoxContainerVm"/>
/// (the tabbed boxes). For a <see cref="DesktopItemContainerType.Custom"/> widget it exposes
/// <see cref="CustomTypeName"/>.
/// </summary>
public sealed class ContainerViewModel : ViewModelBase
{
    private readonly DesktopItemContainer _container;
    private readonly IconImageService _icons;
    private readonly IBoxService _boxService;
    private readonly BoxContainerViewModel? _boxContainerVm;

    public ContainerViewModel(DesktopItemContainer container, IconImageService icons, IBoxService boxService)
    {
        _container = container;
        _icons = icons;
        _boxService = boxService;

        if (container.Type == DesktopItemContainerType.BoxContainer && container.ChildContainer != null)
        {
            _boxContainerVm = new BoxContainerViewModel(container.ChildContainer, icons, boxService);
            _boxContainerVm.SelectedIndexChanged += () =>
            {
                OnPropertyChanged(nameof(ActiveBox));
                OnPropertyChanged(nameof(Title));
            };
            _boxContainerVm.Tabs.CollectionChanged += OnTabsChanged;
            foreach (var t in _boxContainerVm.Tabs)
                t.PropertyChanged += OnBoxPropertyChanged;
        }
    }

    private void OnTabsChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.NewItems != null)
            foreach (BoxViewModel vm in e.NewItems)
                vm.PropertyChanged += OnBoxPropertyChanged;
        if (e.OldItems != null)
            foreach (BoxViewModel vm in e.OldItems)
                vm.PropertyChanged -= OnBoxPropertyChanged;
        OnPropertyChanged(nameof(Title));
    }

    private void OnBoxPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(BoxViewModel.Name) && sender == ActiveBox)
            OnPropertyChanged(nameof(Title));
    }

    public System.Guid Id => _container.Id;

    public DesktopItemContainerType Type => _container.Type;

    public string? CustomTypeName => _container.CustomTypeName;

    /// <summary>Display title: the active box name for a BoxContainer, or the widget name for Custom.</summary>
    public string Title =>
        Type == DesktopItemContainerType.Custom
            ? (CustomTypeName ?? "Custom Widget")
            : (ActiveBox?.Name ?? "");

    public BoxContainerViewModel? BoxContainerVm => _boxContainerVm;

    /// <summary>Whether the container is rolled (collapsed to an edge). Only meaningful for a <see cref="DesktopItemContainerType.BoxContainer"/>.</summary>
    public bool IsRolled
    {
        get => _container.ChildContainer?.IsRolled ?? false;
        set
        {
            if (_container.ChildContainer == null || _container.ChildContainer.IsRolled == value)
            {
                return;
            }

            _container.ChildContainer.IsRolled = value;
            OnPropertyChanged();
        }
    }

    /// <summary>Edge the rolled TitleBar snaps to. <c>null</c> = auto (detect from snap position). Only meaningful for a <see cref="DesktopItemContainerType.BoxContainer"/>.</summary>
    public RollDirection? RollDirection
    {
        get => _container.ChildContainer?.RollDirection;
        set
        {
            if (_container.ChildContainer == null || _container.ChildContainer.RollDirection == value)
            {
                return;
            }

            _container.ChildContainer.RollDirection = value;
            OnPropertyChanged();
        }
    }

    // --- Geometry (always on the DesktopItemContainer) ---

    public double Left
    {
        get => _container.Bounds.X;
        set
        {
            if (_container.Bounds.X == value)
            {
                return;
            }

            _container.Bounds = _container.Bounds with { X = value };
            OnPropertyChanged();
            OnPropertyChanged(nameof(Bounds));
        }
    }

    public double Top
    {
        get => _container.Bounds.Y;
        set
        {
            if (_container.Bounds.Y == value)
            {
                return;
            }

            _container.Bounds = _container.Bounds with { Y = value };
            OnPropertyChanged();
            OnPropertyChanged(nameof(Bounds));
        }
    }

    public double Width
    {
        get => _container.Bounds.Width;
        set
        {
            if (_container.Bounds.Width == value)
            {
                return;
            }

            _container.Bounds = _container.Bounds with { Width = value };
            OnPropertyChanged();
            OnPropertyChanged(nameof(Bounds));
        }
    }

    public double Height
    {
        get => _container.Bounds.Height;
        set
        {
            if (_container.Bounds.Height == value)
            {
                return;
            }

            _container.Bounds = _container.Bounds with { Height = value };
            OnPropertyChanged();
            OnPropertyChanged(nameof(Bounds));
        }
    }

    public RectD Bounds => _container.Bounds;

    // --- Styling (always on the DesktopItemContainer) ---

    public bool IsLocked
    {
        get => _container.IsLocked;
        set
        {
            if (_container.IsLocked != value)
            {
                _container.IsLocked = value;
                OnPropertyChanged();
            }
        }
    }

    public bool IsVisible
    {
        get => _container.IsVisible;
        set
        {
            if (_container.IsVisible != value)
            {
                _container.IsVisible = value;
                OnPropertyChanged();
            }
        }
    }

    public double? Transparency
    {
        get => _container.Transparency;
        set
        {
            if (_container.Transparency != value)
            {
                _container.Transparency = value;
                OnPropertyChanged();
            }
        }
    }

    // --- Tabs (BoxContainer only) ---

    public int SelectedIndex => _boxContainerVm?.SelectedIndex ?? 0;

    /// <summary>The box driving the body/title/menu, or null when there is no tabbed box.</summary>
    public BoxViewModel? ActiveBox => _boxContainerVm?.ActiveBox;

    public ICommand AddTabCommand => new RelayCommand(_ =>
    {
        if (_boxContainerVm != null)
        {
            _boxContainerVm.AddTab($"Tab {_boxContainerVm.Tabs.Count + 1}");
        }
    });

    public ICommand RemoveTabCommand => new RelayCommand(_ =>
    {
        _boxContainerVm?.RemoveTab(_boxContainerVm.SelectedIndex);
    });
}
