using System.Text.Json.Serialization;

namespace DesktopBoxesUI.Core.Models;

/// <summary>
/// The generic, persisted desktop entity. One DesktopItemContainer is one placed-on-desktop element.
/// It is either a <see cref="DesktopItemContainerType.BoxContainer"/> (wrapping a <see cref="BoxContainer"/>)
/// or a <see cref="DesktopItemContainerType.Custom"/> widget (<see cref="CustomTypeName"/> /
/// <see cref="CustomData"/>). All placement and styling (<see cref="Bounds"/>, <see cref="IsLocked"/>,
/// <see cref="IsVisible"/>, <see cref="Transparency"/>) live here, regardless of type.
/// </summary>
public sealed class DesktopItemContainer
{
    public System.Guid Id { get; init; } = System.Guid.NewGuid();

    /// <summary>Window rectangle on the desktop, in DIPs (used by every container type).</summary>
    public RectD Bounds { get; set; }

    public DesktopItemContainerType Type { get; set; } = DesktopItemContainerType.BoxContainer;

    /// <summary>Set when <see cref="Type"/> is <see cref="DesktopItemContainerType.Custom"/>; names the widget.</summary>
    public string? CustomTypeName { get; set; }

    /// <summary>Optional serialized payload for a custom widget.</summary>
    public string? CustomData { get; set; }

    /// <summary>When locked, the container cannot be moved or resized by the user.</summary>
    public bool IsLocked { get; set; }

    public bool IsVisible { get; set; } = true;

    /// <summary>
    /// Fill transparency, 0 = fully opaque .. 1 = fully transparent. When null, the global default is used.
    /// </summary>
    public double? Transparency { get; set; }

    /// <summary>The wrapped <see cref="BoxContainer"/> when <see cref="Type"/> is <see cref="DesktopItemContainerType.BoxContainer"/>.</summary>
    public BoxContainer? ChildContainer { get; set; }

    /// <summary>Convenience accessor for the active child object (not serialized).</summary>
    [JsonIgnore]
    public object? Child => Type == DesktopItemContainerType.BoxContainer ? ChildContainer : null;
}
