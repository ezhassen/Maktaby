using System.Runtime.InteropServices;
using System.Text;

namespace WindowsNative;

public static class Shell32
{
    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    public static extern bool Shell_NotifyIconW(uint message, ref NOTIFYICONDATA data);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern bool Shell_NotifyIcon(uint dwMessage, ref NOTIFYICONDATA lpdata);

    [DllImport("shell32.dll")]
    public static extern int SHQueryUserNotificationState(out int state);

    // QUNS values
    public const int QUNS_RUNNING_D3D_FULL_SCREEN = 3;

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern int SHGetFileInfo(
        string? pszPath,
        uint dwFileAttributes,
        ref SHFILEINFOW psfi,
        uint cbFileInfo,
        uint uFlags);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, SetLastError = true, EntryPoint = "SHGetFileInfo")]
    public static extern int SHGetFileInfoPidl(
        IntPtr pidl,
        uint dwFileAttributes,
        ref SHFILEINFOW psfi,
        uint cbFileInfo,
        uint uFlags);

    [DllImport("shell32.dll")]
    public static extern IntPtr ILCombine(IntPtr pidl1, IntPtr pidl2);

    [DllImport("shell32.dll")]
    public static extern void ILFree(IntPtr pidl);

    [DllImport("shell32.dll")]
    public static extern int SHGetSpecialFolderLocation(IntPtr hwndOwner, int csidl, out IntPtr ppidl);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    public static extern int SHGetPathFromIDListW(IntPtr pidl, StringBuilder pszPath);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern bool ShellExecuteEx(ref SHELLEXECUTEINFO lpExecInfo);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool SHObjectProperties(IntPtr hwnd, int shopObject, string pszObject, string? pszPage);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern int SHFileOperationW(ref SHFILEOPSTRUCT lpFileOp);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern int Shell_NotifyIconGetRect(ref NOTIFYICONIDENTIFIER identifier, out RECT iconLocation);

    [DllImport("shell32.dll")]
    public static extern int SHGetDesktopFolder(out IShellFolder ppshf);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    public static extern int SHCreateItemFromIDList(IntPtr pidl, [MarshalAs(UnmanagedType.LPStruct)] Guid riid, out IShellItem ppv);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    public static extern int SHCreateItemFromParsingName(
        string pszPath,
        IntPtr pbc,
        [MarshalAs(UnmanagedType.LPStruct)] Guid riid,
        out IShellItem ppv);

    [DllImport("shell32.dll")]
    public static extern int SHGetIDListFromObject(IntPtr punk, out IntPtr ppidl);

    public static readonly Guid IID_IShellItem = new("43826D1E-E718-42EE-BC55-A1E261C37BFE");
    public static readonly Guid CLSID_ShellLink = new("00021401-0000-0000-C000-000000000046");
    public static readonly Guid IID_IContextMenu = new("000214e4-0000-0000-c000-000000000046");

    // --- Shell change notifications (desktop watcher) ---

    [DllImport("shell32.dll")]
    public static extern uint SHChangeNotifyRegister(
        IntPtr hwnd,
        SHCNRF fSources,
        SHCNE fEvents,
        uint wMsg,
        int cEntries,
        ref SHChangeNotifyEntry pshcne);

    [DllImport("shell32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool SHChangeNotifyDeregister(uint ulID);

    [DllImport("shell32.dll")]
    public static extern IntPtr SHChangeNotification_Lock(
        IntPtr hChange,
        uint dwProcessId,
        out IntPtr pppidl,
        out int lEvent);

    [DllImport("shell32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool SHChangeNotification_Unlock(IntPtr hLock);
}
