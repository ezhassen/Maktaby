using System.Text.Json.Serialization;

namespace Maktaby.Core.Models;

/// <summary>
/// A Box item reduced to the four things that survive a round trip through the clipboard.
/// </summary>
/// <remarks>
/// <para>
/// A <see cref="BoxItem"/> cannot be put on the clipboard directly. It is a live model object with
/// an <see cref="BoxItem.Id"/> and, in the UI, an icon that has already been resolved; a
/// drag payload would drag those objects between windows and keep a view-model graph alive in the
/// system clipboard. This DTO is the serializable projection of one.
/// </para>
/// <para>
/// It carries no position: pasted items are appended, which is what the drop path does when no
/// insertion slot was chosen.
/// </para>
/// </remarks>
public sealed class BoxItemTransfer
{
    /// <summary>Absolute filesystem path. Empty for a shell-only item.</summary>
    public string Path { get; set; } = string.Empty;

    /// <summary>Base64 absolute PIDL for items with no usable path (UWP/Store apps).</summary>
    public string? Pidl { get; set; }

    public string DisplayName { get; set; } = string.Empty;

    public BoxItemType ItemType { get; set; }

    [JsonIgnore]
    public bool IsShellOnly => string.IsNullOrEmpty(Path) && !string.IsNullOrEmpty(Pidl);

    /// <summary>Projects a model item onto the clipboard shape.</summary>
    public static BoxItemTransfer From(BoxItem item) => new()
    {
        Path = item.Path,
        Pidl = item.Pidl,
        DisplayName = item.DisplayName,
        ItemType = item.ItemType,
    };

    /// <summary>
    /// Rebuilds a model item with a FRESH <see cref="BoxItem.Id"/>. The identity must differ: the
    /// pasted item is a new reference in another box, not a second handle on the old one, and
    /// <see cref="BoxItem.Id"/> is what tells the two apart downstream.
    /// </summary>
    public BoxItem ToItem()
    {
        var name = DisplayName;
        if (string.IsNullOrWhiteSpace(name))
        {
            name = IsShellOnly ? "App" : System.IO.Path.GetFileNameWithoutExtension(Path);
        }

        return new BoxItem
        {
            Path = Path,
            Pidl = Pidl,
            DisplayName = name,
            ItemType = ItemType,
        };
    }

    /// <summary>Identity test against a model item, for the deferred-cut removal.</summary>
    public bool Matches(BoxItem item)
    {
        if (!string.IsNullOrEmpty(Path) && !string.IsNullOrEmpty(item.Path))
        {
            return string.Equals(Path, item.Path, StringComparison.OrdinalIgnoreCase);
        }

        return !string.IsNullOrEmpty(Pidl) &&
               string.Equals(Pidl, item.Pidl, StringComparison.Ordinal);
    }
}

/// <summary>
/// Files on the clipboard that this app did not put there - the user copied them in Explorer, or
/// in any other application.
/// </summary>
/// <remarks>
/// Read as a fallback: when the clipboard carries Maktaby's own payload that wins, because it knows
/// about shell items and whether the user meant copy or cut. This shape exists only so a paste can
/// still be useful when the clipboard came from somewhere else.
/// </remarks>
public sealed class ForeignFileDrop
{
    public string[] Paths { get; set; } = Array.Empty<string>();

    /// <summary>
    /// True when the source application asked for the files to be MOVED, i.e. the user cut them.
    /// Explorer communicates this through <c>Preferred DropEffect</c>.
    /// </summary>
    public bool IsMove { get; set; }

    [JsonIgnore]
    public bool HasFiles => Paths.Length > 0;
}

/// <summary>
/// What the clipboard holds for box items: the items, and whether they were cut.
/// </summary>
/// <remarks>
/// A cut does NOT remove anything when it is placed on the clipboard. The items stay in their box
/// until the paste, because a cut here is a real file operation: the copy has to land first, and
/// only then is the original deleted. Removing at Ctrl+X time would delete files for a paste that
/// may never happen.
/// </remarks>
public sealed class BoxItemClipboardPayload
{
    public List<BoxItemTransfer> Items { get; set; } = new();

    /// <summary>
    /// True for Ctrl+X: the paste must delete the originals once their copies exist. False for
    /// Ctrl+C, which leaves them alone and can be pasted repeatedly.
    /// </summary>
    public bool Cut { get; set; }

    [JsonIgnore]
    public bool HasItems => Items.Count > 0;
}