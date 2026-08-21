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

    /// <summary>
    /// For non-filesystem shell items (e.g. UWP/Store apps dragged from the Start Menu), this holds
    /// the base64 of the item's absolute PIDL. The PIDL is used to resolve the icon and to launch the
    /// item, since such items have no usable filesystem path.
    /// </summary>
    public string? Pidl { get; set; }

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

    /// <summary>
    /// True when two items point at the same underlying desktop object. Filesystem/shell items match by
    /// <see cref="Path"/>; non-filesystem items (e.g. UWP apps from the Start Menu) have no usable path and
    /// match by their absolute <see cref="Pidl"/> instead. One identity is enough — both need not match.
    /// </summary>
    public static bool RefersToSame(BoxItem? a, BoxItem? b)
    {
        if (ReferenceEquals(a, b))
        {
            return true;
        }

        if (a is null || b is null)
        {
            return false;
        }

        if (!string.IsNullOrEmpty(a.Path) && !string.IsNullOrEmpty(b.Path))
        {
            return string.Equals(a.Path, b.Path, StringComparison.OrdinalIgnoreCase);
        }

        if (!string.IsNullOrEmpty(a.Pidl) && !string.IsNullOrEmpty(b.Pidl))
        {
            return string.Equals(a.Pidl, b.Pidl, StringComparison.OrdinalIgnoreCase);
        }

        return false;
    }
}
