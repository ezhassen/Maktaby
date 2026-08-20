using DesktopBoxesUI.Core.Interfaces;
using DesktopBoxesUI.Core.Models;
using DesktopBoxesUI.Core.Services;
using DesktopBoxesUI.ViewModels;
using DesktopBoxesUI.Views;
using Microsoft.Extensions.DependencyInjection;
using System.Windows;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.Linq;
using System.Runtime.Versioning;
using System.Threading.Tasks;

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
    private readonly IMonitorService _monitor;

    private readonly Dictionary<System.Guid, Window> _windows = new();
    private DesktopSurface? _surface;

    public DesktopManager(IServiceProvider provider)
    {
        _mainVm = provider.GetRequiredService<MainViewModel>();
        _containers = provider.GetRequiredService<IContainerService>();
        _persistence = provider.GetRequiredService<IPersistenceService>();
        _desktop = provider.GetRequiredService<IDesktopService>();
        _positioning = provider.GetRequiredService<IWindowPositioningService>();
        _explorer = provider.GetRequiredService<IExplorerDesktopService>();
        _monitor = provider.GetRequiredService<IMonitorService>();
    }

    public async Task InitializeAsync()
    {
        var snapshot = await _persistence.LoadSnapshotAsync();
        if (snapshot is not { Containers.Count: > 0 })
        {
            await BuildDefaultContainerAsync();
        }
        else
        {
            RescaleIfNeeded(snapshot);
            foreach (var container in snapshot.Containers)
            {
                _containers.AddContainer(container);
            }
        }

        _mainVm.Containers.CollectionChanged += Containers_CollectionChanged;
        _mainVm.LoadFromContainers(_containers.GetContainers());

        EnsureSurface();

        foreach (var vm in _mainVm.Containers)
        {
            AddWindow(vm);
        }

        await SaveAsync();
        _explorer.SetDesktopIconsVisible(false);
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

        var current = _monitor.GetPrimaryWorkArea();
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

                items.Clear();
                await foreach (var item in _desktop.GetDesktopItemsAsync())
                {
                    items.Add(item);
                }
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

        Window window = new BoxContainerWindow(vm, _mainVm, _positioning, Save);

        _windows[vm.Id] = window;
        window.Show();
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

        await BuildDefaultContainerAsync();
        EnsureSurface();
        _mainVm.LoadFromContainers(_containers.GetContainers());
        await SaveAsync();
    }

    private void EnsureSurface()
    {
        _surface ??= new DesktopSurface(_mainVm, Save);
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

    public async Task SaveAsync()
    {
        var current = _monitor.GetPrimaryWorkArea();
        var snapshot = new DesktopSnapshot
        {
            DesktopResolution = new SizeD(current.Width, current.Height),
            Containers = _containers.GetContainers().ToList(),
        };
        await _persistence.SaveSnapshotAsync(snapshot);
    }

    public void Save() => _ = SaveAsync();

    public void CloseAll()
    {
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
