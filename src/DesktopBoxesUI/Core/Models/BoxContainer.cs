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

    /// <summary>Whether the container is rolled (collapsed to the <see cref="RollDirection"/> edge). Persisted; defaults to false.</summary>
    public bool IsRolled { get; set; }

    /// <summary>
    /// Edge the rolled TitleBar snaps to. <c>null</c> means "auto": detect from where the container is
    /// snapped (default behavior). A non-null value is an explicit user choice that overrides detection.
    /// Persisted.
    /// </summary>
    public RollDirection? RollDirection { get; set; }
}
