using System.IO;
using DesktopBoxesUI.Core.Models;

namespace DesktopBoxesUI.Core.Services;

/// <summary>
/// Builds a <see cref="BoxItem"/> from a dropped filesystem path (e.g. a file dragged from the
/// Start Menu or File Explorer). Pure model construction; no WPF or Win32 dependencies.
/// </summary>
public static class BoxItemFactory
{
    public static BoxItem? FromPath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        bool isDir = Directory.Exists(path);
        var name = Path.GetFileNameWithoutExtension(path);
        if (string.IsNullOrEmpty(name))
        {
            name = Path.GetFileName(path);
        }

        return new BoxItem
        {
            Path = path,
            DisplayName = name,
            ItemType = isDir ? BoxItemType.Folder : BoxItemType.File,
        };
    }
}
