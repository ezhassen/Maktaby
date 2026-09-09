using System.Runtime.InteropServices;

namespace WindowsNative;

public static class Gdi32
{
    [DllImport("gdi32.dll", SetLastError = true)]
    public static extern IntPtr CreateCompatibleDC(IntPtr dc);

    [DllImport("gdi32.dll")]
    public static extern bool DeleteDC(IntPtr dc);

    [DllImport("gdi32.dll")]
    public static extern IntPtr SelectObject(IntPtr dc, IntPtr obj);

    [DllImport("gdi32.dll")]
    public static extern bool DeleteObject(IntPtr obj);

    [DllImport("gdi32.dll")]
    public static extern bool BitBlt(IntPtr dstDc, int x, int y, int cx, int cy, IntPtr srcDc, int x1, int y1, uint rop);

    public const uint SRCCOPY = 0x00CC0020;
}
