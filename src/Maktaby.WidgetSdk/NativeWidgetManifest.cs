using System.IO;
using System.Text.Json;

namespace Maktaby.WidgetSdk;

/// <summary><c>nwidget.json</c> for a native widget folder. Identity is the folder name
/// (slug); there is no id field. Unknown JSON members are ignored so manifests stay
/// forward-compatible.</summary>
public sealed class NativeWidgetManifest
{
    public string Name { get; set; } = "";
    public string Author { get; set; } = "";
    public string Description { get; set; } = "";
    public string Version { get; set; } = "1.0";

    /// <summary>DLL file name for <c>kind=assembly</c>. Null = the single <c>*.dll</c> in
    /// the folder (error when ambiguous).</summary>
    public string? Assembly { get; set; }

    /// <summary>Full type name implementing <see cref="INativeWidget"/>. Null = the single
    /// such type in the assembly (error when zero or ambiguous).</summary>
    public string? Type { get; set; }

    public double Width { get; set; } = 300;
    public double Height { get; set; } = 220;
    public bool Resizable { get; set; } = true;
    public bool SupportsTheme { get; set; } = true;
    public string? Thumbnail { get; set; }

    /// <summary>Opt in to the host's title bar drawn over the top of the widget (title text,
    /// lock toggle, menu button). Defaults to <c>false</c>: a widget that draws its own
    /// header would otherwise have it covered, and the host already supplies the affordances
    /// a title bar provides — click-to-focus, right-click menu, and
    /// <see cref="NativeWidgetControl.MoveWindowOnMouseDown"/> for dragging.
    ///
    /// Collapsing it does NOT remove the chrome overlay: that window keeps owning the
    /// resize edges and the selection border. Set this only if the widget genuinely has
    /// no content in its top ~28px.</summary>
    public bool ShowTitleBar { get; set; }

    /// <summary>Offer an extra "Auto" option in the automatic theme setting, in addition to
    /// follow-app / light / dark. The host passes <c>auto</c> through to
    /// <c>INativeWidget.ApplyTheme</c> untouched, so the widget decides what it means —
    /// typically deriving light or dark from its own background colour, which is only sensible
    /// for a widget that draws one. Ignored unless <see cref="SupportsTheme"/> is also true.</summary>
    public bool AutoTheme { get; set; }

    public static NativeWidgetManifest DefaultFor(string slug) => new() { Name = slug };

    /// <summary>Parses and clamps; never throws (errors out via <paramref name="error"/>).</summary>
    public static NativeWidgetManifest? TryParse(string json, string slug, out string? error)
    {
        error = null;
        try
        {
            var options = new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true,
                AllowTrailingCommas = true,
                ReadCommentHandling = JsonCommentHandling.Skip,
            };
            var manifest = JsonSerializer.Deserialize<NativeWidgetManifest>(json, options);
            if (manifest is null)
            {
                error = "Manifest parsed as null.";
                return null;
            }
            if (string.IsNullOrWhiteSpace(manifest.Name)) manifest.Name = slug;
            if (manifest.Width <= 0) manifest.Width = 300;
            if (manifest.Height <= 0) manifest.Height = 220;
            return manifest;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return null;
        }
    }
}
