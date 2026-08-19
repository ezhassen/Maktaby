using DesktopBoxesUI.Core.Interfaces;
using DesktopBoxesUI.Core.Models;
using DesktopBoxesUI.Core.Services;
using DesktopBoxesUI.ViewModels;
using DesktopBoxesUI.Views;
using Microsoft.Extensions.DependencyInjection;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.Linq;
using System.Runtime.Versioning;
using System.Threading.Tasks;

namespace DesktopBoxesUI;

/// <summary>
/// Orchestrates the live Box windows. One <see cref="BoxWindow"/> per <see cref="BoxViewModel"/>,
/// kept in sync through the view-model collection. Owns loading/saving the snapshot (the db file)
/// and hiding/restoring Explorer's desktop icons. Resolves its dependencies from the
/// composition root (no new platform code here).
/// </summary>
[SupportedOSPlatform("windows10.0.14393")]
public sealed class DesktopManager
{
    private readonly MainViewModel _mainVm;
    private readonly IBoxService _boxService;
    private readonly IPersistenceService _persistence;
    private readonly IDesktopService _desktop;
    private readonly IWindowPositioningService _positioning;
    private readonly IExplorerDesktopService _explorer;

    private readonly Dictionary<Guid, BoxWindow> _windows = new();
    private DesktopSurface? _surface;

    public DesktopManager(IServiceProvider provider)
    {
        _mainVm = provider.GetRequiredService<MainViewModel>();
        _boxService = provider.GetRequiredService<IBoxService>();
        _persistence = provider.GetRequiredService<IPersistenceService>();
        _desktop = provider.GetRequiredService<IDesktopService>();
        _positioning = provider.GetRequiredService<IWindowPositioningService>();
        _explorer = provider.GetRequiredService<IExplorerDesktopService>();
    }

    public async Task InitializeAsync()
    {
        //await Task.Delay(100);// delaying not fixing no boxes issue
        var stored = await _persistence.LoadBoxesAsync();
        if (stored is not { Count: > 0 })
        {
            await BuildDefaultBoxAsync();
        }
        else
        {
            //TODO: Detect new items and add it to default box, and delete none-exiting items
            foreach (var box in stored)
            {
                _boxService.AddBox(box);
            }
        }

        _mainVm.Boxes.CollectionChanged += Boxes_CollectionChanged;
        _mainVm.LoadFromBoxes(_boxService.GetBoxes());

        // The desktop surface (empty-area drop target) is shown first so it sits behind the Boxes.
        EnsureSurface();

        // Ensure every loaded Box has a window even if CollectionChanged timing skipped it.
        foreach (var vm in _mainVm.Boxes)
        {
            AddWindow(vm);
        }

        await SaveAsync();
        _explorer.SetDesktopIconsVisible(false);
    }

    private async Task BuildDefaultBoxAsync()
    {
        var items = new List<BoxItem>();
        try
        {
            // Explorer's desktop namespace may not be initialized yet at first run, so a single
            // enumeration can yield nothing. Retry briefly until items appear (or attempts run out)
            // so the default Box captures the real desktop icons.
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
            // Shell enumeration can fail on exotic sessions; fall back to an empty default Box
            // rather than crashing startup (which would leave desktop icons hidden).
        }

        var box = _boxService.CreateBox("Desktop", 60, 60, 300, 460);
        foreach (var item in items)
        {
            box.Items.Add(item);
        }
    }

    private void Boxes_CollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.Action == NotifyCollectionChangedAction.Reset)
        {
            CloseAll();
            return;
        }

        if (e.NewItems != null)
        {
            foreach (BoxViewModel vm in e.NewItems)
            {
                AddWindow(vm);
            }
        }

        if (e.OldItems != null)
        {
            foreach (BoxViewModel vm in e.OldItems)
            {
                RemoveWindow(vm.Id);
            }
        }
    }

    private void AddWindow(BoxViewModel vm)
    {
        if (_windows.ContainsKey(vm.Id))
        {
            return;
        }

        var window = new BoxWindow(vm, _mainVm, _positioning, Save);
        _windows[vm.Id] = window;
        window.Show();
    }

    private void RemoveWindow(Guid id)
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
        foreach (var box in _boxService.GetBoxes().ToList())
        {
            _boxService.RemoveBox(box.Id);
        }

        await BuildDefaultBoxAsync();
        EnsureSurface();
        _mainVm.LoadFromBoxes(_boxService.GetBoxes());
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

    public async Task SaveAsync()
    {
        await _persistence.SaveBoxesAsync(_boxService.GetBoxes());
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
