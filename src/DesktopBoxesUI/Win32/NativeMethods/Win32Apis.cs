using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;
using DesktopBoxesUI.Shell.Interop;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.Graphics.Gdi;
using Windows.Win32.UI.WindowsAndMessaging;

namespace DesktopBoxesUI.Win32.NativeMethods;

/// <summary>
/// Single, isolated home for every Win32 P/Invoke call used by this application. CsWin32
/// generates the common APIs into <c>Windows.Win32.PInvoke</c>; the few architecture-specific APIs
/// (SHGetFileInfo, SetWinEventHook) are declared manually in <see cref="ManualApis"/> (still inside
/// this NativeMethods folder). This thin, explicitly named wrapper keeps all native declarations
/// behind one type so none are scattered through ViewModels or Views.
/// </summary>
[SupportedOSPlatform("windows10.0.14393")]
internal static class Win32Apis
{
    public static HWND GetDesktopWindow() => PInvoke.GetDesktopWindow();

    public static HWND GetShellWindow() => PInvoke.GetShellWindow();

    public static HMONITOR MonitorFromWindow(HWND hwnd, MONITOR_FROM_FLAGS flags) => PInvoke.MonitorFromWindow(hwnd, flags);

    public static HMONITOR MonitorFromRect(in RECT rect, MONITOR_FROM_FLAGS flags) => PInvoke.MonitorFromRect(in rect, flags);

    public static BOOL GetMonitorInfo(HMONITOR hMonitor, ref MONITORINFO monitorInfo) => PInvoke.GetMonitorInfo(hMonitor, ref monitorInfo);

    public static BOOL GetWindowRect(HWND hWnd, out RECT rect) => PInvoke.GetWindowRect(hWnd, out rect);

    public static BOOL SetWindowPos(HWND hWnd, HWND hWndInsertAfter, int x, int y, int cx, int cy, SET_WINDOW_POS_FLAGS uFlags)
        => PInvoke.SetWindowPos(hWnd, hWndInsertAfter, x, y, cx, cy, uFlags);

    public static uint GetDpiForSystem() => PInvoke.GetDpiForSystem();

    public static uint GetDpiForWindow(HWND hWnd) => PInvoke.GetDpiForWindow(hWnd);

    public static BOOL DestroyIcon(IntPtr hIcon) => PInvoke.DestroyIcon((HICON)hIcon);

    public static HWND FindWindowEx(HWND hWndParent, HWND hWndChildAfter, string? lpClassName, string? lpWindowName)
        => PInvoke.FindWindowEx(hWndParent, hWndChildAfter, lpClassName, lpWindowName);

    public static BOOL ShowWindow(HWND hWnd, int nCmdShow) => PInvoke.ShowWindow(hWnd, (SHOW_WINDOW_CMD)nCmdShow);

    public static int SHGetFileInfo(string? pszPath, uint dwFileAttributes, ref SHFILEINFOW psfi, uint cbFileInfo, SHGFI uFlags)
        => ManualApis.SHGetFileInfo(pszPath, dwFileAttributes, ref psfi, cbFileInfo, (uint)uFlags);

    public static int SHGetFileInfo(IntPtr pidl, uint dwFileAttributes, ref SHFILEINFOW psfi, uint cbFileInfo, SHGFI uFlags)
        => ManualApis.SHGetFileInfoPidl(pidl, dwFileAttributes, ref psfi, cbFileInfo, (uint)uFlags | (uint)SHGFI.Pidl);

    public static IntPtr SetWinEventHook(uint eventMin, uint eventMax, IntPtr hmodWinEventProc, WinEventProc pfnWinEventProc, uint idProcess, uint idThread, uint dwFlags)
        => ManualApis.SetWinEventHook(eventMin, eventMax, hmodWinEventProc, pfnWinEventProc, idProcess, idThread, dwFlags);

    public static bool UnhookWinEvent(IntPtr hWinEventHook)
        => ManualApis.UnhookWinEvent(hWinEventHook);

    public static bool ShellNotifyIcon(uint dwMessage, ref NOTIFYICONDATAW data)
        => ManualApis.Shell_NotifyIcon(dwMessage, ref data);

    public static short GetAsyncKeyState(int vKey) => ManualApis.GetAsyncKeyState(vKey);

    /// <summary>
    /// One entry extracted from a "Shell IDList Array" drag blob: either a real filesystem
    /// <see cref="FilePath"/>, or (for virtual shell items such as UWP/Store apps) the raw absolute
    /// PIDL bytes, which are stored base64-encoded on the <see cref="Core.Models.BoxItem"/>.
    /// </summary>
    public sealed class ShellItemEntry
    {
        public string? FilePath;
        public byte[]? Pidl;
    }

    /// <summary>
    /// Parses a "Shell IDList Array" (CFSTR_SHELLIDLIST) blob into entries. Each entry carries the
    /// filesystem path when the item lives on disk, otherwise its absolute PIDL bytes.
    /// </summary>
    public static IReadOnlyList<ShellItemEntry> GetShellIdListEntries(byte[] cida)
    {
        var result = new List<ShellItemEntry>();
        if (cida == null || cida.Length < 8)
        {
            return result;
        }

        try
        {
            int cidl = BitConverter.ToInt32(cida, 0);
            if (cidl <= 0 || cida.Length < 4 + (cidl + 1) * 4)
            {
                return result;
            }

            var handle = GCHandle.Alloc(cida, GCHandleType.Pinned);
            try
            {
                IntPtr basePtr = handle.AddrOfPinnedObject();
                IntPtr parent = IntPtr.Add(basePtr, BitConverter.ToInt32(cida, 4));
                for (int i = 1; i <= cidl; i++)
                {
                    IntPtr item = IntPtr.Add(basePtr, BitConverter.ToInt32(cida, 4 + i * 4));
                    IntPtr absolute = ManualApis.ILCombine(parent, item);
                    if (absolute == IntPtr.Zero)
                    {
                        continue;
                    }

                    var entry = new ShellItemEntry();
                    try
                    {
                        var sb = new StringBuilder(260);
                        if (ManualApis.SHGetPathFromIDListW(absolute, sb) != 0 && sb.Length > 0)
                        {
                            entry.FilePath = sb.ToString();
                        }

                        entry.Pidl = CopyPidl(absolute);
                    }
                    finally
                    {
                        ManualApis.ILFree(absolute);
                    }

                    result.Add(entry);
                }
            }
            finally
            {
                handle.Free();
            }
        }
        catch
        {
            // Malformed or unexpected blob: ignore.
        }

        return result;
    }

    /// <summary>Copies an ITEMIDLIST (absolute PIDL) into a managed byte array.</summary>
    private static byte[]? CopyPidl(IntPtr pidl)
    {
        if (pidl == IntPtr.Zero)
        {
            return null;
        }

        int offset = 0;
        while (true)
        {
            int cb = Marshal.ReadInt16(pidl, offset) & 0xFFFF;
            if (cb == 0)
            {
                break;
            }

            offset += cb;
        }

        int total = offset + 2;
        var buffer = new byte[total];
        Marshal.Copy(pidl, buffer, 0, total);
        return buffer;
    }

    internal static bool TryPinPidl(string base64, out byte[] bytes, out GCHandle handle, out IntPtr ptr)
    {
        bytes = Array.Empty<byte>();
        handle = default;
        ptr = IntPtr.Zero;

        try
        {
            bytes = Convert.FromBase64String(base64);
        }
        catch
        {
            return false;
        }

        handle = GCHandle.Alloc(bytes, GCHandleType.Pinned);
        ptr = handle.AddrOfPinnedObject();
        return true;
    }

    /// <summary>Returns the display name for a stored PIDL (used as the item label).</summary>
    public static string GetPidlDisplayName(string base64)
    {
        if (!TryPinPidl(base64, out _, out var handle, out var ptr))
        {
            return string.Empty;
        }

        try
        {
            if (ShellNative.SHCreateItemFromIDList(ptr, ShellNative.IID_IShellItem, out IShellItem item) != 0 || item == null)
            {
                return string.Empty;
            }

            try
            {
                if (item.GetDisplayName(SHGDNF.NORMAL, out var pname) == 0 && pname != IntPtr.Zero)
                {
                    var name = Marshal.PtrToStringUni(pname);
                    Marshal.FreeCoTaskMem(pname);
                    if (!string.IsNullOrEmpty(name))
                    {
                        return name!;
                    }
                }
            }
            finally
            {
                Marshal.ReleaseComObject(item);
            }
        }
        catch
        {
            // ignore
        }
        finally
        {
            handle.Free();
        }

        return string.Empty;
    }

    /// <summary>Resolves the icon HICON for a stored PIDL (virtual shell items included). Caller must DestroyIcon.</summary>
    public static IntPtr GetIconForPidl(string base64)
    {
        if (!TryPinPidl(base64, out _, out var handle, out var ptr))
        {
            return IntPtr.Zero;
        }

        try
        {
            var psfi = new SHFILEINFOW();
            uint cb = (uint)Marshal.SizeOf<SHFILEINFOW>();
            if (SHGetFileInfo(ptr, 0, ref psfi, cb, SHGFI.Icon | SHGFI.SmallIcon | SHGFI.AddOverlays) != 0)
            {
                return psfi.hIcon;
            }
        }
        catch
        {
            // ignore
        }
        finally
        {
            handle.Free();
        }

        return IntPtr.Zero;
    }

    /// <summary>Launches a stored PIDL (works for files, folders and UWP/Store apps).</summary>
    public static bool LaunchPidl(string base64)
    {
        if (!TryPinPidl(base64, out _, out var handle, out var ptr))
        {
            return false;
        }

        try
        {
            var info = new SHELLEXECUTEINFO
            {
                cbSize = Marshal.SizeOf<SHELLEXECUTEINFO>(),
                fMask = SEE_MASK.IDLIST | SEE_MASK.NO_UI,
                hwnd = IntPtr.Zero,
                lpVerb = "open",
                lpFile = null,
                lpIDList = ptr,
                nShow = 1, // SW_SHOWNORMAL
            };

            return ManualApis.ShellExecuteEx(ref info);
        }
        catch
        {
            return false;
        }
        finally
        {
            handle.Free();
        }
    }

    /// <summary>
    /// Resolves a UWP/Store <c>AppUserModelID</c> (e.g. <c>Microsoft.WindowsCalculator_8wekyb3d8bbwe!App</c>)
    /// to its absolute PIDL so it can be opened and get an icon like any other shell item.
    /// Returns <c>null</c> if the id cannot be resolved.
    /// </summary>
    public static byte[]? GetPidlForAppId(string appId)
    {
        // Two known parsing-name forms for the modern "AppsFolder" namespace.
        string[] candidates =
        [
            "shell:appsFolder\\" + appId,
            "shell:::{4234d49b-0245-4df3-b780-3893943456e1}\\" + appId,
        ];

        foreach (var parsing in candidates)
        {
            if (TryGetPidlFromParsingName(parsing, out var pidl) && pidl != null)
            {
                return pidl;
            }
        }

        return null;
    }

    private static bool TryGetPidlFromParsingName(string parsing, out byte[]? pidl)
    {
        pidl = null;
        try
        {
            if (ShellNative.SHCreateItemFromParsingName(parsing, IntPtr.Zero, ShellNative.IID_IShellItem, out IShellItem item) != 0 || item == null)
            {
                return false;
            }

            try
            {
                var pUnk = Marshal.GetIUnknownForObject(item);
                try
                {
                    if (ShellNative.SHGetIDListFromObject(pUnk, out var pidlPtr) != 0 || pidlPtr == IntPtr.Zero)
                    {
                        return false;
                    }

                    try
                    {
                        pidl = CopyPidl(pidlPtr);
                        return true;
                    }
                    finally
                    {
                        ShellNative.ILFree(pidlPtr);
                    }
                }
                finally
                {
                    Marshal.Release(pUnk);
                }
            }
            finally
            {
                Marshal.ReleaseComObject(item);
            }
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Marks a window as a desktop-widget tool window (<c>WS_EX_TOOLWINDOW</c>). This keeps it visible
    /// when the user presses Win+D / Show Desktop (which otherwise minimizes ordinary windows), while
    /// still hiding it from the taskbar and Alt+Tab — matching how Fences-style desktop boxes behave.
    /// </summary>
    public static void MakeToolWindow(IntPtr hwnd)
    {
        try
        {
            int ex = ManualApis.GetWindowLong(hwnd, ManualApis.GWL_EXSTYLE);
            ManualApis.SetWindowLong(hwnd, ManualApis.GWL_EXSTYLE, ex | ManualApis.WS_EX_TOOLWINDOW);
            ManualApis.SetWindowPos(
                hwnd,
                IntPtr.Zero,
                0,
                0,
                0,
                0,
                ManualApis.SWP_NOMOVE | ManualApis.SWP_NOSIZE | ManualApis.SWP_NOZORDER | ManualApis.SWP_FRAMECHANGED | ManualApis.SWP_NOACTIVATE);
        }
        catch
        {
            // ignore — cosmetic if it fails
        }
    }

    /// <summary>
    /// "Glues" a window to the desktop without turning it into a child window. Setting the window's
    /// owner (<c>GWL_HWNDPARENT</c>) to Progman keeps it a top-level window — so WPF layered
    /// (AllowsTransparency) rendering still works — while tying it to the desktop so Show Desktop /
    /// Win+D does not minimize it. The minimize/maximize boxes are also removed so the window cannot
    /// be dispatched into the taskbar by accident.
    /// </summary>
    public static void GlueToDesktop(IntPtr hwnd)
    {
        try
        {
            var progman = ManualApis.FindWindowEx(IntPtr.Zero, IntPtr.Zero, "Progman", null);
            if (progman == IntPtr.Zero)
            {
                return;
            }

            ManualApis.SetWindowLongPtr(hwnd, ManualApis.GWL_HWNDPARENT, progman);
        }
        catch (Exception)
        {
            // Non-critical window setup; ignore failures.
        }
    }

    /// <summary>
    /// Removes the minimize/maximize boxes so the window cannot be minimized (by Win+D / Show Desktop
    /// or by the user). The style change is flushed with SWP_FRAMECHANGED so it takes effect immediately.
    /// </summary>
    public static void PreventMinimize(IntPtr hwnd)
    {
        try
        {
            int style = ManualApis.GetWindowLong(hwnd, ManualApis.GWL_STYLE);
            style = (style & ~ManualApis.WS_MAXIMIZEBOX) & ~ManualApis.WS_MINIMIZEBOX;
            ManualApis.SetWindowLong(hwnd, ManualApis.GWL_STYLE, style);

            ManualApis.SetWindowPos(
                hwnd,
                ManualApis.HWND_TOP,
                0,
                0,
                0,
                0,
                ManualApis.SWP_NOMOVE | ManualApis.SWP_NOSIZE | ManualApis.SWP_FRAMECHANGED | ManualApis.SWP_NOACTIVATE);
        }
        catch (Exception)
        {
            // Non-critical window setup; ignore failures.
        }
    }

    private static readonly HashSet<IntPtr> _allowHide = new();

    /// <summary>
    /// Permits a one-shot real hide (e.g. app shutdown) so the minimize-prevention hook does not
    /// immediately counter it. Call before hiding the window, then the hide proceeds normally.
    /// </summary>
    public static void AllowHide(IntPtr hwnd)
    {
        lock (_allowHide)
        {
            _allowHide.Add(hwnd);
        }
    }

    public static void DisallowHide(IntPtr hwnd)
    {
        lock (_allowHide)
        {
            _allowHide.Remove(hwnd);
        }
    }

    private const int WM_SHOWWINDOW = 0x0018;
    private const int WM_SYSCOMMAND = 0x0112;
    private const int SC_MINIMIZE = 0xF020;
    private const int SW_SHOWNOACTIVATE = 4;

    /// <summary>
    /// HwndSource hook that keeps our desktop windows from being minimized/hidden by Show Desktop /
    /// Win+D. Blocks SC_MINIMIZE and counters a shell-initiated WM_SHOWWINDOW hide by re-showing the
    /// window. Hides we trigger ourselves are preceded by <see cref="AllowHide"/>.
    /// </summary>
    public static IntPtr MinimizePreventionHook(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WM_SYSCOMMAND && (wParam.ToInt32() & 0xFFF0) == SC_MINIMIZE)
        {
            handled = true;
            return IntPtr.Zero;
        }

        if (msg == WM_SHOWWINDOW)
        {
            bool fShow = wParam.ToInt32() != 0;
            if (!fShow)
            {
                bool allowed;
                lock (_allowHide)
                {
                    allowed = _allowHide.Contains(hwnd);
                }

                if (!allowed)
                {
                    ManualApis.ShowWindow(hwnd, SW_SHOWNOACTIVATE);
                    handled = true;
                    return IntPtr.Zero;
                }
            }
        }

        return IntPtr.Zero;
    }
}
