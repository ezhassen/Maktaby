using DesktopBoxesUI.Core.Models;

namespace DesktopBoxesUI.Core.Interfaces;

/// <summary>
/// Provides metadata about shell items (files, folders, special objects) addressed by path.
/// Implemented via Windows Shell APIs in the Shell layer.
/// </summary>
public interface IShellItemService
{
    BoxItemType GetItemType(string path);

    string GetDisplayName(string path);
}
