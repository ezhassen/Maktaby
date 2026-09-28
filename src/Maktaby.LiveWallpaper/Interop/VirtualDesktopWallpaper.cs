using Microsoft.Win32;

namespace Maktaby.LiveWallpaper.Interop;

/// <summary>Windows 11 gives each virtual desktop its own wallpaper, stored under
/// HKCU\...\Explorer\VirtualDesktops\Desktops\{guid}\Wallpaper. During a desktop-switch
/// slide animation, DWM paints the target desktop's static wallpaper before our live layer
/// composites there. Pointing every desktop's wallpaper at our captured frame makes that
/// transition paint a matching frame — no flash. Explorer reads the value when switching to
/// a desktop, so no forced refresh (which would destroy the WorkerW) is needed.
///
/// Windows never deletes the Desktops\{guid} key of a destroyed desktop, so the subkey
/// list accumulates orphans indefinitely (155+ seen in the wild with 2 live). All three
/// operations below therefore scope to currently-live desktops via the VirtualDesktopIDs
/// value, falling back to all subkeys only when the live list is unreadable.</summary>
public static class VirtualDesktopWallpaper
{
    private const string DesktopsKey =
        @"Software\Microsoft\Windows\CurrentVersion\Explorer\VirtualDesktops\Desktops";
    private const string VirtualDesktopsKey =
        @"Software\Microsoft\Windows\CurrentVersion\Explorer\VirtualDesktops";
    private const string LiveIdsValue = "VirtualDesktopIDs";
    private const string WallpaperValue = "Wallpaper";

    /// <summary>GUIDs (braced, case-insensitive) of currently-live virtual desktops, or null
    /// when unreadable/empty — meaning "unknown", for which callers keep the old all-subkeys
    /// behavior rather than skipping everything mid shell-restart.</summary>
    private static HashSet<string>? LiveDesktopIds()
    {
        try
        {
            using var vd = Registry.CurrentUser.OpenSubKey(VirtualDesktopsKey);
            if (vd?.GetValue(LiveIdsValue) is not byte[] raw || raw.Length < 16)
                return null;
            var live = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i + 16 <= raw.Length; i += 16)
                live.Add(new Guid(new ReadOnlySpan<byte>(raw, i, 16)).ToString("B"));
            return live.Count == 0 ? null : live;
        }
        catch { return null; }
    }

    /// <summary>Current per-desktop wallpaper paths, keyed by desktop GUID. Skips orphaned
    /// keys of long-destroyed desktops (see class note).</summary>
    public static Dictionary<string, string> ReadAll()
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        using var desktops = Registry.CurrentUser.OpenSubKey(DesktopsKey);
        if (desktops is null) return result;
        var live = LiveDesktopIds();
        foreach (var guid in desktops.GetSubKeyNames())
        {
            if (live is not null && !live.Contains(guid)) continue;
            using var d = desktops.OpenSubKey(guid);
            if (d?.GetValue(WallpaperValue) is string path)
                result[guid] = path;
        }
        return result;
    }

    /// <summary>Points every live virtual desktop's wallpaper at <paramref name="imagePath"/>.
    /// Writes only keys that actually differ: a steady-state apply then costs reads only —
    /// no registry churn, no key-timestamp poisoning, no Explorer re-reads.</summary>
    public static void SetAll(string imagePath)
    {
        if (string.IsNullOrEmpty(imagePath)) return;
        using var desktops = Registry.CurrentUser.OpenSubKey(DesktopsKey, writable: true);
        if (desktops is null) return;
        var live = LiveDesktopIds();
        foreach (var guid in desktops.GetSubKeyNames())
        {
            if (live is not null && !live.Contains(guid)) continue;
            using var d = desktops.OpenSubKey(guid, writable: true);
            if (d is null) continue;
            if (d.GetValue(WallpaperValue) is string current
                && string.Equals(current, imagePath, StringComparison.OrdinalIgnoreCase))
                continue;
            d.SetValue(WallpaperValue, imagePath, RegistryValueKind.String);
        }
    }

    /// <summary>Restores previously-saved per-desktop wallpaper paths (diffed like
    /// <see cref="SetAll"/>: keys already holding the saved value are left alone).</summary>
    public static void Restore(IReadOnlyDictionary<string, string> saved)
    {
        using var desktops = Registry.CurrentUser.OpenSubKey(DesktopsKey, writable: true);
        if (desktops is null) return;
        foreach (var (guid, path) in saved)
        {
            if (string.IsNullOrEmpty(path)) continue;
            using var d = desktops.OpenSubKey(guid, writable: true);
            if (d is null) continue;
            if (d.GetValue(WallpaperValue) is string current
                && string.Equals(current, path, StringComparison.OrdinalIgnoreCase))
                continue;
            d.SetValue(WallpaperValue, path, RegistryValueKind.String);
        }
    }
}
