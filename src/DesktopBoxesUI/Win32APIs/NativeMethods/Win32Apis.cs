using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;
using DesktopBoxesUI.Shell.Interop;
using DesktopBoxesUI.Win32.Services;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.Graphics.Gdi;
using Windows.Win32.UI.Shell;
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

    /// <summary>Gets the current cursor position in physical screen pixels (works during a drag operation,
    /// unlike <see cref="Mouse.GetPosition"/> which is suppressed by the drag-drop capture).</summary>
    public static bool GetCursorPos(out ManualApis.POINT pt) => ManualApis.GetCursorPos(out pt);

    /// <summary>Shell file operation (rename / delete / recycle). Wraps the manual
    /// <see cref="ManualApis.SHFileOperationW"/> so every native call flows through this wrapper.</summary>
    public static int FileOperation(ref ManualApis.SHFILEOPSTRUCT op) => ManualApis.SHFileOperationW(ref op);

    public const uint FO_MOVE = 0x0001;
    public const uint FO_COPY = 0x0002;
    public const uint FO_DELETE = 0x0003;
    public const ushort FOF_ALLOWUNDO = 0x0040;
    public const ushort FOF_NOCONFIRMATION = 0x0010;
    public const ushort FOF_SILENT = 0x0004;
    public const ushort FOF_NOERRORUI = 0x0400;
    public const ushort FOF_WANTNUKEWARNING = 0x4000;

    /// <summary>The foreground window handle (used as the owner for shell file-operation dialogs).</summary>
    public static IntPtr GetForegroundWindow() => ManualApis.GetForegroundWindow();

    public static uint GetDpiForWindow(HWND hWnd) => PInvoke.GetDpiForWindow(hWnd);

    public static BOOL DestroyIcon(IntPtr hIcon) => PInvoke.DestroyIcon((HICON)hIcon);

    public static void DeleteObject(IntPtr hBitmap) => ManualApis.DeleteObject(hBitmap);

    public static HWND FindWindowEx(HWND hWndParent, HWND hWndChildAfter, string? lpClassName, string? lpWindowName)
        => PInvoke.FindWindowEx(hWndParent, hWndChildAfter, lpClassName, lpWindowName);

    public static BOOL ShowWindow(HWND hWnd, int nCmdShow) => PInvoke.ShowWindow(hWnd, (SHOW_WINDOW_CMD)nCmdShow);

    public static bool IsWindowVisible(IntPtr hWnd) => ManualApis.IsWindowVisible(hWnd);

    /// <summary>
    /// Toggles desktop icon visibility the same way Explorer's "Show desktop icons" context-menu item
    /// does: it sends <c>WM_COMMAND</c> 0x7402 to the desktop's <c>SHELLDLL_DefView</c> window. This
    /// hides/shows the icon container while keeping the desktop view alive, so the right-click "New"
    /// verb keeps working (unlike <c>ShowWindow(SW_HIDE)</c> on the list-view, which breaks it).
    /// The operation flips the current state, so callers should only invoke it on a real state change.
    /// </summary>
    public static void ToggleDesktopIcons()
    {
        IntPtr defView = FindDesktopFolderView();
        if (defView == IntPtr.Zero)
        {
            return;
        }

        const uint WmCommand = 0x0111;
        ManualApis.SendMessage(defView, WmCommand, (IntPtr)0x7402, IntPtr.Zero);
    }

    private static IntPtr FindDesktopFolderView()
    {
        HWND progman = Win32Apis.FindWindowEx(HWND.Null, HWND.Null, "Progman", "Program Manager");
        HWND defView = Win32Apis.FindWindowEx(progman, HWND.Null, "SHELLDLL_DefView", null);
        if ((IntPtr)defView != IntPtr.Zero)
        {
            return (IntPtr)defView;
        }

        HWND worker = Win32Apis.FindWindowEx(HWND.Null, HWND.Null, "WorkerW", null);
        while ((IntPtr)worker != IntPtr.Zero)
        {
            defView = Win32Apis.FindWindowEx(worker, HWND.Null, "SHELLDLL_DefView", null);
            if ((IntPtr)defView != IntPtr.Zero)
            {
                return (IntPtr)defView;
            }

            worker = Win32Apis.FindWindowEx(HWND.Null, worker, "WorkerW", null);
        }

        return IntPtr.Zero;
    }

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

    /// <summary>Returns the bounding rectangle of the tray icon in physical screen pixels (S_OK on success).</summary>
    public static bool ShellNotifyIconGetRect(IntPtr hWnd, uint uID, out RECT rect)
    {
        var id = new ManualApis.NOTIFYICONIDENTIFIER
        {
            cbSize = (uint)Marshal.SizeOf<ManualApis.NOTIFYICONIDENTIFIER>(),
            hWnd = hWnd,
            uID = uID,
        };
        return ManualApis.Shell_NotifyIconGetRect(ref id, out rect) == 0;
    }

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
            if (SHGetFileInfo(ptr, 0, ref psfi, cb, SHGFI.Icon | SHGFI.SmallIcon | SHGFI.AddOverlays | SHGFI.Pidl) != 0)
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

    /// <summary>Resolves the icon for a stored PIDL as an <see cref="HBITMAP"/> (with alpha) using
    /// <c>IShellItemImageFactory</c> — the same mechanism Explorer uses, so it works for virtual shell
    /// items (Start Menu apps, This PC, ...) that <c>SHGetFileInfo</c> won't resolve. Returns
    /// <see cref="IntPtr.Zero"/> on failure. Caller must <see cref="DeleteObject"/> the result.</summary>
    public static IntPtr GetIconBitmapForPidl(string base64)
    {
        if (!TryPinPidl(base64, out _, out var handle, out var ptr))
        {
            return IntPtr.Zero;
        }

        try
        {
            if (ShellNative.SHCreateItemFromIDList(ptr, ShellNative.IID_IShellItem, out IShellItem item) != 0 || item is null)
            {
                return IntPtr.Zero;
            }

            try
            {
                if (item is IShellItemImageFactory factory)
                {
                    var size = new SIZE { cx = 32, cy = 32 };
                    const uint SIIGBF_ICONONLY = 0x00000004;
                    if (factory.GetImage(size, SIIGBF_ICONONLY, out IntPtr hbmp) == 0 && hbmp != IntPtr.Zero)
                    {
                        return hbmp;
                    }
                }

                return IntPtr.Zero;
            }
            finally
            {
                Marshal.ReleaseComObject(item);
            }
        }
        catch
        {
            return IntPtr.Zero;
        }
        finally
        {
            handle.Free();
        }
    }

    /// <summary>Resolves the icon for a filesystem path (e.g. a <c>.lnk</c> on the desktop) as an
    /// <see cref="HBITMAP"/> using <c>IShellItemImageFactory</c>. Unlike <c>SHGetFileInfo</c>, this correctly
    /// follows PIDL-only link targets (such as the shortcuts we create for Start Menu apps). Returns
    /// <see cref="IntPtr.Zero"/> on failure. Caller must <see cref="DeleteObject"/> the result.</summary>
    public static IntPtr GetIconBitmapForPath(string path)
    {
        try
        {
            if (ShellNative.SHCreateItemFromParsingName(path, IntPtr.Zero, ShellNative.IID_IShellItem, out IShellItem item) != 0 || item is null)
            {
                return IntPtr.Zero;
            }

            try
            {
                if (item is IShellItemImageFactory factory)
                {
                    var size = new SIZE { cx = 32, cy = 32 };
                    const uint SIIGBF_ICONONLY = 0x00000004;
                    if (factory.GetImage(size, SIIGBF_ICONONLY, out IntPtr hbmp) == 0 && hbmp != IntPtr.Zero)
                    {
                        return hbmp;
                    }
                }

                return IntPtr.Zero;
            }
            finally
            {
                Marshal.ReleaseComObject(item);
            }
        }
        catch
        {
            return IntPtr.Zero;
        }
    }

    /// <summary>Resolves a stored (base64) absolute PIDL to a filesystem path when the item has one
    /// (e.g. a Start Menu <c>.lnk</c> shortcut). Returns <c>null</c> for pure virtual shell items.</summary>
    public static string? GetPathFromPidl(string base64)
    {
        if (!TryPinPidl(base64, out _, out var handle, out var ptr))
        {
            return null;
        }

        try
        {
            var sb = new StringBuilder(260);
            if (ManualApis.SHGetPathFromIDListW(ptr, sb) != 0 && sb.Length > 0)
            {
                return sb.ToString();
            }

            return null;
        }
        finally
        {
            handle.Free();
        }
    }

    /// <summary>Creates a <c>.lnk</c> shortcut (storing the PIDL) at <paramref name="destinationLnkPath"/> for a
    /// virtual shell item that has no filesystem path (e.g. a Start Menu app). Mirrors what Explorer does when
    /// you drag such an item to the desktop. Returns false on failure.</summary>
    public static bool CreateShortcutFromPidl(string pidlBase64, string destinationLnkPath)
    {
        if (!TryPinPidl(pidlBase64, out _, out var handle, out var ptr))
        {
            return false;
        }

        try
        {
            var linkType = Type.GetTypeFromCLSID(ShellNative.CLSID_ShellLink);
            if (linkType == null)
            {
                return false;
            }

            if (Activator.CreateInstance(linkType) is not IShellLink link)
            {
                return false;
            }

            try
            {
                if (link.SetIDList(ptr) != 0)
                {
                    return false;
                }

                if (link is IPersistFile pf)
                {
                    pf.Save(destinationLnkPath, true);
                    return true;
                }
            }
            finally
            {
                Marshal.ReleaseComObject(link);
            }
        }
        catch
        {
            return false;
        }
        finally
        {
            handle.Free();
        }

        return false;
    }

    /// <summary>Shows the Windows property sheet for a shell item (Alt+Enter behaviour).</summary>
    public static void ShowProperties(IntPtr hwnd, string? path, string? pidlBase64)
    {
        try
        {
            var info = new SHELLEXECUTEINFO
            {
                cbSize = Marshal.SizeOf<SHELLEXECUTEINFO>(),
                fMask = SEE_MASK.NO_UI,
                hwnd = hwnd,
                lpVerb = "properties",
                lpFile = path,
                nShow = 1, // SW_SHOWNORMAL
            };

            // Prefer the absolute PIDL (handles .lnk and virtual shell items exactly like Explorer).
            if (!string.IsNullOrEmpty(pidlBase64) && TryPinPidl(pidlBase64, out _, out var handle, out var relPtr))
            {
                try
                {
                    if (ManualApis.SHGetSpecialFolderLocation(IntPtr.Zero, 0 /* CSIDL_DESKTOP */, out IntPtr deskPidl) == 0 && deskPidl != IntPtr.Zero)
                    {
                        try
                        {
                            IntPtr absPidl = ManualApis.ILCombine(deskPidl, relPtr);
                            if (absPidl != IntPtr.Zero)
                            {
                                try
                                {
                                    info.fMask = SEE_MASK.INVOKEIDLIST;
                                    info.lpIDList = absPidl;
                                    info.lpFile = null;
                                    ManualApis.ShellExecuteEx(ref info);
                                }
                                finally
                                {
                                    ManualApis.ILFree(absPidl);
                                }

                                return;
                            }
                        }
                        finally
                        {
                            ManualApis.ILFree(deskPidl);
                        }
                    }
                }
                finally
                {
                    handle.Free();
                }

                // Could not build an absolute PIDL; fall back to nothing rather than an invalid launch.
                return;
            }

            ManualApis.ShellExecuteEx(ref info);
        }
        catch
        {
            // Never let a shell failure take down the app.
        }
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

            // Raise the window above the Explorer desktop listview (SysListView32) within the desktop
            // layer. Without this, OLE drag/drop over empty desktop is delivered to Explorer instead of
            // our surface, so drops onto empty space never reach us. It stays below the real top-level
            // app windows (and below the BoxContainer windows, which are created after it and also
            // glued to the desktop), preserving click-through and per-container drops.
            ManualApis.SetWindowPos(hwnd, ManualApis.HWND_TOP, 0, 0, 0, 0,
                ManualApis.SWP_NOMOVE | ManualApis.SWP_NOSIZE | ManualApis.SWP_NOACTIVATE);
        }
        catch (Exception)
        {
            // Non-critical window setup; ignore failures.
        }
    }

    /// <summary>
    /// Glues the desktop overlay surface to the Explorer desktop, but places it directly ABOVE the
    /// desktop list-view (SysListView32) so that OLE drag/drop over empty desktop is delivered to the
    /// surface instead of to Explorer (which rejects our custom format and shows a "no-drop" cursor).
    /// The surface stays below the real top-level app windows and below the box container windows
    /// (which are glued to Progman and created afterwards), so per-container drops and normal window
    /// interaction are unaffected. Falls back to <see cref="GlueToDesktop"/> if the list-view can't be
    /// located.
    /// </summary>
    public static void GlueToDesktopSurface(IntPtr hwnd)
    {
        try
        {
            var listView = ExplorerDesktopService.FindDesktopListView();
            var parent = listView != IntPtr.Zero ? ManualApis.GetParent(listView) : IntPtr.Zero;
            if (parent == IntPtr.Zero)
            {
                parent = ManualApis.FindWindowEx(IntPtr.Zero, IntPtr.Zero, "Progman", null);
            }

            if (parent == IntPtr.Zero)
            {
                return;
            }

            ManualApis.SetWindowLongPtr(hwnd, ManualApis.GWL_HWNDPARENT, parent);

            // Sit BELOW the list-view by default so Explorer's own drag/drop (moving icons, dropping
            // files) is delivered to Explorer as normal. It is raised above the list-view only for the
            // duration of our own tab drag (see RaiseDesktopSurface) to show the correct drop cursor.
            ManualApis.SetWindowPos(hwnd, ManualApis.HWND_BOTTOM, 0, 0, 0, 0,
                ManualApis.SWP_NOMOVE | ManualApis.SWP_NOSIZE | ManualApis.SWP_NOACTIVATE);
        }
        catch (Exception)
        {
            // Non-critical window setup; ignore failures.
        }
    }

    /// <summary>
    /// Temporarily raises the desktop surface above the Explorer list-view (so OLE delivers our
    /// in-process tab drag to it and the correct drop cursor shows) or restores it below the list-view
    /// (so Explorer's native drag/drop keeps working). Used only for the duration of a tab drag.
    /// </summary>
    public static void RaiseDesktopSurface(IntPtr surfaceHwnd, IntPtr listViewHwnd, bool above)
    {
        try
        {
            var insertAfter = above && listViewHwnd != IntPtr.Zero ? listViewHwnd : ManualApis.HWND_BOTTOM;
            ManualApis.SetWindowPos(surfaceHwnd, insertAfter, 0, 0, 0, 0,
                ManualApis.SWP_NOMOVE | ManualApis.SWP_NOSIZE | ManualApis.SWP_NOACTIVATE);
        }
        catch (Exception)
        {
            // Non-critical; ignore.
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

    // --- Low-level mouse hook (used to detect clicks outside the app, e.g. the bare desktop) ---

    public const int WH_MOUSE_LL = ManualApis.WH_MOUSE_LL;

    public static IntPtr SetWindowsHookEx(int idHook, ManualApis.HookProc lpfn, IntPtr hMod, uint dwThreadId)
        => ManualApis.SetWindowsHookEx(idHook, lpfn, hMod, dwThreadId);

    public static bool UnhookWindowsHookEx(IntPtr hhk) => ManualApis.UnhookWindowsHookEx(hhk);

    public static IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam)
        => ManualApis.CallNextHookEx(hhk, nCode, wParam, lParam);

    public static uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId)
        => ManualApis.GetWindowThreadProcessId(hWnd, out lpdwProcessId);

    public static IntPtr GetModuleHandle(string? moduleName) => ManualApis.GetModuleHandle(moduleName);

    public static IntPtr WindowFromPoint(ManualApis.POINT pt) => ManualApis.WindowFromPoint(pt);

    /// <summary>Moves focus to <paramref name="hWnd"/> (e.g. the desktop shell window) so a previously
    /// focused window loses focus. Used to defocus the active box when the user clicks empty desktop.</summary>
    public static bool SetForegroundWindow(IntPtr hWnd) => ManualApis.SetForegroundWindow(hWnd);

    /// <summary>True when <paramref name="hwnd"/> is a <c>SysListView32</c> (the desktop list-view among others).</summary>
    public static bool IsSysListView32(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero)
        {
            return false;
        }

        var sb = new StringBuilder(256);
        return ManualApis.GetClassName(hwnd, sb, 256) != 0 && sb.ToString() == "SysListView32";
    }

    /// <summary>
    /// True when <paramref name="hwnd"/> belongs to the Explorer desktop — i.e. it is a descendant of
    /// <c>Progman</c> or <c>WorkerW</c>. This reliably covers the desktop list-view AND the app's own
    /// transparent <see cref="Views.DesktopSurface"/> (a child of <c>SHELLDLL_DefView</c>), regardless of
    /// whether desktop icons are shown or hidden, and without depending on a specific class hierarchy.
    /// File Explorer windows are excluded because their ancestry stops at <c>CabinetWClass</c>, not Progman/WorkerW.
    /// </summary>
    public static bool IsDesktopChild(IntPtr hwnd)
    {
        IntPtr h = hwnd;
        for (int i = 0; i < 16 && h != IntPtr.Zero; i++)
        {
            var sb = new StringBuilder(256);
            if (ManualApis.GetClassName(h, sb, 256) != 0)
            {
                string cls = sb.ToString();
                if (cls == "Progman" || cls == "WorkerW")
                {
                    return true;
                }
            }

            h = ManualApis.GetParent(h);
        }

        return false;
    }

    private static readonly HashSet<IntPtr> _boxWindows = new();

    /// <summary>Registers/unregisters a <see cref="Views.BoxContainerWindow"/> handle so the low-level
    /// mouse hook can exclude our own boxes from "empty desktop" detection.</summary>
    public static void RegisterBoxWindow(IntPtr hwnd)
    {
        lock (_boxWindows)
        {
            _boxWindows.Add(hwnd);
        }
    }

    public static void UnregisterBoxWindow(IntPtr hwnd)
    {
        lock (_boxWindows)
        {
            _boxWindows.Remove(hwnd);
        }
    }

    public static bool IsBoxWindow(IntPtr hwnd)
    {
        lock (_boxWindows)
        {
            return _boxWindows.Contains(hwnd);
        }
    }

    /// <summary>
    /// True when <paramref name="screenPt"/> (physical screen pixels) falls on empty desktop area
    /// within the given desktop list-view — i.e. not on a desktop icon. Uses <c>LVM_HITTEST</c> so a
    /// double-click that hits an icon leaves the icon's own open behaviour intact.
    /// </summary>
    public static bool IsDesktopEmptyPoint(IntPtr listViewHwnd, ManualApis.POINT screenPt)
    {
        if (listViewHwnd == IntPtr.Zero || !Win32Apis.IsSysListView32(listViewHwnd))
        {
            return false;
        }

        var client = screenPt;
        if (!ManualApis.ScreenToClient(listViewHwnd, ref client))
        {
            return false;
        }

        var info = new ManualApis.LVHITTESTINFO { pt = client };
        ManualApis.SendMessage(listViewHwnd, ManualApis.LVM_HITTEST, IntPtr.Zero, ref info);
        return (info.flags & ManualApis.LVHT_NOWHERE) != 0 || info.iItem < 0;
    }
}
