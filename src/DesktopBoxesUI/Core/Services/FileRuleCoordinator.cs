using System;
using System.IO;
using System.Linq;
using DesktopBoxesUI.Core.Interfaces;
using DesktopBoxesUI.Core.Models;
using DesktopBoxesUI.Core.Services;

namespace DesktopBoxesUI.Core.Services;

/// <summary>
/// Bridges the desktop file-system watcher to the box rule engine: when a file appears or disappears
/// on the desktop it is routed (by file type) into the target <see cref="Box"/> and the snapshot is
/// persisted. Only <see cref="BoxType.DesktopItems"/> boxes receive items.
/// </summary>
public sealed class FileRuleCoordinator : IFileRuleCoordinator
{
    private readonly IFileWatcherService _watcher;
    private readonly IRuleService _rules;
    private readonly IBoxService _boxService;
    private readonly IDispatcher _dispatcher;
    private readonly Action _save;

    public FileRuleCoordinator(
        IFileWatcherService watcher,
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
        _watcher.FileCreated += OnFileCreated;
        _watcher.FileDeleted += OnFileDeleted;
        _watcher.Start();
    }

    public void Stop()
    {
        _watcher.FileCreated -= OnFileCreated;
        _watcher.FileDeleted -= OnFileDeleted;
        _watcher.Stop();
    }

    private void OnFileCreated(string path) => _dispatcher.Invoke(() => HandleCreated(path));

    private void OnFileDeleted(string path) => _dispatcher.Invoke(() => HandleDeleted(path));

    private void HandleCreated(string path)
    {
        var item = BoxItemFactory.FromPath(path);
        if (item is null || string.IsNullOrEmpty(item.Path))
        {
            return;
        }

        if (AlreadyTracked(item.Path))
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

    private void HandleDeleted(string path)
    {
        bool changed = false;
        foreach (var box in _boxService.GetBoxes())
        {
            if (box.BoxType != BoxType.DesktopItems)
            {
                continue;
            }

            var existing = box.Items.FirstOrDefault(i =>
                string.Equals(i.Path, path, StringComparison.OrdinalIgnoreCase));
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

    private bool AlreadyTracked(string path)
    {
        foreach (var box in _boxService.GetBoxes())
        {
            if (box.Items.Any(i => string.Equals(i.Path, path, StringComparison.OrdinalIgnoreCase)))
            {
                return true;
            }
        }

        return false;
    }
}
