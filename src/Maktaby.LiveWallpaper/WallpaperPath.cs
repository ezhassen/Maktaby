namespace Maktaby.LiveWallpaper;

/// <summary>Wallpaper source kind, classified by path. Single source of truth — classify once
/// per call site with <see cref="WallpaperPath.GetRenderTypeFromPath"/> instead of repeating
/// extension checks.</summary>
public enum WallpaperKind
{
    /// <summary>Not a wallpaper: no path, or an unsupported container. Callers skip these
    /// (log + fall through to the OS wallpaper) — never attempt playback on them.</summary>
    None,
    /// <summary>Static image or animated GIF (<see cref="WallpaperPath.ImageExtensions"/>).</summary>
    Image,
    /// <summary>Video container the engine attempts to decode
    /// (<see cref="Playback.CodecSupport.VideoExtensions"/>).</summary>
    Video,
    /// <summary>HTML/CSS/JS file or a folder (used via its index.html).</summary>
    Web,
}

/// <summary>Single home for wallpaper path classification (extensions + predicates + kind).
/// Moved off <c>Engine</c> so renderers, the file picker and the UI share them.</summary>
public static class WallpaperPath
{
    public static readonly IReadOnlySet<string> ImageExtensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        { ".png", ".jpg", ".jpeg", ".bmp", ".gif", ".tif", ".tiff" };

    /// <summary>Entry-point extensions for HTML wallpapers (WebviewRender). A folder whose
    /// index.html is used also counts — see <see cref="IsWebPath"/>.</summary>
    public static readonly IReadOnlySet<string> WebExtensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        { ".html", ".htm", ".css", ".js" };

    public static bool IsImagePath(string? path)
    {
        if (string.IsNullOrEmpty(path)) return false;
        return Path.GetExtension(path) is string ext && ImageExtensions.Contains(ext);
    }

    public static bool IsWebPath(string? path)
    {
        if (string.IsNullOrEmpty(path)) return false;
        if (Directory.Exists(path)) return true;
        return Path.GetExtension(path) is string ext && WebExtensions.Contains(ext);
    }

    /// <summary>True for video containers the engine attempts to decode — a positive check
    /// against <see cref="Playback.CodecSupport.VideoExtensions"/>, never a fallback.
    /// Unknown containers are <see cref="WallpaperKind.None"/>, not video.</summary>
    public static bool IsVideoPath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return false;
        return Playback.CodecSupport.VideoExtensions.Contains(Path.GetExtension(path));
    }

    /// <summary>Classifies <paramref name="path"/> once; prefer this over the individual
    /// predicates when a method branches on kind.</summary>
    public static WallpaperKind GetRenderTypeFromPath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return WallpaperKind.None;
        if (IsImagePath(path)) return WallpaperKind.Image;
        if (IsVideoPath(path)) return WallpaperKind.Video;
        if (IsWebPath(path)) return WallpaperKind.Web;
        return WallpaperKind.None;
    }
}
