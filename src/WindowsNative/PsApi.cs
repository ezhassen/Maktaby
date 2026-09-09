using System.Runtime.InteropServices;

namespace WindowsNative;

public static class PsApi
{
    [DllImport("psapi.dll")]
    public static extern int EmptyWorkingSet(IntPtr hProcess);
}
