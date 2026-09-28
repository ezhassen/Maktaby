using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using Maktaby.Native;

namespace Maktaby.Win32APIs.Services;

/// <summary>
/// Manages "launch on Windows startup" via a shortcut in the per-user Startup folder
/// (%APPDATA%\Microsoft\Windows\Start Menu\Programs\Startup).
///
/// Previously this wrote HKCU\Software\Microsoft\Windows\CurrentVersion\Run directly, and
/// behavioral heuristics flag exactly that (an unsigned process registering autorun) — enabling
/// the toggle tripped PDM:Trojan.Win32.Generic lockdowns. A Startup-folder shortcut is the
/// Explorer-managed, user-visible equivalent and needs no registry writes at all — not even
/// a legacy cleanup: any Run-key access keeps the flagged behavior signature.
///
/// This is the system source of truth for the toggle; the user's preference is also mirrored in
/// <see cref="Settings.AppJSettings.LaunchOnStartup"/>.
/// </summary>
public static class StartupManager
{
#if DEBUG
    private const string AppName = "Maktaby_Debug";
#else
    private const string AppName = "Maktaby";
#endif

    private static string LinkPath =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Startup), AppName + ".lnk");

    /// <summary>True when our Startup shortcut exists and points at this executable.</summary>
    public static bool IsEnabled
    {
        get
        {
            try
            {
                string link = LinkPath;
                if (!File.Exists(link)) return false;
                string? target = ResolveLinkTarget(link);
                if (target is null) return false;
                var exe = Process.GetCurrentProcess().MainModule?.FileName;
                if (string.IsNullOrEmpty(exe)) return false;
                return string.Equals(target, exe, StringComparison.OrdinalIgnoreCase);
            }
            catch (Exception ex)
            {
                ex.Log_Error();
                return false;
            }
        }
    }

    /// <summary>Creates the Startup shortcut for the current executable.</summary>
    public static void Enable()
    {
        var exe = Process.GetCurrentProcess().MainModule?.FileName;
        if (string.IsNullOrEmpty(exe))
        {
            return;
        }

        try
        {
            CreateLink(LinkPath, exe);
        }
        catch (Exception ex)
        {
            ex.Log_Error();
            // Best-effort: a locked-down profile may reject the write; the toggle simply won't persist.
        }
    }

    /// <summary>Removes the Startup shortcut.</summary>
    public static void Disable()
    {
        try
        {
            string link = LinkPath;
            if (File.Exists(link)) File.Delete(link);
        }
        catch (Exception ex)
        {
            ex.Log_Error();
            // Best-effort.
        }
    }

    private static void CreateLink(string linkPath, string exe)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(linkPath)!);
        object? shellLink = null;
        try
        {
            shellLink = new ShellLink();
            var link = (IShellLinkW)shellLink;
            link.SetPath(exe);
            try { link.SetWorkingDirectory(Path.GetDirectoryName(exe)!); } catch { }
            try { link.SetDescription("Maktaby - Desktop Organizer"); } catch { }
            var persist = (IPersistFile)shellLink;
            int hr = persist.Save(linkPath, false);
            if (hr != 0) Marshal.ThrowExceptionForHR(hr);
        }
        finally
        {
            // Deterministic COM release (one RCW, two interface references).
            if (shellLink is not null) Marshal.ReleaseComObject(shellLink);
        }
    }

    private static string? ResolveLinkTarget(string linkPath)
    {
        object? shellLink = null;
        try
        {
            shellLink = new ShellLink();
            var persist = (IPersistFile)shellLink;
            int hr = persist.Load(linkPath, 0);
            if (hr != 0) return null;
            var link = (IShellLinkW)shellLink;
            var sb = new StringBuilder(260);
            try
            {
                link.GetPath(sb, sb.Capacity, IntPtr.Zero, 0);
            }
            catch
            {
                return null;
            }
            string target = sb.ToString();
            return string.IsNullOrEmpty(target) ? null : target;
        }
        finally
        {
            if (shellLink is not null) Marshal.ReleaseComObject(shellLink);
        }
    }
}
