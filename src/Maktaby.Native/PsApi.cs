using System.Runtime.InteropServices;

namespace Maktaby.Native;

public static class PsApi
{
    [DllImport("psapi.dll")]
    public static extern int EmptyWorkingSet(IntPtr hProcess);

    [DllImport("psapi.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool GetProcessMemoryInfo(IntPtr hProcess, out PROCESS_MEMORY_COUNTERS_EX2 ppsmemCounters, uint cb);
}
