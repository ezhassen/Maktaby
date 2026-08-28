using System.Text.Json.Serialization;

namespace DesktopBoxesUI.Core.Models;

/// <summary>Optional widget.json describing a CssWidget.</summary>
public sealed class CssWidgetManifest
{
    public string? Name { get; set; }
    public string? Author { get; set; }
    public string? Description { get; set; }
    public string? Version { get; set; }

    /// <summary>Default window width in DIPs (fallback 300).</summary>
    public int? Width { get; set; }
    public int? Height { get; set; }

    /// <summary>Whether the widget window is resizable. Null = true.</summary>
    public bool? Resizable { get; set; }

    /// <summary>Allow fetch/XHR/WebSocket. Null = false (network off).</summary>
    public bool? AllowNetwork { get; set; }

    /// <summary>Thumbnail file relative to widget folder (default thumbnail.png).</summary>
    public string? Thumbnail { get; set; }

    [JsonIgnore]
    public bool IsResizable => Resizable ?? true;

    [JsonIgnore]
    public bool IsNetworkAllowed => AllowNetwork ?? false;

    public static CssWidgetManifest DefaultFor(string slug) => new()
    {
        Name = slug,
        Width = 300,
        Height = 200,
        Resizable = true,
        AllowNetwork = false
    };
}
