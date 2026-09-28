using System.Runtime.InteropServices;

namespace Maktaby.Native;

public static class UxTheme
{
    [DllImport("uxtheme.dll", CharSet = CharSet.Unicode)]
    public static extern int SetWindowTheme(IntPtr hwnd, string? pszSubAppName, string? pszSubIdList);

    [DllImport("uxtheme.dll", EntryPoint = "#135", CharSet = CharSet.Unicode)]
    public static extern int SetPreferredAppMode(int preferredAppMode);

    [DllImport("uxtheme.dll", EntryPoint = "#136", CharSet = CharSet.Unicode)]
    public static extern int FlushMenuThemes();
}
