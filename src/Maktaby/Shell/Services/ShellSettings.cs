using Microsoft.Win32;

namespace Maktaby.Shell.Services;

/// <summary>
/// Reads Explorer shell settings (Base Class Library only) so the app can match the user's
/// "single-click vs double-click to open" preference.
/// </summary>
public static class ShellSettings
{
    /// <summary>True when Explorer is configured for single-click-to-open an item.</summary>
    public static bool IsSingleClickToOpen()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Explorer");
            if (key?.GetValue("ShellState") is byte[] state && state.Length > 4)
            {
                return (state[4] & 0x80) != 0;
            }
        }
        catch
        {
            // ignore; default to double-click
        }

        return false;
    }
}
