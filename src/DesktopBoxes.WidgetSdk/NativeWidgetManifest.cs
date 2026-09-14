using System.IO;
using System.Text.Json;

namespace DesktopBoxes.WidgetSdk;

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
