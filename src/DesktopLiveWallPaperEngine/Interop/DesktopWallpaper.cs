using System.Runtime.InteropServices;
using System.Text;
using WindowsNative;
using static WindowsNative.Win32Constants;

namespace DesktopLiveWallPaperEngine.Interop;

/// <summary>Reads and writes the OS static desktop wallpaper. We set it to a frame of the
/// live wallpaper so that whatever Windows paints during a virtual-desktop switch (or Task
/// View) already matches — no flash of the previous wallpaper before our live layer returns.
/// Setting an actual path is safe on the 24H2 raised desktop (only the null-refresh form of
/// SPI_SETDESKWALLPAPER destroys the WorkerW).</summary>
public static class DesktopWallpaper
{
    private const uint SPI_GETDESKWALLPAPER = 0x0073;
    private const uint SPI_SETDESKWALLPAPER = 0x0014;
    private const uint SPIF_UPDATEINIFILE = 0x0001;

    /// <summary>Current primary wallpaper path (empty if none / a solid color).</summary>
    public static string GetCurrent()
    {
        var sb = new StringBuilder(520);
        return User32.SystemParametersInfoW(SPI_GETDESKWALLPAPER, (uint)sb.Capacity, sb, 0)
            ? sb.ToString()
            : "";
    }

    /// <summary>Whether the IDesktopWallpaper COM server exists on this machine. It is absent
    /// on some builds (REGDB_E_CLASSNOTREG on every activation — e.g. Win11 25H2 dev),
    /// where per-monitor SetWallpaper is impossible and SPI is the only path. Probed once
    /// per process; a missing server is an environmental fact, not a per-call error.</summary>
    private static readonly Lazy<bool> ComAvailable = new(() =>
    {
        try
        {
            _ = new DesktopWallpaperClass();
            return true;
        }
        catch (Exception ex) when (ex.HResult == unchecked((int)0x80040154))
        {
            Serilog.Log.Information("IDesktopWallpaper COM server is not registered on this build; per-monitor wallpaper falls back to SPI");
            return false;
        }
        catch (Exception ex)
        {
            Serilog.Log.Warning($"IDesktopWallpaper probe failed ({ex.Message}); per-monitor wallpaper falls back to SPI");
            return false;
        }
    });

    /// <summary>Sets the wallpaper for one monitor (by device name, e.g. \\.\DISPLAY1) via the
    /// modern per-monitor API, falling back to the single-wallpaper API.</summary>
    public static void SetForMonitor(string monitorDevice, RECT monitorBounds, string imagePath)
    {
        if (ComAvailable.Value)
        {
            try
            {
                var dw = (IDesktopWallpaper)new DesktopWallpaperClass();
                try { dw.SetPosition(DesktopWallpaperPosition.Fill); } catch { }

                string? monitorId = FindMonitorId(dw, monitorBounds);
                if (monitorId is not null)
                {
                    dw.SetWallpaper(monitorId, imagePath);
                    return;
                }
                dw.SetWallpaper(null, imagePath); // all monitors
                return;
            }
            catch (Exception ex)
            {
                Serilog.Log.Warning($"IDesktopWallpaper failed ({ex.Message}); using SPI");
            }
        }
        SetSingle(imagePath);
    }

    /// <summary>Sets a single wallpaper for all monitors (SystemParametersInfoW takes a raw
    /// pointer, so marshal and free the string ourselves).
    ///
    /// Deliberately omits SPIF_SENDCHANGE: that flag makes SystemParametersInfo broadcast
    /// WM_SETTINGCHANGE to every top-level window and wait for each one to answer, so a single
    /// app that is not pumping messages blocks the call indefinitely. Restore runs on the UI
    /// thread inside <see cref="Engine.Dispose"/>, so that hang left the app alive
    /// forever after --exit / tray Quit / logoff. The wallpaper still applies without the flag;
    /// other apps are notified asynchronously instead.</summary>
    public static void SetSingle(string imagePath)
    {
        if (string.IsNullOrEmpty(imagePath)) return;
        IntPtr p = Marshal.StringToHGlobalUni(imagePath);
        try
        {
            User32.SystemParametersInfoW(SPI_SETDESKWALLPAPER, 0, p, SPIF_UPDATEINIFILE);
        }
        finally
        {
            Marshal.FreeHGlobal(p);
        }

        // SendNotifyMessage returns immediately for other processes' windows.
        User32.SendNotifyMessageW(Win32Constants.HWND_BROADCAST, Win32Constants.WM_SETTINGCHANGE,
            (IntPtr)SPI_SETDESKWALLPAPER, IntPtr.Zero);
    }

    /// <summary>Restore a previously-saved wallpaper path.</summary>
    public static void RestoreCurrent(string savedPath) => SetSingle(savedPath);

    private static string? FindMonitorId(IDesktopWallpaper dw, RECT target)
    {
        uint count = dw.GetMonitorDevicePathCount();
        for (uint i = 0; i < count; i++)
        {
            string id = dw.GetMonitorDevicePathAt(i);
            if (string.IsNullOrEmpty(id)) continue;
            try
            {
                var r = dw.GetMonitorRECT(id);
                if (r.Left == target.Left && r.Top == target.Top && r.Right == target.Right && r.Bottom == target.Bottom)
                    return id;
            }
            catch { /* detached monitor id — skip */ }
        }
        return null;
    }
}
