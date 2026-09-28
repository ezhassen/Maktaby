using System.Runtime.InteropServices;
using System.Text;

namespace Maktaby.Native;

/// <summary>Shared user32 P/Invoke. Hand-rolled DllImport only — the single canonical home for
/// every user32 call used by Maktaby, Maktaby.LiveWallpaper and Maktaby.Shared.</summary>
public static partial class User32
{
    // Window class / lifecycle
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern ushort RegisterClassExW(ref WNDCLASSEX wc);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern ushort RegisterClassEx(ref WNDCLASSEX wc);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern IntPtr CreateWindowExW(
        uint exStyle, string className, string? windowName, uint style,
        int x, int y, int width, int height,
        IntPtr parent, IntPtr menu, IntPtr instance, IntPtr param);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern IntPtr CreateWindowEx(
        uint dwExStyle,
        string? lpClassName,
        string? lpWindowName,
        uint dwStyle,
        int x,
        int y,
        int nWidth,
        int nHeight,
        IntPtr hWndParent,
        IntPtr hMenu,
        IntPtr hInstance,
        IntPtr lpParam);

    [DllImport("user32.dll")]
    public static extern IntPtr DefWindowProcW(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    public static extern IntPtr DefWindowProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern bool DestroyWindow(IntPtr hwnd);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern bool UnregisterClass(string className, IntPtr instance);

    [DllImport("user32.dll")]
    public static extern bool ShowWindow(IntPtr hwnd, int cmdShow);

    // Message loop
    [DllImport("user32.dll")]
    public static extern int GetMessageW(out MSG msg, IntPtr hwnd, uint filterMin, uint filterMax);

    [DllImport("user32.dll")]
    public static extern bool TranslateMessage(ref MSG msg);

    [DllImport("user32.dll")]
    public static extern IntPtr DispatchMessageW(ref MSG msg);

    [DllImport("user32.dll")]
    public static extern void PostQuitMessage(int exitCode);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    public static extern bool PostMessageW(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool PostMessage(IntPtr hWnd, uint Msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    public static extern bool PostThreadMessageW(uint threadId, uint msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    public static extern IntPtr SendMessageW(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern IntPtr SendMessage(IntPtr hWnd, uint Msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern int SendMessage(IntPtr hWnd, uint Msg, IntPtr wParam, ref LVHITTESTINFO lParam);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern IntPtr SendMessageTimeoutW(
        IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam,
        uint flags, uint timeoutMs, out IntPtr result);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern IntPtr SendMessageTimeout(
        IntPtr hWnd,
        uint Msg,
        IntPtr wParam,
        IntPtr lParam,
        uint fuFlags,
        uint uTimeout,
        out IntPtr lpdwResult);

    /// <summary>Fire-and-forget for cross-process targets — never blocks on a hung window.</summary>
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    public static extern bool SendNotifyMessageW(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam);

    /// <summary>Window DPI — logged on attach; DPI scaling is where wallpaper layers usually break.</summary>
    [DllImport("user32.dll")]
    public static extern uint GetDpiForWindow(IntPtr hwnd);

    [DllImport("user32.dll")]
    public static extern uint GetDpiForSystem();

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    public static extern uint RegisterWindowMessageW(string message);

    [DllImport("user32.dll", EntryPoint = "RegisterWindowMessageW", CharSet = CharSet.Unicode)]
    public static extern uint RegisterWindowMessage(string lpString);

    // Window discovery / hierarchy
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    public static extern IntPtr FindWindowW(string? className, string? windowName);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    public static extern IntPtr FindWindow(string? className, string? windowName);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    public static extern IntPtr FindWindowExW(IntPtr parent, IntPtr childAfter, string? className, string? windowName);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    public static extern IntPtr FindWindowEx(IntPtr hWndParent, IntPtr hWndChildAfter, string? lpszClass, string? lpszWindow);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    public static extern IntPtr GetDesktopWindow();

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    public static extern IntPtr GetShellWindow();

    [DllImport("user32.dll")]
    public static extern bool EnumWindows(EnumWindowsProc callback, IntPtr lParam);

    [DllImport("user32.dll")]
    public static extern bool EnumChildWindows(IntPtr hWndParent, EnumWindowsProc lpEnumFunc, IntPtr lParam);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern IntPtr SetParent(IntPtr child, IntPtr newParent);

    [DllImport("user32.dll")]
    public static extern IntPtr GetParent(IntPtr hwnd);

    // Per-thread DPI awareness. MonitorTracker pins these around enumeration.
    [DllImport("user32.dll")]
    public static extern IntPtr GetThreadDpiAwarenessContext();

    [DllImport("user32.dll")]
    public static extern IntPtr SetThreadDpiAwarenessContext(IntPtr context);

    // Pseudo-handles (valid on any thread): UNAWARE=-1, SYSTEM_AWARE=-2, PER_MONITOR=-3, V2=-4.
    public static readonly IntPtr DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE = new(-3);
    public static readonly IntPtr DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2 = new(-4);

    [DllImport("user32.dll")]
    public static extern IntPtr GetWindow(IntPtr hwnd, uint cmd);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    public static extern int GetClassNameW(IntPtr hwnd, StringBuilder buffer, int maxCount);

    [DllImport("user32.dll", CharSet = CharSet.Auto, SetLastError = true)]
    public static extern int GetClassName(IntPtr hWnd, StringBuilder lpClassName, int nMaxCount);

    [DllImport("user32.dll", CharSet = CharSet.Auto)]
    public static extern int GetWindowText(IntPtr hWnd, StringBuilder lpString, int nMaxCount);

    [DllImport("user32.dll")]
    public static extern bool IsWindow(IntPtr hwnd);

    [DllImport("user32.dll")]
    public static extern bool IsWindowVisible(IntPtr hwnd);

    [DllImport("user32.dll")]
    public static extern bool IsZoomed(IntPtr hwnd);

    [DllImport("user32.dll")]
    public static extern bool IsIconic(IntPtr hwnd);

    [DllImport("user32.dll")]
    public static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    public static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    public static extern IntPtr SetActiveWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    public static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint processId);

    [DllImport("user32.dll")]
    public static extern IntPtr GetAncestor(IntPtr hwnd, uint flags);

    [DllImport("user32.dll")]
    public static extern IntPtr WindowFromPoint(POINT pt);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool ScreenToClient(IntPtr hWnd, ref POINT lpPoint);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool GetCursorPos(out POINT point);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool SetCursorPos(int X, int Y);

    [DllImport("user32.dll")]
    public static extern int GetDoubleClickTime();

    [DllImport("user32.dll")]
    public static extern int GetSystemMetrics(int nIndex);

    [DllImport("user32.dll")]
    public static extern short GetAsyncKeyState(int vKey);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool DragDetect(IntPtr hWnd, POINT pt);

    // Styles / position
    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW", SetLastError = true)]
    public static extern IntPtr GetWindowLongPtrW(IntPtr hwnd, int index);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW", SetLastError = true)]
    public static extern IntPtr SetWindowLongPtrW(IntPtr hwnd, int index, IntPtr newLong);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongW")]
    public static extern int GetWindowLong(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool GetLayeredWindowAttributes(IntPtr hwnd, out uint pcrKey, out byte pbAlpha, out uint pdwFlags);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongW")]
    public static extern int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW", SetLastError = true)]
    public static extern IntPtr SetWindowLongPtr(IntPtr hWnd, int nIndex, IntPtr dwNewLong);

    [DllImport("user32.dll", EntryPoint = "GetClassLongW")]
    public static extern int GetClassLong(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll", EntryPoint = "SetClassLongW")]
    public static extern int SetClassLong(IntPtr hWnd, int nIndex, int dwNewLong);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern bool SetWindowPos(IntPtr hwnd, IntPtr insertAfter, int x, int y, int cx, int cy, uint flags);

    [DllImport("user32.dll")]
    public static extern bool GetWindowRect(IntPtr hwnd, out RECT rect);

    [DllImport("user32.dll")]
    public static extern bool GetClientRect(IntPtr hwnd, out RECT rect);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern int MapWindowPoints(IntPtr from, IntPtr to, ref RECT rect, uint pointCount);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern bool SetLayeredWindowAttributes(IntPtr hwnd, uint colorKey, byte alpha, uint flags);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern bool UpdateLayeredWindow(
        IntPtr hwnd, IntPtr dstDc, ref POINT dst, ref SIZE size,
        IntPtr srcDc, ref POINT src, uint colorKey, ref BLENDFUNCTION blend, uint flags);

    [DllImport("user32.dll")]
    public static extern bool InvalidateRect(IntPtr hwnd, IntPtr rect, bool erase);

    // Painting helpers
    [DllImport("user32.dll")]
    public static extern IntPtr BeginPaint(IntPtr hwnd, out PAINTSTRUCT ps);

    [DllImport("user32.dll")]
    public static extern bool EndPaint(IntPtr hwnd, ref PAINTSTRUCT ps);

    [DllImport("user32.dll")]
    public static extern IntPtr GetDC(IntPtr hwnd);

    [DllImport("user32.dll")]
    public static extern int ReleaseDC(IntPtr hwnd, IntPtr dc);

    // Monitors
    [DllImport("user32.dll")]
    public static extern bool EnumDisplayMonitors(IntPtr hdc, IntPtr clip, MonitorEnumProc callback, IntPtr data);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    public static extern bool GetMonitorInfoW(IntPtr hMonitor, ref MONITORINFOEX info);

    [DllImport("user32.dll", EntryPoint = "GetMonitorInfoW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool GetMonitorInfoEx(IntPtr hMonitor, ref MONITORINFOEX lpmi);

    [DllImport("user32.dll", EntryPoint = "GetMonitorInfoW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool GetMonitorInfo(IntPtr hMonitor, ref MONITORINFO monitorInfo);

    [DllImport("user32.dll")]
    public static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint flags);

    [DllImport("user32.dll")]
    public static extern IntPtr MonitorFromPoint(POINT pt, uint flags);

    [DllImport("user32.dll")]
    public static extern IntPtr MonitorFromRect(in RECT rect, uint flags);

    // WinEvent hooks
    [DllImport("user32.dll", SetLastError = true)]
    public static extern IntPtr SetWinEventHook(
        uint eventMin, uint eventMax, IntPtr module, WinEventProc callback,
        uint processId, uint threadId, uint flags);

    [DllImport("user32.dll")]
    public static extern bool UnhookWinEvent(IntPtr hook);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool UnhookWindowsHookEx(IntPtr hhk);

    // Low-level hooks (declared manually: strongly-typed wrappers fight the raw
    // IntPtr-based callback signature we need).
    [DllImport("user32.dll", EntryPoint = "SetWindowsHookExW")]
    public static extern IntPtr SetWindowsHookEx(int idHook, HookProc lpfn, IntPtr hMod, uint dwThreadId);

    [DllImport("user32.dll")]
    public static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);

    // Timers
    [DllImport("user32.dll", SetLastError = true)]
    public static extern UIntPtr SetTimer(IntPtr hwnd, UIntPtr id, uint elapseMs, IntPtr callback);

    [DllImport("user32.dll")]
    public static extern bool KillTimer(IntPtr hwnd, UIntPtr id);

    // Menus (tray + context menus)
    [DllImport("user32.dll")]
    public static extern IntPtr CreatePopupMenu();

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    public static extern bool AppendMenuW(IntPtr menu, uint flags, UIntPtr idOrSubmenu, string? text);

    [DllImport("user32.dll")]
    public static extern bool DestroyMenu(IntPtr menu);

    [DllImport("user32.dll")]
    public static extern int TrackPopupMenuEx(IntPtr menu, uint flags, int x, int y, IntPtr hwnd, IntPtr tpmParams);

    [DllImport("user32.dll", EntryPoint = "LoadCursorW")]
    public static extern IntPtr LoadCursor(IntPtr hInstance, IntPtr lpCursorName);

    [DllImport("user32.dll", EntryPoint = "SetCursor")]
    public static extern IntPtr SetCursor(IntPtr hCursor);

    [DllImport("user32.dll")]
    public static extern IntPtr SetCapture(IntPtr hWnd);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool ReleaseCapture();

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool TrackMouseEvent(ref TRACKMOUSEEVENT lpEventTrack);

    // Keyboard injection
    [DllImport("user32.dll")]
    public static extern void keybd_event(byte bVk, byte bScan, uint dwFlags, UIntPtr dwExtraInfo);

    // Misc
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern bool SystemParametersInfoW(uint action, uint uiParam, IntPtr pvParam, uint winIni);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern bool SystemParametersInfoW(uint action, uint uiParam, StringBuilder pvParam, uint winIni);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    public static extern int MessageBoxW(IntPtr hwnd, string text, string caption, uint type);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    public static extern IntPtr LoadCursorW(IntPtr instance, IntPtr cursorName);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool DestroyIcon(IntPtr hIcon);

    // Power-setting notifications (RegisterPowerSettingNotification).
    [DllImport("user32.dll", SetLastError = true)]
    public static extern IntPtr RegisterPowerSettingNotification(IntPtr recipient, ref Guid powerSettingGuid, int flags);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool UnregisterPowerSettingNotification(IntPtr handle);

    /// <summary>GDI (0) / USER (1) handle count for a process. The 10k-per-process GDI
    /// quota makes this the handle-leak counterpart to private bytes: climbing GDI with
    /// flat managed heap names an HBITMAP/HICON/DC leak; flat GDI with climbing private
    /// bytes points at driver mappings or pools instead.</summary>
    [DllImport("user32.dll")]
    public static extern uint GetGuiResources(IntPtr hProcess, uint uiFlags);
}
