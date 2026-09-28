using Microsoft.Win32;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using Maktaby.Native;

namespace Maktaby.Win32APIs.Services;

/// <summary>
/// Manages "launch on Windows startup" via HKCU\Software\Microsoft\Windows\CurrentVersion\Run.
///
/// The Run-key value is the persistence surface used by both the app's Settings toggle and the
/// installer (see installer.iss → [Run] entry that launches the app with --enable-startup as the
/// original user, so the write always lands in the correct user's hive regardless of installer
/// elevation). The installer's job is only to offer the option; the app owns the actual write.
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

    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";

    /// <summary>True when our Run-key value exists and points at this executable.</summary>
    public static bool IsEnabled
    {
        get
        {
            try
            {
                string? exe = Process.GetCurrentProcess().MainModule?.FileName;
                if (string.IsNullOrEmpty(exe)) return false;

                using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, false);;
                if (key is null) return false;

                object? value = key.GetValue(AppName);
                if (value is string s)
                {
                    return string.Equals(
                        s.Trim('"').Trim(),
                        exe,
                        StringComparison.OrdinalIgnoreCase);
                }
                return false;
            }
            catch (Exception ex)
            {
                ex.Log_Error();
                return false;
            }
        }
    }

    /// <summary>Writes the Run-key value for the current executable.</summary>
    public static void Enable()
    {
        string? exe = Process.GetCurrentProcess().MainModule?.FileName;
        if (string.IsNullOrEmpty(exe)) return;

        // Quote the path in case it contains spaces. The Run key treats the value as a command line.
        string value = $"\"{exe}\"";

        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, true);
            key?.SetValue(AppName, value);
        }
        catch (Exception ex)
        {
            ex.Log_Error();
        }
    }

    /// <summary>Removes the Run-key value.</summary>
    public static void Disable()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, true);
            key?.DeleteValue(AppName, false);
        }
        catch (Exception ex)
        {
            ex.Log_Error();
        }
    }

    //========================================================================
    // OLD Shortcut-based implementation (ShellLink in Startup folder).
    // Replaced by the registry approach above on user request.
    //========================================================================
    /*
    private static string LinkPath =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Startup), AppName + ".lnk");

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
    */
}
