using Maktaby.Core.Models;

namespace Maktaby.Core.Interfaces;

/// <summary>
/// Provides metadata about shell items (files, folders, special objects) addressed by path.
/// Implemented via Windows Shell APIs in the Shell layer.
/// </summary>
public interface IShellItemService
{
    BoxItemType GetItemType(string path);

    string GetDisplayName(string path);
}
