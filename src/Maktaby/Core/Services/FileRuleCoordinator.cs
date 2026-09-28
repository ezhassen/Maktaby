using Maktaby.Core.Interfaces;
using Maktaby.Core.Models;
using Maktaby.Core.Services;
using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;

namespace Maktaby.Core.Services;

/// <summary>
/// Bridges the desktop Shell watcher to the box rule engine: when an item appears or disappears on the
/// desktop it is routed (by file type) into the target <see cref="Box"/> and the snapshot is persisted.
/// Only <see cref="BoxType.DesktopItems"/> boxes receive items. Works for both real files and virtual
/// shell items (the watcher resolves every change to a <see cref="BoxItem"/>). Also performs the
/// user-initiated rename/delete of tracked items, pausing the watcher so the app doesn't react to its
/// own mutation.
/// </summary>
public sealed class FileRuleCoordinator : IFileRuleCoordinator
{
    private readonly IShellWatcherService _watcher;
    private readonly IRuleService _rules;
    private readonly IBoxService _boxService;
    private readonly IDispatcher _dispatcher;
    private readonly IFileOperationService _fileOps;
    private readonly Action _save;

    public FileRuleCoordinator(
        IShellWatcherService watcher,
        IRuleService rules,
        IBoxService boxService,
        IDispatcher dispatcher,
        IFileOperationService fileOps,
        Action save)
    {
        _watcher = watcher;
        _rules = rules;
        _boxService = boxService;
        _dispatcher = dispatcher;
        _fileOps = fileOps;
        _save = save;
    }

    public void Start()
    {
        _watcher.ItemCreated += OnItemCreated;
        _watcher.ItemDeleted += OnItemDeleted;
        _watcher.Start();
    }

    public void Stop()
    {
        _watcher.ItemCreated -= OnItemCreated;
        _watcher.ItemDeleted -= OnItemDeleted;
        _watcher.Stop();
    }

    private void OnItemCreated(BoxItem item) => _dispatcher.Invoke(() => HandleCreated(item));

    private void OnItemDeleted(BoxItem item) => _dispatcher.Invoke(() => HandleDeleted(item));

    private void HandleCreated(BoxItem item)
    {
        if (item is null || string.IsNullOrEmpty(item.Path))
        {
            return;
        }

        if (AlreadyTracked(item))
        {
            return;
        }

        var targetId = _rules.MatchTargetBoxId(Path.GetFileName(item.Path));
        if (targetId is null)
        {
            return;
        }

        var box = _boxService.GetBox(targetId.Value);
        if (box is null || box.BoxType != BoxType.DesktopItems)
        {
            return;
        }

        box.Items.Add(item);
        _save();
    }

    private void HandleDeleted(BoxItem item)
    {
        if (item is null || string.IsNullOrEmpty(item.Path))
        {
            return;
        }

        bool changed = false;
        foreach (var box in _boxService.GetBoxes())
        {
            if (box.BoxType != BoxType.DesktopItems)
            {
                continue;
            }

            var existing = box.Items.FirstOrDefault(i => BoxItem.RefersToSame(i, item));
            if (existing != null)
            {
                box.Items.Remove(existing);
                changed = true;
            }
        }

        if (changed)
        {
            _save();
        }
    }

    private bool AlreadyTracked(BoxItem item)
    {
        foreach (var box in _boxService.GetBoxes())
        {
            if (box.Items.Any(i => BoxItem.RefersToSame(i, item)))
            {
                return true;
            }
        }

        return false;
    }

    public Task<bool> RenameItemAsync(BoxItem item, string newName)
    {
        if (item is null || string.IsNullOrWhiteSpace(newName))
        {
            return Task.FromResult(false);
        }

        _watcher.Pause();
        try
        {
            if (!string.IsNullOrEmpty(item.Path))
            {
                if (!_fileOps.Rename(item.Path, newName.Trim()))
                {
                    return Task.FromResult(false);
                }

                var dir = Path.GetDirectoryName(item.Path);
                if (dir is not null)
                {
                    item.Path = Path.Combine(dir, newName.Trim());
                }

                // Show the friendly (extension-less) name, matching how the item was first displayed.
                item.DisplayName = Path.GetFileNameWithoutExtension(item.Path);
            }
            else
            {
                item.DisplayName = newName.Trim();
            }
        }
        finally
        {
            _watcher.Resume();
        }

        _save();
        return Task.FromResult(true);
    }

    public async Task<bool> DeleteItemAsync(BoxItem item, bool permanent)
    {
        if (item is null)
        {
            return false;
        }

        if (!string.IsNullOrEmpty(item.Path))
        {
            _watcher.Pause();
            try
            {
                if (!await _fileOps.DeleteAsync([item.Path], permanent))
                {
                    return false;
                }
            }
            finally
            {
                _watcher.Resume();
            }
        }

        // Remove the (now-deleted) item from every DesktopItems box it was tracked in.
        foreach (var box in _boxService.GetBoxes())
        {
            if (box.BoxType != BoxType.DesktopItems)
            {
                continue;
            }

            var existing = box.Items.FirstOrDefault(i => BoxItem.RefersToSame(i, item));
            if (existing is not null)
            {
                box.Items.Remove(existing);
            }
        }

        _save();
        return true;
    }

    public async Task<bool> DeleteItemsAsync(IEnumerable<BoxItem> items, bool permanent)
    {
        if (!(items?.Any() == true))
        {
            return false;
        }
        var itemsPaths = items.Where(d => !string.IsNullOrEmpty(d.Path)).Select(d => d.Path).ToList();
        if (itemsPaths.Any())
        {
            _watcher.Pause();
            try
            {
                if (!await _fileOps.DeleteAsync(itemsPaths, permanent))
                {
                    return false;
                }
            }
            finally
            {
                _watcher.Resume();
            }
        }

        foreach (var item in items)
        {
            // Remove the (now-deleted) item from every DesktopItems box it was tracked in.
            foreach (var box in _boxService.GetBoxes())
            {
                if (box.BoxType != BoxType.DesktopItems)
                {
                    continue;
                }

                var existing = box.Items.FirstOrDefault(i => BoxItem.RefersToSame(i, item));
                if (existing is not null)
                {
                    box.Items.Remove(existing);
                }
            }
        }

        _save();
        return true;
    }
}
