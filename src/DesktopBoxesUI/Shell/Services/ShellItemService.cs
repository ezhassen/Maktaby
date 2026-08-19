using System.IO;
using DesktopBoxesUI.Core.Interfaces;
using DesktopBoxesUI.Core.Models;

namespace DesktopBoxesUI.Shell.Services;

/// <summary>
/// Minimal, dependency-free implementation of <see cref="IShellItemService"/> based on the
/// Base Class Library only. Real Shell COM metadata (verbs, special folders, etc.) can replace
/// this later without affecting callers.
/// </summary>
public sealed class ShellItemService : IShellItemService
{
    public BoxItemType GetItemType(string path)
    {
        if (string.IsNullOrEmpty(path))
        {
            return BoxItemType.Unknown;
        }

        if (path.EndsWith(".lnk", StringComparison.OrdinalIgnoreCase) ||
            path.EndsWith(".url", StringComparison.OrdinalIgnoreCase))
        {
            return BoxItemType.Shortcut;
        }

        if (Directory.Exists(path))
        {
            return BoxItemType.Folder;
        }

        if (File.Exists(path))
        {
            return BoxItemType.File;
        }

        return BoxItemType.Unknown;
    }

    public string GetDisplayName(string path)
    {
        if (string.IsNullOrEmpty(path))
        {
            return string.Empty;
        }

        var name = Path.GetFileName(path);
        if (name.EndsWith(".lnk", StringComparison.OrdinalIgnoreCase) && name.Length > 4)
        {
            name = name[..^4];
        }

        return name;
    }
}
