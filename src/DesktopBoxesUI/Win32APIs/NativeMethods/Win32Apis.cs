using DesktopBoxesUI.Shell.Interop;
using DesktopBoxesUI.Win32.Services;
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;
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

    public static bool IsWindow(IntPtr hWnd) => PInvoke.IsWindow((HWND)hWnd);

    public static IntPtr GetWindow(IntPtr hWnd, uint uCmd) => ManualApis.GetWindow(hWnd, uCmd);

    public static IntPtr LoadArrowCursor() => ManualApis.LoadCursor(IntPtr.Zero, (IntPtr)32512 /* IDC_ARROW */);

    public static IntPtr SetCursor(IntPtr hCursor) => ManualApis.SetCursor(hCursor);

    /// <summary>Returns the window's class name, or <see cref="string.Empty"/> if it cannot be read.</summary>
    public static string GetWindowClass(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero)
        {
            return string.Empty;
        }

        var sb = new StringBuilder(256);
        return ManualApis.GetClassName(hwnd, sb, 256) != 0 ? sb.ToString() : string.Empty;
    }

    /// <summary>
    /// True when <paramref name="hwnd"/> is a top-level window of the desktop shell layer
    /// (<c>Progman</c> / <c>WorkerW</c>) or the desktop window itself. Used to validate a cached
    /// anchor before forcing it as an insert-after handle, so a stale or rogue handle can never pin
    /// the surface at a high z-position.
    /// </summary>
    public static bool IsDesktopLayerTopLevel(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero || hwnd == GetDesktopWindow())
        {
            return hwnd != IntPtr.Zero;
        }

        string cls = GetWindowClass(hwnd);
        return cls == ShellWindowClasses.Progman || cls == ShellWindowClasses.WorkerW;
    }

    /// <summary>Makes a layered window click-through (mouse hits the window beneath it) for debugging
    /// overlays that must not steal input while they visualize the desktop.</summary>
    public static void MakeClickThrough(IntPtr hwnd)
    {
        try
        {
            int ex = ManualApis.GetWindowLong(hwnd, ManualApis.GWL_EXSTYLE);
            ManualApis.SetWindowLong(hwnd, ManualApis.GWL_EXSTYLE, ex | ManualApis.WS_EX_TRANSPARENT);
        }
        catch (Exception)
        {
            // Non-critical; ignore.
        }
    }

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
        //try
        //{
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
        //}
        //catch
        //{
        //    // ignore — cosmetic if it fails
        //}
    }

    /// <summary>
    /// "Glues" a window to the desktop without turning it into a child window. Setting the window's
    /// owner (<c>GWL_HWNDPARENT</c>) keeps it a top-level window — so WPF layered (AllowsTransparency)
    /// rendering still works — while tying it to the desktop layer so Show Desktop / Win+D does not
    /// minimize it; the minimize/maximize boxes are also removed so the window cannot be dispatched
    /// into the taskbar by accident.
    /// When the custom <see cref="Views.DesktopSurface"/> is live, its handle can be passed as
    /// <paramref name="owner"/>: Windows guarantees an owned window always sits ABOVE its owner in the
    /// z-order, so a box owned by the surface can never sink below it (and never lose clicks/activation
    /// to it), while remaining a real top-level window (multi-monitor geometry, keyboard focus and OLE
    /// drag/drop keep working — unlike a true WS_CHILD SetParent). Falls back to Progman when no owner
    /// is given (or the surface handle is no longer valid).
    /// </summary>
    public static void GlueToDesktop(IntPtr hwnd, IntPtr? owner = null)
    {
        //try
        //{
        var ownerHwnd = owner ?? IntPtr.Zero;
        if (ownerHwnd == IntPtr.Zero || !IsWindow(ownerHwnd))
        {
            ownerHwnd = ManualApis.FindWindowEx(IntPtr.Zero, IntPtr.Zero, ShellWindowClasses.Progman, null);
        }

        if (ownerHwnd == IntPtr.Zero)
        {
            return;
        }

        ManualApis.SetWindowLongPtr(hwnd, ManualApis.GWL_HWNDPARENT, ownerHwnd);

        // Raise the window above the Explorer desktop listview (SysListView32) within the desktop
        // layer. Without this, OLE drag/drop over empty desktop is delivered to Explorer instead of
        // our surface, so drops onto empty space never reach us. Box windows stay above the
        // DesktopSurface — structurally, because they are OWNED by it (owned windows always float
        // above their owner) — preserving click-through and per-container drops.
        ManualApis.SetWindowPos(hwnd, ManualApis.HWND_TOP, 0, 0, 0, 0,
            ManualApis.SWP_NOMOVE | ManualApis.SWP_NOSIZE | ManualApis.SWP_NOACTIVATE);
        //}
        //catch (Exception)
        //{
        //    // Non-critical window setup; ignore failures.
        //}
    }

    /// <summary>The live HWND of the desktop surface, published so debug tooling can locate it even
    /// when it is reparented (and thus invisible to <c>EnumWindows</c>).</summary>
    public static IntPtr DesktopSurfaceHandle;

    /// <summary>
    /// Returns the real desktop root window (the Progman / WorkerW that actually hosts the Explorer
    /// <c>SHELLDLL_DefView</c>). We use <c>GetParent(DefView)</c> rather than a blind
    /// <c>FindWindowEx("Progman")</c>, because there can be several vestigial Progman windows and only
    /// the one parenting DefView is the one whose content we must sit above.
    /// </summary>
    public static IntPtr GetDesktopRootHandle()
    {
        // Prefer the parent of the live Explorer DefView: that is the exact Progman/WorkerW whose content
        // we must sit above. (GetParent of a DefView returns Progman or the WorkerW that hosts it.)
        var defView = ExplorerDesktopService.FindDesktopSHELLDLL_DefView();
        if (defView != IntPtr.Zero)
        {
            var parent = ManualApis.GetParent(defView);
            if (parent != IntPtr.Zero)
            {
                return parent;
            }
        }

        // Fallback: any Progman window.
        var progman = ManualApis.FindWindowEx(IntPtr.Zero, IntPtr.Zero, ShellWindowClasses.Progman, null);
        if (progman != IntPtr.Zero)
        {
            return progman;
        }

        // Fallback: the WorkerW that actually hosts a DefView (iterate if the simple search missed it).
        var worker = ExplorerDesktopService.FindDesktopWorkerW();
        if (worker != IntPtr.Zero)
        {
            return worker;
        }

        // Last resort: the desktop window itself. It is always non-zero, and inserting the surface just
        // above it still keeps us above the Explorer list-view and below every real application window.
        // Never return IntPtr.Zero here — a zero root would make GlueToDesktopSurface and the z-order
        // override no-op, leaving the surface as a normal top-level window that floats above app windows.
        return GetDesktopWindow();
    }

    /// <summary>
    /// Resolves the top-level window the desktop surface must be inserted just above. The live "inner"
    /// topmost window of the desktop layer is the Explorer list-view (<c>SysListView32</c>) while desktop
    /// icons are shown, or its parent <c>SHELLDLL_DefView</c> once icons are hidden. Z-order placement is
    /// only well defined within a band, so that inner window is resolved UP to its nearest TOP-LEVEL
    /// ancestor — the exact Progman / WorkerW that hosts it — instead of a blind (possibly vestigial)
    /// Progman lookup. Never returns <see cref="IntPtr.Zero"/>.
    /// </summary>
    public static IntPtr GetDesktopAnchorHandle()
    {
        IntPtr inner = IntPtr.Zero;

        var listView = ExplorerDesktopService.FindDesktopListView();
        if (listView != IntPtr.Zero && IsWindowVisible(listView))
        {
            // Icons shown: the list-view is the live desktop content.
            inner = listView;
        }

        if (inner == IntPtr.Zero)
        {
            var defView = ExplorerDesktopService.FindDesktopSHELLDLL_DefView();
            if (defView != IntPtr.Zero && IsWindowVisible(defView))
            {
                // Icons hidden: the list-view is hidden and DefView is the live desktop content.
                inner = defView;
            }
        }

        // Walk up while the window is still a child (WS_CHILD); the first non-child ancestor of the
        // inner window is its hosting top-level root.
        IntPtr h = inner;
        while (h != IntPtr.Zero && (ManualApis.GetWindowLong(h, ManualApis.GWL_STYLE) & ManualApis.WS_CHILD) != 0)
        {
            var parent = ManualApis.GetParent(h);
            if (parent == IntPtr.Zero)
            {
                break;
            }

            h = parent;
        }

        if (h != IntPtr.Zero && (ManualApis.GetWindowLong(h, ManualApis.GWL_STYLE) & ManualApis.WS_CHILD) == 0
            && IsDesktopLayerTopLevel(h))
        {
            return h;
        }

        return GetDesktopRootHandle();
    }

    /// <summary>
    /// Glues the desktop overlay surface to the shell: it becomes a top-level (layered) window owned by
    /// the resolved desktop anchor (the top-level Progman / WorkerW that hosts the live desktop content —
    /// the <c>SysListView32</c> list-view or, when icons are hidden, its <c>SHELLDLL_DefView</c>) and is
    /// inserted just ABOVE that anchor in the z-order — so it sits above the Explorer desktop content yet
    /// below every real application window. A top-level layered window with a non-zero-alpha background
    /// reliably receives input when it is the topmost window at a point, becoming the hit target for
    /// empty-desktop input without ever covering other apps. <see cref="PreventMinimize"/> keeps the
    /// shell's "Show Desktop" from dismissing it.
    /// </summary>
    public static void GlueToDesktopSurface(IntPtr hwnd)
    {
        var root = GetDesktopAnchorHandle();
        if (root == IntPtr.Zero)
        {
            return;
        }

        // A child window can never be the hit target here: a WS_EX_LAYERED child is always painted
        // BEHIND its non-layered siblings (the Explorer DefView/list-view). So we stay a top-level layered
        // window, own it to the desktop root, stop it being minimised, and finally insert it just above
        // that root — so the final z-order (below every real app) is the one we want.
        ManualApis.SetWindowLongPtr(hwnd, ManualApis.GWL_HWNDPARENT, root);
        PreventMinimize(hwnd);

        // WS_EX_NOACTIVATE stays despite boxes being OWNED by this surface (ownership guarantees
        // Z-ORDER, not FOCUS policy). Experiment conclusion: with the style removed the surface CAN
        // take focus, but every empty-desktop click starts an activation/focus war — Explorer steals
        // focus back ~10ms later (KILLFOCUS), and during the churn both the double-click hide-all
        // gesture and the right-click desktop context menu stop working. Click-no-activate keeps the
        // surface input-transparent for focus while still receiving mouse input.
        // Also strip WS_EX_TOPMOST if anything set it: a topmost-band window floats above every normal
        // app window regardless of what insert-after handle the z-order guard forces.
        int ex = ManualApis.GetWindowLong(hwnd, ManualApis.GWL_EXSTYLE);
        ex &= ~ManualApis.WS_EX_TOPMOST;
        ManualApis.SetWindowLong(hwnd, ManualApis.GWL_EXSTYLE, ex | ManualApis.WS_EX_NOACTIVATE);

        PositionSurfaceOverDesktop(hwnd);
    }

    /// <summary>
    /// Positions the (top-level) surface over the primary work area and inserts it just ABOVE the
    /// resolved desktop anchor (<see cref="GetDesktopAnchorHandle"/> — the top-level host of the live
    /// desktop content). Because it is a top-level window — not a child — it escapes the layered-child
    /// "always behind non-layered siblings" rule, so it ends up above the Explorer list-view while staying
    /// below every real application window (apps sit above the desktop). Used on first glue and on
    /// display/DPI changes.
    /// </summary>
    public static void PositionSurfaceOverDesktop(IntPtr hwnd)
    {
        var root = GetDesktopAnchorHandle();
        if (root == IntPtr.Zero)
        {
            return;
        }

        var hmon = PInvoke.MonitorFromWindow((HWND)hwnd, MONITOR_FROM_FLAGS.MONITOR_DEFAULTTOPRIMARY);
        MONITORINFO mi = default;
        mi.cbSize = (uint)Marshal.SizeOf<MONITORINFO>();
        if (!PInvoke.GetMonitorInfo(hmon, ref mi))
        {
            return;
        }

        RECT wa = mi.rcWork;
        // Top-level window: screen coordinates, inserted just above the desktop root.
        ManualApis.SetWindowPos(
            hwnd,
            root,
            wa.left,
            wa.top,
            wa.right - wa.left,
            wa.bottom - wa.top,
            ManualApis.SWP_NOACTIVATE);
    }

    /// <summary>
    /// Returns the work-area rectangle (physical screen pixels) of the monitor that contains
    /// <paramref name="hwnd"/>, or <see langword="null"/> if it cannot be determined. Used to place
    /// dialog/surface windows on the same monitor as the window that triggered them.
    /// </summary>
    public static (int left, int top, int right, int bottom)? GetMonitorWorkArea(IntPtr hwnd)
    {
        var hmon = PInvoke.MonitorFromWindow((HWND)hwnd, MONITOR_FROM_FLAGS.MONITOR_DEFAULTTONEAREST);
        if (hmon == HWND.Null)
        {
            return null;
        }

        MONITORINFO mi = default;
        mi.cbSize = (uint)Marshal.SizeOf<MONITORINFO>();
        if (!PInvoke.GetMonitorInfo(hmon, ref mi))
        {
            return null;
        }

        return (mi.rcWork.left, mi.rcWork.top, mi.rcWork.right, mi.rcWork.bottom);
    }

    /// <summary>
    /// Removes the minimize/maximize boxes so the window cannot be minimized (by Win+D / Show Desktop
    /// or by the user). The style change is flushed with SWP_FRAMECHANGED so it takes effect immediately.
    /// </summary>
    public static void PreventMinimize(IntPtr hwnd)
    {
        int style = ManualApis.GetWindowLong(hwnd, ManualApis.GWL_STYLE);
        style = (style & ~ManualApis.WS_MAXIMIZEBOX) & ~ManualApis.WS_MINIMIZEBOX;
        ManualApis.SetWindowLong(hwnd, ManualApis.GWL_STYLE, style);

        // Flush the style change without disturbing the z-order (SWP_NOZORDER keeps whatever position the
        // caller established) so this never bumps the surface above application windows.
        ManualApis.SetWindowPos(
            hwnd,
            ManualApis.HWND_TOP,
            0,
            0,
            0,
            0,
            ManualApis.SWP_NOMOVE | ManualApis.SWP_NOSIZE | ManualApis.SWP_FRAMECHANGED | ManualApis.SWP_NOACTIVATE | ManualApis.SWP_NOZORDER);
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
    private const int SW_PARENTCLOSING = 1;
    private const int SW_SHOWNOACTIVATE = 4;

    /// <summary>
    /// HwndSource hook that keeps our desktop windows from being minimized/hidden by Show Desktop /
    /// Win+D. Blocks SC_MINIMIZE and counters a shell-initiated WM_SHOWWINDOW hide (lParam
    /// <c>SW_PARENTCLOSING</c>) by re-showing the window. Hides we trigger ourselves are preceded by
    /// <see cref="AllowHide"/>; hides from other sources (display changes, shell restarts) are not fought.
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
            if (!fShow && lParam.ToInt32() == SW_PARENTCLOSING)
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
        return ManualApis.GetClassName(hwnd, sb, 256) != 0 && sb.ToString() == ShellWindowClasses.SysListView32; //"SysListView32";
    }

    /// <summary>
    /// True when <paramref name="hwnd"/> belongs to the Explorer desktop — i.e. it is a descendant of
    /// <c>Progman</c> or <c>WorkerW</c>. This reliably covers the desktop list-view AND the app's own
    /// transparent <see cref="Views.DesktopSurface"/> (a top-level window owned by the desktop root),
    /// regardless of whether desktop icons are shown or hidden, and without depending on a specific class hierarchy.
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
                if (cls == ShellWindowClasses.ShellDefView || cls == ShellWindowClasses.Progman || cls == ShellWindowClasses.WorkerW || cls == ShellWindowClasses.SysListView32)
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
    /// Hit-tests a point (list-view CLIENT pixels) against the desktop list-view. The list-view is
    /// owned by Explorer, so the direct <c>LVM_HITTEST</c> with an in-process struct cannot work —
    /// user32 does not marshal pointer-carrying messages across processes and the struct would come
    /// back untouched. Fast-path tries the direct call anyway; when it clearly did not execute, falls
    /// back to the canonical remote-buffer technique: allocate the <c>LVHITTESTINFO</c> in Explorer's
    /// address space, send, read back.
    /// </summary>
    public static bool TryHitTestDesktopList(IntPtr listViewHwnd, ManualApis.POINT clientPt, out uint flags, out int item)
    {
        flags = 0;
        item = -1;
        if (listViewHwnd == IntPtr.Zero)
        {
            return false;
        }

        GetWindowThreadProcessId(listViewHwnd, out uint procId);
        if (procId == 0)
        {
            return false;
        }

        // Cross-process targets never get the direct call: user32 does not marshal the struct
        // pointer, yet SendMessage still returns non-zero — a "successful" result built from a
        // pointer the list-view could not legally read. Only trust in-process results, and even then
        // only when the struct was actually filled (flags/iItem of exactly 0/0 is impossible for a
        // real hit-test: empty points yield LVHT_NOWHERE + iItem -1).
        bool sameProcess = procId == ManualApis.GetCurrentProcessId();
        if (sameProcess)
        {
            var local = new ManualApis.LVHITTESTINFO { pt = clientPt };
            ManualApis.SendMessage(listViewHwnd, ManualApis.LVM_HITTEST, IntPtr.Zero, ref local);
            if (local.flags != 0 || local.iItem != 0)
            {
                flags = local.flags;
                item = local.iItem;
                return true;
            }

            return false;
        }

        // Remote path.
        IntPtr hProc = ManualApis.OpenProcess(
            ManualApis.PROCESS_VM_OPERATION | ManualApis.PROCESS_VM_READ | ManualApis.PROCESS_VM_WRITE,
            false,
            procId);
        if (hProc == IntPtr.Zero)
        {
            return false;
        }

        try
        {
            const uint MEM_COMMIT = 0x1000;
            const uint MEM_RESERVE = 0x2000;
            const uint MEM_RELEASE = 0x8000;
            const uint PAGE_READWRITE = 0x04;

            uint size = (uint)Marshal.SizeOf<ManualApis.LVHITTESTINFO>();
            IntPtr remote = ManualApis.VirtualAllocEx(hProc, IntPtr.Zero, size, MEM_COMMIT | MEM_RESERVE, PAGE_READWRITE);
            if (remote == IntPtr.Zero)
            {
                return false;
            }

            try
            {
                // Only the point needs writing; VirtualAllocEx memory is zero-initialised.
                byte[] input =
                {
                    (byte)clientPt.X, (byte)(clientPt.X >> 8), (byte)(clientPt.X >> 16), (byte)(clientPt.X >> 24),
                    (byte)clientPt.Y, (byte)(clientPt.Y >> 8), (byte)(clientPt.Y >> 16), (byte)(clientPt.Y >> 24),
                };
                if (!ManualApis.WriteProcessMemory(hProc, remote, input, (uint)input.Length, out _))
                {
                    return false;
                }

                ManualApis.SendMessage(listViewHwnd, ManualApis.LVM_HITTEST, IntPtr.Zero, remote);
                var outBytes = new byte[size];
                if (!ManualApis.ReadProcessMemory(hProc, remote, outBytes, size, out _) || outBytes.Length < 16)
                {
                    return false;
                }

                flags = BitConverter.ToUInt32(outBytes, 8);
                item = BitConverter.ToInt32(outBytes, 12);
                return true;
            }
            finally
            {
                ManualApis.VirtualFreeEx(hProc, remote, 0, MEM_RELEASE);
            }
        }
        finally
        {
            ManualApis.CloseHandle(hProc);
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

        // Hidden desktop icons: the shell only SW_HIDEs the list-view — the items remain in it and
        // still hit-test as present, which made every point look like "on an icon". An invisible
        // list-view means there is nothing to hit: every point is empty desktop.
        if (!IsWindowVisible(listViewHwnd))
        {
            return true;
        }

        var client = screenPt;
        if (!ManualApis.ScreenToClient(listViewHwnd, ref client))
        {
            return false;
        }

        if (!TryHitTestDesktopList(listViewHwnd, client, out uint flags, out int item))
        {
            return false;
        }

        return (flags & ManualApis.LVHT_NOWHERE) != 0 || item < 0;
    }
}
