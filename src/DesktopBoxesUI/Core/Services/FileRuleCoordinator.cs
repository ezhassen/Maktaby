using System;
using System.IO;
using System.Linq;
using DesktopBoxesUI.Core.Interfaces;
using DesktopBoxesUI.Core.Models;
using DesktopBoxesUI.Core.Services;

namespace DesktopBoxesUI.Core.Services;

/// <summary>
/// Bridges the desktop Shell watcher to the box rule engine: when an item appears or disappears on the
/// desktop it is routed (by file type) into the target <see cref="Box"/> and the snapshot is persisted.
/// Only <see cref="BoxType.DesktopItems"/> boxes receive items. Works for both real files and virtual
/// shell items (the watcher resolves every change to a <see cref="BoxItem"/>).
/// </summary>
public sealed class FileRuleCoordinator : IFileRuleCoordinator
{
    private readonly IShellWatcherService _watcher;
    private readonly IRuleService _rules;
    private readonly IBoxService _boxService;
    private readonly IDispatcher _dispatcher;
    private readonly Action _save;

    public FileRuleCoordinator(
        IShellWatcherService watcher,
        IRuleService rules,
        IBoxService boxService,
        IDispatcher dispatcher,
        Action save)
    {
        _watcher = watcher;
        _rules = rules;
        _boxService = boxService;
        _dispatcher = dispatcher;
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
}
