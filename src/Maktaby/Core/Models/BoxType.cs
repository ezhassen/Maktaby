namespace Maktaby.Core.Models;

/// <summary>
/// The behaviour of a <see cref="Box"/>. Currently only <see cref="DesktopItems"/> is wired up;
/// <see cref="FolderPortal"/> is scaffolded (selected via the box menu) and will later bind the box
/// to a live folder's contents.
/// </summary>
public enum BoxType
{
    DesktopItems = 0,
    FolderPortal = 1,
}
