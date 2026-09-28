using System.Runtime.InteropServices;

namespace Maktaby.Native;

public static class ComDlg32
{
    [DllImport("comdlg32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern bool GetOpenFileNameW(ref OPENFILENAME ofn);

    public const uint OFN_FILEMUSTEXIST = 0x00001000;
    public const uint OFN_PATHMUSTEXIST = 0x00000800;
    public const uint OFN_NOCHANGEDIR = 0x00000008;
    public const uint OFN_EXPLORER = 0x00080000;
    public const uint OFN_ENABLESIZING = 0x00800000;
}
