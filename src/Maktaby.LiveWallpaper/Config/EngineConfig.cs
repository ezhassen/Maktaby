using System.Text.Json;
using System.Text.Json.Serialization;

namespace Maktaby.LiveWallpaper.Config;

public enum FitMode { Fill, Fit, Stretch }

public sealed class WallpaperAssignment
{
    /// <summary>Monitor device name (e.g. \.\DISPLAY1); "*" applies to all monitors.</summary>
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
    /// Get the wallpaper path for a specific monitor.
    /// Returns the per-monitor assignment if set, otherwise falls back to the "*" (all monitors) assignment.
    /// </summary>
    public string? WallpaperFor(string monitorDevice)
    {
        var exact = Wallpapers.FirstOrDefault(w => string.Equals(w.Monitor, monitorDevice, StringComparison.OrdinalIgnoreCase));
        var star = Wallpapers.FirstOrDefault(w => w.Monitor == "*");
        var path = (exact ?? star)?.Path;
        return string.IsNullOrWhiteSpace(path) ? null : path;
    }

    /// <summary>
    /// Assign a wallpaper to a monitor.
    /// If monitorDevice is "*", sets the default for all monitors.
    /// If a specific monitor device name, sets wallpaper only for that monitor.
    /// </summary>
    /// <param name="monitorDevice">target monitor. * for all, or a specific monitor device name.</param>
    /// <param name="path">wallpaper file path (empty string to clear).</param>
    public void Assign(string monitorDevice, string path)
    {
        if (string.IsNullOrWhiteSpace(monitorDevice)) return;
        var existing = Wallpapers.FirstOrDefault(w => string.Equals(w.Monitor, monitorDevice, StringComparison.OrdinalIgnoreCase));
        if (existing is not null)
        {
            existing.Path = path;
            // Remove any per-monitor entry that matches when clearing
            if (string.IsNullOrWhiteSpace(path) && existing.Monitor != "*")
                Wallpapers.Remove(existing);
        }
        else if (!string.IsNullOrWhiteSpace(path))
        {
            Wallpapers.Add(new WallpaperAssignment { Monitor = monitorDevice, Path = path });
        }
        // If assigning "" to "*", just leave the star entry as-is (it's the default)
    }
}
