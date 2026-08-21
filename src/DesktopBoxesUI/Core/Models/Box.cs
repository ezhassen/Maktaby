namespace DesktopBoxesUI.Core.Models;

using System.Collections.ObjectModel;

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

    /// <summary>
    /// True for the built-in default box. The default box is fed by the default rule and cannot be
    /// deleted (nor its tab removed by the user).
    /// </summary>
    public bool IsDefault { get; set; }

    /// <summary>The items contained in this Box, in display order.</summary>
    public ObservableCollection<BoxItem> Items { get; set; } = new();
}
