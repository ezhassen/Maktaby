using System.Runtime.InteropServices;

namespace Maktaby.Native;

public static class DwmApi
{
    [DllImport("dwmapi.dll", PreserveSig = true)]
    public static extern int DwmGetWindowAttribute(IntPtr hwnd, uint dwAttribute, out int pvAttribute, int cbAttribute);

    [DllImport("dwmapi.dll")]
    public static extern int DwmSetWindowAttribute(IntPtr hwnd, int dwAttribute, ref int pvAttribute, int cbAttribute);
}
