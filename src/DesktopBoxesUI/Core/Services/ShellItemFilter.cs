using System.IO;

namespace DesktopBoxesUI.Core.Services;

/// <summary>
/// Filters out shell/desktop items that should never be surfaced in a Box — currently the per-folder
/// <c>desktop.ini</c> configuration file.
/// </summary>
public static class ShellItemFilter
{
    public static bool IsExcluded(string? path)
    {
        if (string.IsNullOrEmpty(path))
        {
            return false;
        }

        return string.Equals(Path.GetFileName(path), "desktop.ini", StringComparison.OrdinalIgnoreCase);
    }
}
