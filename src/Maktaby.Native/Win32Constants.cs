namespace Maktaby.Native;

/// <summary>Canonical Win32 constants shared by all consumers. Integral types match the native
/// API parameter types (styles/messages are uint; indices that feed int APIs stay int).</summary>
public static class Win32Constants
{
    // Window styles
    public const uint WS_POPUP = 0x80000000;
    public const uint WS_CHILD = 0x40000000;
    public const uint WS_VISIBLE = 0x10000000;
    public const uint WS_DISABLED = 0x08000000;
    public const uint WS_CLIPCHILDREN = 0x02000000;
    public const uint WS_CLIPSIBLINGS = 0x04000000;
    public const int WS_MAXIMIZEBOX = 0x00010000;
    public const int WS_MINIMIZEBOX = 0x00020000;

    // Extended styles
    public const uint WS_EX_LAYERED = 0x00080000;
    public const uint WS_EX_TRANSPARENT = 0x00000020;
    public const uint WS_EX_TOOLWINDOW = 0x00000080;
    public const uint WS_EX_NOACTIVATE = 0x08000000;
    public const uint WS_EX_NOREDIRECTIONBITMAP = 0x00200000;
    public const uint WS_EX_TOPMOST = 0x00000008;

    public const int GWL_STYLE = -16;
    public const int GWL_EXSTYLE = -20;
    public const int GWL_HWNDPARENT = -8;
    public const int GCL_STYLE = -26;
    public const int CS_DBLCLKS = 0x0008;

    // SetWindowPos handles
    public static readonly IntPtr HWND_TOP = IntPtr.Zero;
    public static readonly IntPtr HWND_BOTTOM = new(1);
    public static readonly IntPtr HWND_MESSAGE = new(-3);
    public static readonly IntPtr HWND_BROADCAST = new(0xFFFF);

    // SetWindowPos flags
    public const uint SWP_NOSIZE = 0x0001;
    public const uint SWP_NOMOVE = 0x0002;
    public const uint SWP_NOZORDER = 0x0004;
    public const uint SWP_NOACTIVATE = 0x0010;
    public const uint SWP_SHOWWINDOW = 0x0040;
    public const uint SWP_FRAMECHANGED = 0x0020;

    public const int SW_HIDE = 0;
    public const int SW_SHOWNOACTIVATE = 4;
    public const int SW_PARENTCLOSING = 1;

    // GetWindow commands
    public const uint GW_HWNDFIRST = 0;
    public const uint GW_HWNDNEXT = 2;
    public const uint GW_HWNDPREV = 3;
    public const uint GW_OWNER = 4;
    public const uint GW_CHILD = 5;

    // Messages
    public const uint WM_QUIT = 0x0012;
    public const uint WM_DESTROY = 0x0002;
    public const uint WM_PAINT = 0x000F;
    public const uint WM_CLOSE = 0x0010;
    public const uint WM_ERASEBKGND = 0x0014;
    public const uint WM_TIMER = 0x0113;
    public const uint WM_DISPLAYCHANGE = 0x007E;
    public const uint WM_COMMAND = 0x0111;
    public const uint WM_LBUTTONUP = 0x0202;
    public const int WM_LBUTTONDOWN = 0x0201;
    public const int WM_RBUTTONDOWN = 0x0204;
    public const int WM_MBUTTONDOWN = 0x0207;
    public const int WM_XBUTTONDOWN = 0x020B;
    public const uint WM_LBUTTONDBLCLK = 0x0203;
    public const uint WM_RBUTTONUP = 0x0205;
    public const uint WM_CONTEXTMENU = 0x007B;
    public const uint WM_USER = 0x0400;
    public const uint WM_SHOWWINDOW = 0x0018;
    public const uint WM_SYSCOMMAND = 0x0112;
    public const uint SC_MINIMIZE = 0xF020;
    public const uint WM_WTSSESSION_CHANGE = 0x02B1;
    public const uint WM_POWERBROADCAST = 0x0218;
    public const uint WM_APP = 0x8000;
    public const uint WM_SETTINGCHANGE = 0x001A;
    // Theme-related broadcasts. The light/dark ("default Windows mode") switch arrives as
    // WM_SETTINGCHANGE with lParam "ImmersiveColorSet" — it has no SPI_* wParam — and
    // WM_THEMECHANGED / WM_SYSCOLORCHANGE accompany accent + high-contrast changes. All
    // three are what Wpf.Ui's own SystemThemeWatcher listens to, so the app-level theme
    // follower watches the same set. See Helpers/SystemThemeFollower.
    public const uint WM_THEMECHANGED = 0x031A;
    public const uint WM_SYSCOLORCHANGE = 0x0315;

    public const int WTS_SESSION_LOCK = 0x7;
    public const int WTS_SESSION_UNLOCK = 0x8;
    public const int PBT_APMRESUMEAUTOMATIC = 0x12;
    public const int PBT_POWERSETTINGCHANGE = 0x8013;

    // SendMessageTimeout
    public const uint SMTO_NORMAL = 0x0000;

    // SystemParametersInfo
    public const uint SPI_SETDESKWALLPAPER = 0x0014;
    public const uint SPIF_UPDATEINIFILE = 0x0001;

    // WinEvents
    public const uint EVENT_SYSTEM_FOREGROUND = 0x0003;
    public const uint EVENT_SYSTEM_MOVESIZEEND = 0x000B;
    public const uint EVENT_SYSTEM_MINIMIZESTART = 0x0016;
    public const uint EVENT_SYSTEM_MINIMIZEEND = 0x0017;
    public const uint EVENT_OBJECT_LOCATIONCHANGE = 0x800B;
    public const uint EVENT_OBJECT_DESTROY = 0x8001;
    public const uint EVENT_OBJECT_SHOW = 0x8002;
    public const uint EVENT_OBJECT_HIDE = 0x8003;
    public const uint WINEVENT_OUTOFCONTEXT = 0x0000;
    public const uint WINEVENT_SKIPOWNPROCESS = 0x0002;
    public const int OBJID_WINDOW = 0;

    // Hooks
    public const int WH_MOUSE_LL = 14;

    // Monitors
    public const uint MONITOR_DEFAULTTOPRIMARY = 1;
    public const uint MONITOR_DEFAULTTONEAREST = 2;
    public const uint MONITORINFOF_PRIMARY = 1;
    public const int SM_REMOTESESSION = 0x1000;
    public const int SM_XVIRTUALSCREEN = 76;
    public const int SM_YVIRTUALSCREEN = 77;
    public const int SM_CXVIRTUALSCREEN = 78;
    public const int SM_CYVIRTUALSCREEN = 79;
    public const int SM_CXDOUBLECLK = 36;
    public const int SM_CYDOUBLECLK = 37;
    public const int SM_CXICON = 11;
    public const int MDT_EFFECTIVE_DPI = 0;

    // DWM window attributes (dwmapi)
    public const uint DWMWA_CLOAKED = 14;
    public const int DWMWA_USE_IMMERSIVE_DARK_MODE = 20;

    // Menus
    public const uint MF_STRING = 0x0000;
    public const uint MF_SEPARATOR = 0x0800;
    public const uint MF_CHECKED = 0x0008;
    public const uint MF_POPUP = 0x0010;
    public const uint MF_GRAYED = 0x0001;
    public const uint TPM_RIGHTBUTTON = 0x0002;
    public const uint TPM_RETURNCMD = 0x0100;

    // Layered windows
    public const uint LWA_ALPHA = 0x0002;
    public const uint ULW_ALPHA = 0x0002;
    public const byte AC_SRC_OVER = 0x00;
    public const byte AC_SRC_ALPHA = 0x01;
    public const uint TME_LEAVE = 0x00000002;

    // Tray (Shell_NotifyIcon)
    public const uint NIM_ADD = 0x0000;
    public const uint NIM_MODIFY = 0x0001;
    public const uint NIM_DELETE = 0x0002;
    public const uint NIM_SETVERSION = 0x0004;
    public const uint NIF_MESSAGE = 0x0001;
    public const uint NIF_ICON = 0x0002;
    public const uint NIF_TIP = 0x0004;
    public const uint NOTIFYICON_VERSION_4 = 4;

    // Shell file operations
    public const uint FO_MOVE = 0x0001;
    public const uint FO_COPY = 0x0002;
    public const uint FO_DELETE = 0x0003;
    public const ushort FOF_ALLOWUNDO = 0x0040;
    public const ushort FOF_NOCONFIRMATION = 0x0010;
    public const ushort FOF_SILENT = 0x0004;
    public const ushort FOF_NOERRORUI = 0x0400;
    public const ushort FOF_WANTNUKEWARNING = 0x4000;

    // Process access
    public const int PROCESS_VM_OPERATION = 0x0008;
    public const int PROCESS_VM_READ = 0x0010;
    public const int PROCESS_VM_WRITE = 0x0020;
    public const uint MEM_COMMIT = 0x1000;
    public const uint MEM_RESERVE = 0x2000;
    public const uint MEM_RELEASE = 0x8000;
    public const uint PAGE_READWRITE = 0x04;
    public const uint PAGE_GUARD = 0x100;

    // List-view
    public const int LVM_FIRST = 0x1000;
    public const int LVM_HITTEST = LVM_FIRST + 18;
    public const int LVM_GETIMAGELIST = LVM_FIRST + 2;
    public const int LVSIL_NORMAL = 0;
    public const int LVM_GETITEMCOUNT = LVM_FIRST + 4;
    public const int LVM_GETITEMRECT = LVM_FIRST + 14;
    public const int LVIR_ICON = 1;
    public const uint LVHT_NOWHERE = 0x0001;

    // Keyboard
    public const uint KEYEVENTF_KEYUP = 0x0002;
    public const byte VK_ESCAPE = 0x1B;

    // GetGuiResources flags (handle-leak forensics)
    public const uint GR_GDIOBJECTS = 0;
    public const uint GR_USEROBJECTS = 1;

    // Toolhelp32 process snapshot
    public const uint TH32CS_SNAPPROCESS = 0x00000002;
}
