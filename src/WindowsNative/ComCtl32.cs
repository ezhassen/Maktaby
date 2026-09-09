using System.Runtime.InteropServices;

namespace WindowsNative;

public static class ComCtl32
{
    [DllImport("comctl32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool ImageList_GetIconSize(IntPtr himl, ref int cx, ref int cy);
}
