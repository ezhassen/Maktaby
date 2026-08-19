namespace DesktopBoxesUI.Core.Models;

/// <summary>
/// A single item (shortcut, file, folder, etc.) that has been placed inside a <see cref="Box"/>.
/// Lightweight and easy to extend; does not yet carry icon/image state.
/// </summary>
public sealed class BoxItem
{
    public Guid Id { get; init; } = Guid.NewGuid();

    /// <summary>Full filesystem or shell path identifying this item.</summary>
    public string Path { get; set; } = string.Empty;

    /// <summary>Human-readable name shown in the UI.</summary>
    public string DisplayName { get; set; } = string.Empty;

    public BoxItemType ItemType { get; set; } = BoxItemType.Unknown;

    /// <summary>Sort/display order of the item within its parent <see cref="Box"/>.</summary>
    public int Order { get; set; }

    /// <summary>
    /// Optional layout hint relative to the owning Box (in Box-local coordinates).
    /// Null means the Box uses a flowing layout instead of free positioning.
    /// </summary>
    public double? RelativeLeft { get; set; }

    /// <summary>Optional layout hint relative to the owning Box (in Box-local coordinates).</summary>
    public double? RelativeTop { get; set; }
}
