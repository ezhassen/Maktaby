using Microsoft.Win32;
using System.Diagnostics;

namespace DesktopBoxesUI.Win32APIs.Services;

/// <summary>
/// Manages the "launch on Windows startup" registry entry (HKCU\Software\Microsoft\Windows\CurrentVersion\Run).
/// This is the system source of truth for the toggle; the user's preference is also mirrored in
/// <see cref="Settings.AppJSettings.LaunchOnStartup"/>.
/// </summary>
public static class StartupManager
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
#if DEBUG
    private const string AppName = "DesktopBoxes_Debug";
#else
    private const string AppName = "DesktopBoxes";
#endif

    //TODO: Checks should also check app exe path

    /// <summary>True when the Run-key entry for this app exists.</summary>
    public static bool IsEnabled
    {
        get
        {
            try
            {
                using var key = Registry.CurrentUser.OpenSubKey(RunKey);
                var savedVal = key?.GetValue(AppName);
                if (savedVal is not string savedExePath) return false;
                var exe = Process.GetCurrentProcess().MainModule?.FileName;
                if (string.IsNullOrEmpty(exe)) return false;
                return savedExePath == exe;
            }
            catch (Exception ex)
            {
                ex.Log_Error();
                return false;
            }
        }
    }

    /// <summary>Registers the current executable in the Run key (quoted path).</summary>
    public static void Enable()
    {
        var exe = Process.GetCurrentProcess().MainModule?.FileName;
        if (string.IsNullOrEmpty(exe))
        {
            return;
        }

        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(RunKey);
            key.SetValue(AppName, $"\"{exe}\"");
        }
        catch (Exception ex)
        {
            ex.Log_Error();
            // Best-effort: a locked-down registry may reject the write; the toggle simply won't persist.
        }
    }

    /// <summary>Removes the Run-key entry.</summary>
    public static void Disable()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey, writable: true);
            key?.DeleteValue(AppName, false);
        }
        catch (Exception ex)
        {
            ex.Log_Error();
            // Best-effort.
        }
    }
}
