using System.Collections.Generic;

namespace DesktopBoxesUI.Core.Models;

/// <summary>
/// The tabbed set of <see cref="Box"/>es belonging to a <see cref="DesktopItemContainer"/> of type
/// <see cref="DesktopItemContainerType.BoxContainer"/>. Holds only the boxes and the active-tab index;
/// geometry and styling live on the owning <see cref="DesktopItemContainer"/>.
/// </summary>
public sealed class BoxContainer
{
    public System.Guid Id { get; init; } = System.Guid.NewGuid();

    /// <summary>The boxes shown as tabs, in display order.</summary>
    public List<Box> Boxes { get; set; } = new();

    /// <summary>Index of the active tab. Clamped on load to stay within <see cref="Boxes"/>.</summary>
    public int SelectedIndex { get; set; }
}
