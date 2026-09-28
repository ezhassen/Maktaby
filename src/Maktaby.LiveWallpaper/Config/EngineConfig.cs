using System.Text.Json;
using System.Text.Json.Serialization;

namespace Maktaby.LiveWallpaper.Config;

public enum FitMode { Fill, Fit, Stretch }

public sealed class WallpaperAssignment
{
    /// <summary>Monitor device name (e.g. \\.\DISPLAY1); "*" applies to all monitors.</summary>
    public string Monitor { get; set; } = "*";
    public string Path { get; set; } = "";
}

public sealed class PauseConfig : Maktaby.Native.Playback.PauseConfig
{
    // Shared shape (Maktaby.Native.Playback.PauseConfig) so both hosts map through ToPolicy:
    // same JSON, no persistence change.
}

public sealed class EngineConfig
{
    public List<WallpaperAssignment> Wallpapers { get; set; } = [];
    public FitMode Fit { get; set; } = FitMode.Fill;
    public bool MuteVideo { get; set; } = true;
    public double Volume { get; set; } = 0.3;
    public PauseConfig Pause { get; set; } = new();

    /// <summary>Cap in bytes for the shared in-memory media cache (videos, animated GIFs at or
    /// under this size are read once and shared by all monitors showing them). 0 disables.</summary>
    public long PreloadMaxBytes { get; set; } = 100L * 1024 * 1024;

    /// <summary>
    /// 
    /// </summary>
    /// <param name="monitorDevice"></param>
    /// <returns></returns>
    public string? WallpaperFor(string monitorDevice)
    {
        var exact = Wallpapers.FirstOrDefault(w => string.Equals(w.Monitor, monitorDevice, StringComparison.OrdinalIgnoreCase));
        var star = Wallpapers.FirstOrDefault(w => w.Monitor == "*");
        var path = (exact ?? star)?.Path;
        return string.IsNullOrWhiteSpace(path) ? null : path;
    }

    /// <summary>
    /// Assign a wallpaper to a monitor
    /// </summary>
    /// <param name="monitorDevice">target monitor. * for all</param>
    /// <param name="path"></param>
    public void Assign(string monitorDevice, string path)
    {
        var existing = Wallpapers.FirstOrDefault(w => string.Equals(w.Monitor, monitorDevice, StringComparison.OrdinalIgnoreCase));
        if (existing is not null) existing.Path = path;
        else Wallpapers.Add(new WallpaperAssignment { Monitor = monitorDevice, Path = path });
    }
}