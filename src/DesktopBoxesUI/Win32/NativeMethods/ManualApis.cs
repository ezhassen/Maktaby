using System.Runtime.InteropServices;
using System.Text;

namespace DesktopBoxesUI.Win32.NativeMethods;

/// <summary>
/// Manual P/Invoke declarations for the few Win32 APIs that CsWin32 refuses to emit under the
/// AnyCPU WPF "temporary target assembly" build (architecture-specific APIs such as
/// SHGetFileInfo and SetWinEventHook). Kept in the single sanctioned NativeMethods location so no
/// raw P/Invoke is scattered through the codebase. Everything still flows through the
/// <see cref="Win32Apis"/> wrapper.
/// </summary>
internal static class ManualApis
{
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

    [DllImport("user32.dll")]
    public static extern IntPtr SetWinEventHook(
        uint eventMin,
        uint eventMax,
        IntPtr hmodWinEventProc,
        WinEventProc pfnWinEventProc,
        uint idProcess,
        uint idThread,
        uint dwFlags);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool UnhookWinEvent(IntPtr hWinEventHook);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern bool Shell_NotifyIcon(uint dwMessage, ref NOTIFYICONDATAW lpdata);

    [DllImport("user32.dll")]
    public static extern short GetAsyncKeyState(int vKey);

    [DllImport("shlwapi.dll")]
    public static extern IntPtr ILCombine(IntPtr pidl1, IntPtr pidl2);

    [DllImport("shlwapi.dll")]
    public static extern void ILFree(IntPtr pidl);

    [DllImport("shell32.dll")]
    public static extern int SHGetSpecialFolderLocation(IntPtr hwndOwner, int csidl, out IntPtr ppidl);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    public static extern int SHGetPathFromIDListW(IntPtr pidl, StringBuilder pszPath);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern bool ShellExecuteEx(ref SHELLEXECUTEINFO lpExecInfo);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongW")]
    public static extern int GetWindowLong(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongW")]
    public static extern int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")]
    public static extern IntPtr SetWindowLongPtr(IntPtr hWnd, int nIndex, IntPtr dwNewLong);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int X, int Y, int cx, int cy, uint uFlags);

    public const int GWL_EXSTYLE = -20;
    public const int WS_EX_TOOLWINDOW = 0x00000080;

    public const uint SWP_NOMOVE = 0x0002;
    public const uint SWP_NOSIZE = 0x0001;
    public const uint SWP_NOZORDER = 0x0004;
    public const uint SWP_FRAMECHANGED = 0x0020;
    public const uint SWP_NOACTIVATE = 0x0010;

    public static readonly IntPtr HWND_TOP = new(0);
    public static readonly IntPtr HWND_BOTTOM = new(1);

    [DllImport("user32.dll")]
    public static extern IntPtr GetParent(IntPtr hWnd);

    [DllImport("user32.dll", CharSet = CharSet.Auto, SetLastError = true)]
    public static extern int GetClassName(IntPtr hWnd, StringBuilder lpClassName, int nMaxCount);

    [DllImport("user32.dll")]
    public static extern IntPtr SetParent(IntPtr hWndChild, IntPtr hWndNewParent);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool PostMessage(IntPtr hWnd, uint Msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool GetCursorPos(out POINT lpPoint);

    [DllImport("user32.dll")]
    public static extern IntPtr CreatePopupMenu();

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool DestroyMenu(IntPtr hMenu);

    [DllImport("user32.dll")]
    public static extern int TrackPopupMenuEx(IntPtr hmenu, uint fuFlags, int x, int y, IntPtr hwnd, IntPtr lptpm);

    public const uint TPM_RETURNCMD = 0x0100;
    public const uint TPM_RIGHTBUTTON = 0x0002;

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    public static extern IntPtr FindWindowEx(IntPtr hWndParent, IntPtr hWndChildAfter, string lpszClass, string? lpszWindow);

    public delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern IntPtr SendMessageTimeout(
        IntPtr hWnd,
        uint Msg,
        IntPtr wParam,
        IntPtr lParam,
        uint fuFlags,
        uint uTimeout,
        out IntPtr lpdwResult);

    public const int GWL_STYLE = -16;
    public const int GWL_HWNDPARENT = -8;
    public const int WS_MAXIMIZEBOX = 0x00010000;
    public const int WS_MINIMIZEBOX = 0x00020000;
    public const int WsPopup = unchecked((int)0x80000000);
    public const int WsChild = 0x40000000;

    [StructLayout(LayoutKind.Sequential)]
    public struct POINT
    {
        public int X;
        public int Y;
    }
}

/// <summary>Flags for <see cref="ManualApis.ShellExecuteEx"/>.</summary>
[Flags]
internal enum SEE_MASK : uint
{
    IDLIST = 0x00000004,
    NO_UI = 0x00000400,
}

/// <summary>Used by <see cref="ManualApis.ShellExecuteEx"/> to launch a shell item by PIDL.</summary>
[StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
internal struct SHELLEXECUTEINFO
{
    public int cbSize;
    public SEE_MASK fMask;
    public IntPtr hwnd;
    public string? lpVerb;
    public string? lpFile;
    public string? lpParameters;
    public string? lpDirectory;
    public int nShow;
    public IntPtr hInstApp;
    public IntPtr lpIDList;
    public string? lpClass;
    public IntPtr hkeyClass;
    public uint dwHotKey;
    public IntPtr hIconOrMonitor;
    public IntPtr hProcess;
}

/// <summary>NOTIFYICONDATAW used by <see cref="ManualApis.Shell_NotifyIcon"/> (declared manually because
/// CsWin32 refuses to emit it under the AnyCPU WPF "temporary target assembly" build).</summary>
[StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
internal struct NOTIFYICONDATAW
{
    public uint cbSize;
    public IntPtr hWnd;
    public uint uID;
    public uint uFlags;
    public uint uCallbackMessage;
    public IntPtr hIcon;
    [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
    public string szTip;
    public uint dwState;
    public uint dwStateMask;
    [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)]
    public string szInfo;
    public uint uTimeoutOrVersion;
    [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)]
    public string szInfoTitle;
    public uint dwInfoFlags;
    public Guid guidItem;
    public IntPtr hBalloonIcon;
}

/// <summary>SHFILEINFO structure used by <see cref="ManualApis.SHGetFileInfo"/>.</summary>
[StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
internal struct SHFILEINFOW
{
    public IntPtr hIcon;
    public int iIcon;
    public uint dwAttributes;

    [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
    public string szDisplayName;

    [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 80)]
    public string szTypeName;
}

/// <summary>Flags for <see cref="ManualApis.SHGetFileInfo"/>.</summary>
[Flags]
internal enum SHGFI : uint
{
    Icon = 0x00000100,
    SmallIcon = 0x00000001,
    LargeIcon = 0x00000000,
    UseFileAttributes = 0x00000010,
    AddOverlays = 0x00000020,
    OpenIcon = 0x00000002,
    Pidl = 0x00000008,
}

/// <summary>Callback for <see cref="ManualApis.SetWinEventHook"/>.</summary>
internal delegate void WinEventProc(
    IntPtr hWinEventHook,
    uint eventType,
    IntPtr hwnd,
    int idObject,
    int idChild,
    uint idEventThread,
    uint dwmsEventTime);
