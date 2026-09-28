using System.Runtime.InteropServices;

namespace Maktaby.Native;

public static class WtsApi32
{
    [DllImport("wtsapi32.dll", SetLastError = true)]
    public static extern bool WTSRegisterSessionNotification(IntPtr hwnd, uint flags);

    [DllImport("wtsapi32.dll")]
    public static extern bool WTSUnRegisterSessionNotification(IntPtr hwnd);

    public const uint NOTIFY_FOR_THIS_SESSION = 0;
}
