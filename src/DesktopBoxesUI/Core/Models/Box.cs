namespace DesktopBoxesUI.Core.Models;

/// <summary>
/// A "Box": a movable, resizable, persistent container that groups desktop items.
/// A Box aggregates many <see cref="BoxItem"/>s; it is NOT one window per item.
/// </summary>
public sealed class Box
{
    public Guid Id { get; init; } = Guid.NewGuid();

    public string Name { get; set; } = string.Empty;

    public double Left { get; set; }
    public double Top { get; set; }
    public double Width { get; set; } = 240;
    public double Height { get; set; } = 200;

    /// <summary>When locked, the Box cannot be moved or resized by the user.</summary>
    public bool IsLocked { get; set; }

    public bool IsVisible { get; set; } = true;

    /// <summary>
    /// Box fill transparency, 0 = fully opaque .. 1 = fully transparent. When null, the global
    /// default (see <see cref="Interfaces.SettingsKeys.DefaultBoxTransparency"/>) is used.
    /// </summary>
    public double? Transparency { get; set; }

    /// <summary>The items contained in this Box, in display order.</summary>
    public List<BoxItem> Items { get; set; } = new();
}
