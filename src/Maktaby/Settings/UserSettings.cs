using System.ComponentModel;
using System.Text.Json;
using System.Text.Json.Serialization;
using Wpf.Ui.Appearance;

namespace Maktaby.Settings;

public class UserSettings
{
    /// <summary>
    /// App Selected Theme Dark, light, null(as System)
    /// </summary>
    [Category("Appearance"), DefaultValue(null)]
    public string? SelectedTheme { get; set; }

    /// <summary>
    /// Interface language (BCP-47 code, e.g. "en", "ar"). null = follow the OS UI
    /// culture (English when it has no translation). The .xlf files in Langs/ supply
    /// the text and the language's content direction (LTR/RTL).
    /// </summary>
    [Category("Appearance"), DefaultValue(null)]
    public string? Language { get; set; }

    /// <summary>
    /// Window backdrop effect for app windows: "auto" (let WPF-UI decide per theme),
    /// "none", "mica", "acrylic", or "tabbed". null/unknown = "auto".
    /// </summary>
    [Category("Appearance"), DefaultValue("mica")]
    public string? WindowBackdropType { get; set; }

    public ApplicationTheme GetSelectedTheme()
    {
        switch (SelectedTheme?.Trim().ToLowerInvariant())
        {
            case "light":
                return ApplicationTheme.Light;
            case "dark":
                return ApplicationTheme.Dark;
            default:
                return ApplicationTheme.Unknown;
        }
    }

    public bool SelectedTheme_IsSystem()
    {
        return GetSelectedTheme() switch
        {
            ApplicationTheme.Light or ApplicationTheme.Dark => false,
            _ => true
        };
    }

    /// <summary>
    /// Resolves <see cref="WindowBackdropType"/> to a WPF-UI backdrop. null/unknown → Mica (the
    /// app default); explicit choices map to their enum value.
    /// </summary>
    public Wpf.Ui.Controls.WindowBackdropType GetWindowBackdropType()
    {
        return WindowBackdropType?.Trim().ToLowerInvariant() switch
        {
            "none" => Wpf.Ui.Controls.WindowBackdropType.None,
            "mica" => Wpf.Ui.Controls.WindowBackdropType.Mica,
            "acrylic" => Wpf.Ui.Controls.WindowBackdropType.Acrylic,
            "tabbed" => Wpf.Ui.Controls.WindowBackdropType.Tabbed,
            "auto" => Wpf.Ui.Controls.WindowBackdropType.Auto,
            _ => Wpf.Ui.Controls.WindowBackdropType.Mica,
        };
    }

    public bool SelectedTheme_IsLight()
    {
        return GetSelectedTheme() switch
        {
            ApplicationTheme.Light => true,
            _ => false
        };
    }

    public bool SelectedTheme_IsDark()
    {
        return GetSelectedTheme() switch
        {
            ApplicationTheme.Dark => true,
            _ => false
        };
    }

    /// <summary>
    /// Global default box transparency, 0 = fully opaque .. 1 = fully transparent.
    /// Used when a <see cref="Core.Models.Box"/> does not override it.
    /// </summary>
    [Category("Appearance_Boxes"), DefaultValue(0.5d)]
    public double DefaultBoxTransparencyValue { get; set; } = 0.5d;

    #region Default box theme overrides

    /// <summary>
    /// Default box Selected Theme, dark, light, (null= get app SelectedTheme)
    /// </summary>
    [Category("Appearance_Boxes"), DefaultValue(null)]
    public string? DefaultBoxTheme { get; set; }

    [Category("Appearance_Boxes"), DefaultValue(null)]
    public string? DefaultBoxBackColor { get; set; }

    [Category("Appearance_Boxes"), DefaultValue(null)]
    public string? DefaultBoxTitleBarBackColor { get; set; }

    [Category("Appearance_Boxes"), DefaultValue(null)]
    public string? DefaultBoxForeColor { get; set; }

    [Category("Appearance_Boxes"), DefaultValue(null)]
    public string? DefaultBoxTitleBarForeColor { get; set; }

    [Category("Appearance_Boxes"), DefaultValue(null)]
    public string? DefaultBoxBorderColor { get; set; }

    [Category("Appearance_Boxes"), DefaultValue(1)]
    public double? DefaultBoxBorderThickness { get; set; }

    /// <summary>
    /// When true the TitleBar colors (back, fore) uses Box Colors. (default to true)
    /// </summary>
    [Category("Appearance_Boxes"), DefaultValue(true)]
    public bool DefaultBoxTitleBarColorsSameAsBox { get; set; } = true;

    #endregion

    /// <summary>
    /// Default box item icon size min is 16 max 128. if null use desktop current icon size
    /// </summary>
    [Category("Appearance_Boxes"), DefaultValue(null)]
    public int? DefaultBoxIconSize { get; set; }


    #region WebWidgets

    /// <summary>
    /// Default box Selected Theme, dark, light, (null= get app SelectedTheme)
    /// </summary>
    [Category("Appearance_WebWidgets"), DefaultValue(null)]
    public string? DefaultWebWidgetsTheme { get; set; }

    /// <summary>Suspend widget WebViews when a fullscreen app covers their monitor.</summary>
    [Category("Widgets"), DefaultValue(true)]
    public bool PauseWidgetsOnFullscreen { get; set; } = true;

    /// <summary>Suspend widget WebViews while battery saver is on.</summary>
    [Category("Widgets"), DefaultValue(true)]
    public bool PauseWidgetsOnBatterySaver { get; set; } = true;

    /// <summary>Suspend widget WebViews in remote sessions.</summary>
    [Category("Widgets"), DefaultValue(true)]
    public bool PauseWidgetsOnRemoteSession { get; set; } = true;

    #endregion

    #region Hints

    /// <summary>Suppresses the "container hidden, bring it back via Settings → Containers"
    /// hint shown after hiding a container from its context menu.</summary>
    [Category("General"), DefaultValue(false)]
    public bool HideContainerHintDismissed { get; set; } = false;

    #endregion

    #region LiveWallpaper
    /// <summary>Full path of the live wallpaper video (.mp4). Null/empty = no wallpaper.</summary>
    [Category("LiveWallpaper"), DefaultValue(null)]
    public string? LiveWallpaperPath { get; set; }

    /// <summary>Per-monitor wallpaper assignments (monitor device name -> file path).</summary>
    [Category("LiveWallpaper"), DefaultValue(null)]
    [JsonConverter(typeof(LiveWallpaperAssignmentsConverter))]
    public List<LiveWallpaperMonitorAssignment>? LiveWallpaperAssignments { get; set; }

    /// <summary>Master switch. When false all wallpaper windows are closed (settings kept).</summary>
    [Category("LiveWallpaper"), DefaultValue(true)]
    public bool LiveWallpaperEnabled { get; set; } = false;

    /// <summary>User Play/Pause intent. Auto-pause (fullscreen cover) is runtime-only.</summary>
    [Category("LiveWallpaper"), DefaultValue(true)]
    public bool LiveWallpaperPlaying { get; set; } = true;

    /// <summary>Max total MB of video/GIF bytes pinned in RAM and shared by all monitors showing
    /// the same file (0 disables preloading; files over the cap stream from disk).</summary>
    [Category("LiveWallpaper"), DefaultValue(100)]
    public int LiveWallpaperPreloadMaxMB { get; set; } = 100;

    #endregion

    #region Updates

    /// <summary>Stored as "Auto" / "Stable" / "Beta". Auto (the default) follows the installed
    /// build: a prerelease install tracks prereleases, a stable install tracks stable.</summary>
    [Category("Updates"), DefaultValue("Auto")]
    public string UpdateChannel { get; set; } = "Auto";

    /// <summary>Version the user chose to skip, or null. Applies to exactly this version —
    /// a newer release is offered again. Cleared once an update is actually installed.</summary>
    [Category("Updates"), DefaultValue(null)]
    public string? UpdateSkippedVersion { get; set; }

    /// <summary>Master switch for the background check. The tray "Check for updates" item and
    /// Settings → Updates → "Check now" still work when this is off.</summary>
    [Category("Updates"), DefaultValue(true)]
    public bool CheckForUpdatesOnStartup { get; set; } = true;

    #endregion

}


//public class ColorsProps
//{
//    public string? BackColor { get; set; }
//    public string? ForeColor { get; set; }
//}

public sealed class LiveWallpaperMonitorAssignment
{
    public string Monitor { get; set; } = string.Empty;
    public string Path { get; set; } = string.Empty;
}

/// <summary>Reads both legacy (JSON string containing a dictionary) and new (array of objects) forms.</summary>
public sealed class LiveWallpaperAssignmentsConverter : JsonConverter<List<LiveWallpaperMonitorAssignment>?>
{
    public override List<LiveWallpaperMonitorAssignment>? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.Null) return null;

        // Legacy: a JSON string holding a dictionary<string,string>.
        if (reader.TokenType == JsonTokenType.String)
        {
            var json = reader.GetString();
            if (string.IsNullOrWhiteSpace(json)) return null;
            try
            {
                var dict = JsonSerializer.Deserialize<Dictionary<string, string>>(json, options);
                if (dict is null) return null;
                return dict.Select(kv => new LiveWallpaperMonitorAssignment { Monitor = kv.Key, Path = kv.Value }).ToList();
            }
            catch
            {
                return null;
            }
        }

        if (reader.TokenType == JsonTokenType.StartArray)
        {
            var list = JsonSerializer.Deserialize<List<LiveWallpaperMonitorAssignment>>(ref reader, options);
            return list;
        }

        if (reader.TokenType == JsonTokenType.StartObject)
        {
            var dict = JsonSerializer.Deserialize<Dictionary<string, string>>(ref reader, options);
            if (dict is null) return null;
            return dict.Select(kv => new LiveWallpaperMonitorAssignment { Monitor = kv.Key, Path = kv.Value }).ToList();
        }

        return null;
    }

    public override void Write(Utf8JsonWriter writer, List<LiveWallpaperMonitorAssignment>? value, JsonSerializerOptions options)
    {
        if (value is null) { writer.WriteNullValue(); return; }
        JsonSerializer.Serialize(writer, value, options);
    }
}