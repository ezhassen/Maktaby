namespace DesktopBoxesUI.Core.Models;

/// <summary>
/// The kind of child a <see cref="DesktopItemContainer"/> wraps. A container is one placed-on-desktop
/// entity; it is either a <see cref="BoxContainer"/> (which handles all visuals and the boxes it
/// contains) or a custom widget identified by <see cref="DesktopItemContainer.CustomTypeName"/>.
/// </summary>
public enum DesktopItemContainerType
{
    Custom = 0,
    BoxContainer = 1,
}
