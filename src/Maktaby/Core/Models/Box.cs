namespace Maktaby.Core.Models;

using Maktaby.Settings;
using System.Collections.ObjectModel;

/// <summary>
/// A single box inside a <see cref="BoxContainer"/>. Holds its items and behaviour; geometry and visual
/// styling (bounds, lock, visibility, transparency) live on the owning <see cref="BoxContainer"/>, not
/// here.
/// </summary>
using System.Text.Json.Serialization;

[JsonConverter(typeof(BoxJsonConverter))]
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

    /// <summary>
    /// Box icon size min 16 to 128 max. if null use <see cref="UserSettings.DefaultBoxIconSize"/>
    /// </summary>
    public int? IconSize { get; set; }

    /// <summary>The items contained in this Box, in display order.</summary>
    public ObservableCollection<BoxItem> Items { get; set; } = new();

    /// <summary>Absolute path of the folder this portal shows (only when <see cref="BoxType"/> is FolderPortal).</summary>
    public string? FolderPath { get; set; }

    /// <summary>View mode for FolderPortal (Icons or Details).</summary>
    public FolderPortalViewMode FolderPortalViewMode { get; set; } = FolderPortalViewMode.Details;

    /// <summary>Sort key for FolderPortal.</summary>
    public FolderSortMode FolderSortBy { get; set; } = FolderSortMode.Name;

    /// <summary>True = ascending, false = descending.</summary>
    public bool FolderSortAscending { get; set; } = true;
}
