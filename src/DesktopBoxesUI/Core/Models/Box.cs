namespace DesktopBoxesUI.Core.Models;

/// <summary>
/// A single box inside a <see cref="BoxContainer"/>. Holds its items and behaviour; geometry and visual
/// styling (bounds, lock, visibility, transparency) live on the owning <see cref="BoxContainer"/>, not
/// here.
/// </summary>
public sealed class Box
{
    public System.Guid Id { get; init; } = System.Guid.NewGuid();

    public string Name { get; set; } = string.Empty;

    /// <summary>The behaviour of this Box (DesktopItems, FolderPortal, …).</summary>
    public BoxType BoxType { get; set; } = BoxType.DesktopItems;

    /// <summary>The items contained in this Box, in display order.</summary>
    public List<BoxItem> Items { get; set; } = new();
}
