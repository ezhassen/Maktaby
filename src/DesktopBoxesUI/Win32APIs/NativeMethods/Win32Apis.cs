using DesktopBoxesUI.Shell.Interop;
using DesktopBoxesUI.Win32.Services;
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;
using WindowsNative;
using static WindowsNative.Win32Constants;

namespace DesktopBoxesUI.Win32.NativeMethods;

/// <summary>
/// Single, isolated home for every Win32 call used by this application. All raw declarations
/// live in the shared <c>WindowsNative</c> project (hand-rolled DllImport, no CsWin32); this
/// thin, explicitly named wrapper keeps call sites behind one type so none are scattered
/// through ViewModels or Views.
/// </summary>
[SupportedOSPlatform("windows10.0.14393")]
internal static class Win32Apis
{
    public static IntPtr GetDesktopWindow() => User32.GetDesktopWindow();

    public static IntPtr GetShellWindow() => User32.GetShellWindow();

    public static IntPtr MonitorFromWindow(IntPtr hwnd, uint flags) => User32.MonitorFromWindow(hwnd, flags);

    /// <summary>Enumerates all display monitors. The callback runs synchronously, so the caller
    /// may keep the delegate in a local.</summary>
    public static bool EnumDisplayMonitors(MonitorEnumProc proc)
        => User32.EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, proc, IntPtr.Zero);

    public static bool GetMonitorInfoEx(IntPtr hMonitor, ref MONITORINFOEX info)
        => User32.GetMonitorInfoEx(hMonitor, ref info);

    /// <summary>Effective DPI for a monitor (per-monitor, unlike GetDpiForSystem).</summary>
    public static bool TryGetDpiForMonitor(IntPtr hMonitor, out uint dpiX, out uint dpiY)
    {
        dpiX = dpiY = 96;
        try
        {
            int hr = Shcore.GetDpiForMonitorTyped(hMonitor, MDT_EFFECTIVE_DPI, out dpiX, out dpiY);
            return hr == 0 && dpiX > 0 && dpiY > 0;
        }
        catch { return false; }
    }

    /// <summary>True when the window is cloaked by DWM (UWP splash, virtual-desktop hidden, ...).
    /// Cloaked windows must not count as visible fullscreen covers.</summary>
    public static bool IsWindowCloaked(IntPtr hwnd)
    {
        try
        {
            if (DwmApi.DwmGetWindowAttribute(hwnd, DWMWA_CLOAKED, out int cloaked, sizeof(int)) == 0)
                return cloaked != 0;
        }
        catch { }
        return false;
    }

    public static IntPtr MonitorFromRect(in RECT rect, uint flags) => User32.MonitorFromRect(in rect, flags);

    public static bool GetMonitorInfo(IntPtr hMonitor, ref MONITORINFO monitorInfo) => User32.GetMonitorInfo(hMonitor, ref monitorInfo);

    public static bool GetWindowRect(IntPtr hWnd, out RECT rect) => User32.GetWindowRect(hWnd, out rect);

    public static bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int x, int y, int cx, int cy, uint uFlags)
        => User32.SetWindowPos(hWnd, hWndInsertAfter, x, y, cx, cy, uFlags);

    public static uint GetDpiForSystem() => User32.GetDpiForSystem();

    /// <summary>Total I/O bytes transferred by a process (disk + network + device, cumulative).
    /// False when the handle lacks query rights or the process is gone.</summary>
    public static bool TryGetProcessIoCounters(IntPtr hProcess, out ulong readBytes, out ulong writeBytes)
    {
        readBytes = writeBytes = 0;
        try
        {
            if (Kernel32.GetProcessIoCounters(hProcess, out var counters))
            {
                readBytes = counters.ReadTransferCount;
                writeBytes = counters.WriteTransferCount;
                return true;
            }
        }
        catch { }
        return false;
    }

    /// <summary>Snapshot of pid → parent-pid for every live process (Toolhelp32). Used to
    /// attribute msedgewebview2 renderer/GPU/utility children — which are grandchildren of
    /// our process (children of the browser PID) — back to our app. Empty on failure.</summary>
    public static Dictionary<int, int> GetProcessParentMap()
    {
        var map = new Dictionary<int, int>();
        IntPtr snap = IntPtr.Zero;
        try
        {
            snap = Kernel32.CreateToolhelp32Snapshot(TH32CS_SNAPPROCESS, 0);
            if (snap == IntPtr.Zero || snap == new IntPtr(-1))
                return map;
            var entry = new PROCESSENTRY32W { Size = (uint)Marshal.SizeOf<PROCESSENTRY32W>() };
            if (!Kernel32.Process32FirstW(snap, ref entry))
                return map;
            do
            {
                map[(int)entry.ProcessID] = (int)entry.ParentProcessID;
                entry.Size = (uint)Marshal.SizeOf<PROCESSENTRY32W>();
            } while (Kernel32.Process32NextW(snap, ref entry));
        }
        catch { }
        finally
        {
            try { if (snap != IntPtr.Zero && snap != new IntPtr(-1)) Kernel32.CloseHandle(snap); } catch { }
        }
        return map;
    }

    /// <summary>Exe file name (e.g. "msedgewebview2.exe") for a pid via a Toolhelp snapshot.
    /// Empty when the process is gone or cannot be read.</summary>
    public static string GetProcessExeName(int pid)
    {
        IntPtr snap = IntPtr.Zero;
        try
        {
            snap = Kernel32.CreateToolhelp32Snapshot(TH32CS_SNAPPROCESS, 0);
            if (snap == IntPtr.Zero || snap == new IntPtr(-1))
                return string.Empty;
            var entry = new PROCESSENTRY32W { Size = (uint)Marshal.SizeOf<PROCESSENTRY32W>() };
            if (!Kernel32.Process32FirstW(snap, ref entry))
                return string.Empty;
            do
            {
                if ((int)entry.ProcessID == pid)
                    return entry.ExeFile ?? string.Empty;
                entry.Size = (uint)Marshal.SizeOf<PROCESSENTRY32W>();
            } while (Kernel32.Process32NextW(snap, ref entry));
        }
        catch { }
        finally
        {
            try { if (snap != IntPtr.Zero && snap != new IntPtr(-1)) Kernel32.CloseHandle(snap); } catch { }
        }
        return string.Empty;
    }

    /// <summary>GDI / USER handle count for a process handle (GetGuiResources). Used by
    /// diagnostics to separate handle leaks (climbing GDI) from driver-mapping / pool
    /// growth (flat GDI, climbing private bytes). Returns 0 on failure.</summary>
    public static uint GetGuiHandleCount(IntPtr hProcess, bool userObjects)
    {
        try { return User32.GetGuiResources(hProcess, userObjects ? GR_USEROBJECTS : GR_GDIOBJECTS); }
        catch { return 0; }
    }

    /// <summary>True when <paramref name="pid"/> is <paramref name="rootPid"/> or descends
    /// from it through the snapshot <paramref name="parentMap"/> (browser → renderer chains).</summary>
    public static bool IsDescendantOf(int pid, int rootPid, Dictionary<int, int> parentMap)
    {
        int cur = pid;
        for (int i = 0; i < 64; i++)
        {
            if (cur == rootPid) return true;
            if (!parentMap.TryGetValue(cur, out int parent) || parent == 0 || parent == cur)
                return false;
            cur = parent;
        }
        return false;
    }

    /// <summary>Gets the current cursor position in physical screen pixels (works during a drag operation,
    /// unlike <see cref="Mouse.GetPosition"/> which is suppressed by the drag-drop capture).</summary>
    public static bool GetCursorPos(out POINT pt) => User32.GetCursorPos(out pt);

    /// <summary>Native drag throttle with Explorer semantics: captures the mouse to
    /// <paramref name="hwnd"/> and returns true only once the cursor leaves the system drag rect
    /// from its current position, false if the button releases first (a click, never a drag).
    /// Call only after the cheap threshold check passed — this blocks the UI thread until the
    /// press/release decision, exactly like the native control does.</summary>
    public static bool ConfirmDrag(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero) return false;
        if (!GetCursorPos(out var pt)) return true;
        try { return User32.DragDetect(hwnd, pt); }
        catch { return true; }
    }

    /// <summary>Shell file operation (rename / delete / recycle). Wraps
    /// <see cref="Shell32.SHFileOperationW"/> so every native call flows through this wrapper.</summary>
    public static int FileOperation(ref SHFILEOPSTRUCT op) => Shell32.SHFileOperationW(ref op);

    /// <summary>The foreground window handle (used as the owner for shell file-operation dialogs).</summary>
    public static IntPtr GetForegroundWindow() => User32.GetForegroundWindow();

    public static uint GetDpiForWindow(IntPtr hWnd) => User32.GetDpiForWindow(hWnd);

    public static bool DestroyIcon(IntPtr hIcon) => User32.DestroyIcon(hIcon);

    public static void DeleteObject(IntPtr hBitmap) => Gdi32.DeleteObject(hBitmap);

    public static IntPtr FindWindowEx(IntPtr hWndParent, IntPtr hWndChildAfter, string? lpClassName, string? lpWindowName)
        => User32.FindWindowEx(hWndParent, hWndChildAfter, lpClassName, lpWindowName);

    public static bool ShowWindow(IntPtr hWnd, int nCmdShow) => User32.ShowWindow(hWnd, nCmdShow);

    public static bool IsWindowVisible(IntPtr hWnd) => User32.IsWindowVisible(hWnd);

    public static bool IsWindow(IntPtr hWnd) => User32.IsWindow(hWnd);

    public static IntPtr GetWindow(IntPtr hWnd, uint uCmd) => User32.GetWindow(hWnd, uCmd);

    public static IntPtr LoadArrowCursor() => User32.LoadCursor(IntPtr.Zero, (IntPtr)32512 /* IDC_ARROW */);

    public static IntPtr SetCursor(IntPtr hCursor) => User32.SetCursor(hCursor);

    public static void TrimWorkingSet()
    {
        try { PsApi.EmptyWorkingSet(System.Diagnostics.Process.GetCurrentProcess().Handle); } catch { }
    }

    /// <summary>Registers a system broadcast message (e.g. "TaskbarCreated" after Explorer restarts).</summary>
    public static bool SetCursorPos(int X, int Y) => User32.SetCursorPos(X, Y);

    public static uint RegisterWindowMessage(string message) => User32.RegisterWindowMessage(message);

    /// <summary>Nudges the cursor by 1px — forces the OS to deliver a fresh
    /// WM_MOUSEMOVE to whatever window is under it, priming WPF's hover state.</summary>
    public static void NudgeCursor()
    {
        if (GetCursorPos(out var pt))
        {
            SetCursorPos(pt.X + 1, pt.Y);
        }
    }

    /// <summary>Returns the window's class name, or <see cref="string.Empty"/> if it cannot be read.</summary>
    public static string GetWindowClass(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero)
        {
            return string.Empty;
        }

        var sb = new StringBuilder(256);
        return User32.GetClassName(hwnd, sb, 256) != 0 ? sb.ToString() : string.Empty;
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
            int ex = User32.GetWindowLong(hwnd, GWL_EXSTYLE);
            User32.SetWindowLong(hwnd, GWL_EXSTYLE, ex | (int)WS_EX_TRANSPARENT);
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
        User32.SendMessage(defView, WmCommand, (IntPtr)0x7402, IntPtr.Zero);
    }

    private static IntPtr FindDesktopFolderView()
    {
        IntPtr progman = Win32Apis.FindWindowEx(IntPtr.Zero, IntPtr.Zero, "Progman", "Program Manager");
        IntPtr defView = Win32Apis.FindWindowEx(progman, IntPtr.Zero, "SHELLDLL_DefView", null);
        if (defView != IntPtr.Zero)
        {
            return defView;
        }

        IntPtr worker = Win32Apis.FindWindowEx(IntPtr.Zero, IntPtr.Zero, "WorkerW", null);
        while (worker != IntPtr.Zero)
        {
            defView = Win32Apis.FindWindowEx(worker, IntPtr.Zero, "SHELLDLL_DefView", null);
            if (defView != IntPtr.Zero)
            {
                return defView;
            }

            worker = Win32Apis.FindWindowEx(IntPtr.Zero, worker, "WorkerW", null);
        }

        return IntPtr.Zero;
    }

    public static int SHGetFileInfo(string? pszPath, uint dwFileAttributes, ref SHFILEINFOW psfi, uint cbFileInfo, SHGFI uFlags)
        => Shell32.SHGetFileInfo(pszPath, dwFileAttributes, ref psfi, cbFileInfo, (uint)uFlags);

    public static int SHGetFileInfo(IntPtr pidl, uint dwFileAttributes, ref SHFILEINFOW psfi, uint cbFileInfo, SHGFI uFlags)
        => Shell32.SHGetFileInfoPidl(pidl, dwFileAttributes, ref psfi, cbFileInfo, (uint)uFlags | (uint)SHGFI.Pidl);

    public static IntPtr SetWinEventHook(uint eventMin, uint eventMax, IntPtr hmodWinEventProc, WinEventProc pfnWinEventProc, uint idProcess, uint idThread, uint dwFlags)
        => User32.SetWinEventHook(eventMin, eventMax, hmodWinEventProc, pfnWinEventProc, idProcess, idThread, dwFlags);

    public static bool UnhookWinEvent(IntPtr hWinEventHook)
        => User32.UnhookWinEvent(hWinEventHook);

    public static bool ShellNotifyIcon(uint dwMessage, ref NOTIFYICONDATA data)
        => Shell32.Shell_NotifyIcon(dwMessage, ref data);

    /// <summary>Returns the bounding rectangle of the tray icon in physical screen pixels (S_OK on success).</summary>
    public static bool ShellNotifyIconGetRect(IntPtr hWnd, uint uID, out RECT rect)
    {
        var id = new NOTIFYICONIDENTIFIER
        {
            Size = (uint)Marshal.SizeOf<NOTIFYICONIDENTIFIER>(),
            Hwnd = hWnd,
            Id = uID,
        };
        return Shell32.Shell_NotifyIconGetRect(ref id, out rect) == 0;
    }

    public static short GetAsyncKeyState(int vKey) => User32.GetAsyncKeyState(vKey);

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
                    IntPtr absolute = Shell32.ILCombine(parent, item);
                    if (absolute == IntPtr.Zero)
                    {
                        continue;
                    }

                    var entry = new ShellItemEntry();
                    try
                    {
                        var sb = new StringBuilder(260);
                        if (Shell32.SHGetPathFromIDListW(absolute, sb) != 0 && sb.Length > 0)
                        {
                            entry.FilePath = sb.ToString();
                        }

                        entry.Pidl = CopyPidl(absolute);
                    }
                    finally
                    {
                        Shell32.ILFree(absolute);
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
            if (Shell32.SHCreateItemFromIDList(ptr, Shell32.IID_IShellItem, out IShellItem item) != 0 || item == null)
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
            if (Shell32.SHCreateItemFromIDList(ptr, Shell32.IID_IShellItem, out IShellItem item) != 0 || item is null)
            {
                return IntPtr.Zero;
            }

            try
            {
                if (item is IShellItemImageFactory factory)
                {
                    var size = new SIZE { Cx = 32, Cy = 32 };
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
            if (Shell32.SHCreateItemFromParsingName(path, IntPtr.Zero, Shell32.IID_IShellItem, out IShellItem item) != 0 || item is null)
            {
                return IntPtr.Zero;
            }

            try
            {
                if (item is IShellItemImageFactory factory)
                {
                    var size = new SIZE { Cx = 32, Cy = 32 };
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
            if (Shell32.SHGetPathFromIDListW(ptr, sb) != 0 && sb.Length > 0)
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
            var linkType = Type.GetTypeFromCLSID(Shell32.CLSID_ShellLink);
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
            // If we have a filesystem path, try direct properties first (fast path for normal files/folders).
            // Use NO_UI to suppress error UI; if direct fails (e.g., user profile known folder), fall back to IDLIST via parsing name.
            if (!string.IsNullOrWhiteSpace(path))
            {
                bool exists = false;
                try { exists = System.IO.File.Exists(path) || System.IO.Directory.Exists(path); } catch { exists = false; }
                if (exists)
                {
                    var direct = new SHELLEXECUTEINFO
                    {
                        cbSize = Marshal.SizeOf<SHELLEXECUTEINFO>(),
                        fMask = SEE_MASK.NO_UI,
                        hwnd = hwnd,
                        lpVerb = "properties",
                        lpFile = path,
                        nShow = 1, // SW_SHOWNORMAL
                    };
                    if (Shell32.ShellExecuteEx(ref direct))
                        return;
                    // Direct failed (e.g., user profile, known folder) -> try IDLIST from parsing name with NO_UI to avoid MessageBox
                    try
                    {
                        if (Shell32.SHCreateItemFromParsingName(path, IntPtr.Zero, Shell32.IID_IShellItem, out var item) == 0 && item != null)
                        {
                            IntPtr punk = IntPtr.Zero;
                            try
                            {
                                punk = Marshal.GetIUnknownForObject(item);
                                if (Shell32.SHGetIDListFromObject(punk, out IntPtr pidl) == 0 && pidl != IntPtr.Zero)
                                {
                                    try
                                    {
                                        var idlInfo = new SHELLEXECUTEINFO
                                        {
                                            cbSize = Marshal.SizeOf<SHELLEXECUTEINFO>(),
                                            fMask = SEE_MASK.INVOKEIDLIST | SEE_MASK.NO_UI,
                                            hwnd = hwnd,
                                            lpVerb = "properties",
                                            lpIDList = pidl,
                                            nShow = 1,
                                        };
                                        if (Shell32.ShellExecuteEx(ref idlInfo))
                                            return;
                                    }
                                    finally { Marshal.FreeCoTaskMem(pidl); }
                                }
                            }
                            finally
                            {
                                if (punk != IntPtr.Zero) Marshal.Release(punk);
                                Marshal.ReleaseComObject(item);
                            }
                        }
                    }
                    catch { }
                }
                else
                {
                    // Path doesn't exist as filesystem (maybe placeholder) - still try direct, let shell handle
                    var direct2 = new SHELLEXECUTEINFO
                    {
                        cbSize = Marshal.SizeOf<SHELLEXECUTEINFO>(),
                        fMask = SEE_MASK.NO_UI,
                        hwnd = hwnd,
                        lpVerb = "properties",
                        lpFile = path,
                        nShow = 1,
                    };
                    if (Shell32.ShellExecuteEx(ref direct2))
                        return;
                }
                // For known folders like user profile where ShellExecuteEx fails, try SHObjectProperties directly (no error UI)
                if (!string.IsNullOrWhiteSpace(path))
                {
                    try { if (Shell32.SHObjectProperties(hwnd, 2 /*SHOP_FILEPATH*/, path, null)) return; } catch { }
                }
            }

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
                    if (Shell32.SHGetSpecialFolderLocation(IntPtr.Zero, 0 /* CSIDL_DESKTOP */, out IntPtr deskPidl) == 0 && deskPidl != IntPtr.Zero)
                    {
                        try
                        {
                            IntPtr absPidl = Shell32.ILCombine(deskPidl, relPtr);
                            if (absPidl != IntPtr.Zero)
                            {
                                try
                                {
                                    info.fMask = SEE_MASK.INVOKEIDLIST;
                                    info.lpIDList = absPidl;
                                    info.lpFile = null;
                                    Shell32.ShellExecuteEx(ref info);
                                }
                                finally
                                {
                                    Shell32.ILFree(absPidl);
                                }

                                return;
                            }
                        }
                        finally
                        {
                            Shell32.ILFree(deskPidl);
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

            Shell32.ShellExecuteEx(ref info);
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

            return Shell32.ShellExecuteEx(ref info);
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
            if (Shell32.SHCreateItemFromParsingName(parsing, IntPtr.Zero, Shell32.IID_IShellItem, out IShellItem item) != 0 || item == null)
            {
                return false;
            }

            try
            {
                var pUnk = Marshal.GetIUnknownForObject(item);
                try
                {
                    if (Shell32.SHGetIDListFromObject(pUnk, out var pidlPtr) != 0 || pidlPtr == IntPtr.Zero)
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
                        Shell32.ILFree(pidlPtr);
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
        int ex = User32.GetWindowLong(hwnd, GWL_EXSTYLE);
        User32.SetWindowLong(hwnd, GWL_EXSTYLE, ex | (int)WS_EX_TOOLWINDOW);
        User32.SetWindowPos(
            hwnd,
            IntPtr.Zero,
            0,
            0,
            0,
            0,
            SWP_NOMOVE | SWP_NOSIZE | SWP_NOZORDER | SWP_FRAMECHANGED | SWP_NOACTIVATE);
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
            ownerHwnd = User32.FindWindowEx(IntPtr.Zero, IntPtr.Zero, ShellWindowClasses.Progman, null);
        }

        if (ownerHwnd == IntPtr.Zero)
        {
            return;
        }

        User32.SetWindowLongPtr(hwnd, GWL_HWNDPARENT, ownerHwnd);

        // Enforce tool-window semantics on every glue: after an Explorer restart the new taskbar can
        // briefly classify re-owned boxes as regular windows (stale-owner / WPF style churn), which
        // put some of them into the taskbar. WS_EX_TOOLWINDOW excludes a window from the taskbar and
        // Alt-Tab permanently; FRAMECHANGED flushes it immediately.
        MakeToolWindow(hwnd);

        // Place the window DIRECTLY ABOVE its owner (surface or desktop anchor). This anchors the
        // box in the correct z-band: above the desktop content but below all running applications.
        // Using the owner handle (not HWND_TOP/HWND_BOTTOM) means the OS maintains the ordering
        // through the ownership chain — no fighting with other windows.
        User32.SetWindowPos(hwnd, ownerHwnd, 0, 0, 0, 0,
            SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE);
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
            var parent = User32.GetParent(defView);
            if (parent != IntPtr.Zero)
            {
                return parent;
            }
        }

        // Fallback: any Progman window.
        var progman = User32.FindWindowEx(IntPtr.Zero, IntPtr.Zero, ShellWindowClasses.Progman, null);
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
        while (h != IntPtr.Zero && (User32.GetWindowLong(h, GWL_STYLE) & WS_CHILD) != 0)
        {
            var parent = User32.GetParent(h);
            if (parent == IntPtr.Zero)
            {
                break;
            }

            h = parent;
        }

        if (h != IntPtr.Zero && (User32.GetWindowLong(h, GWL_STYLE) & WS_CHILD) == 0
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
        User32.SetWindowLongPtr(hwnd, GWL_HWNDPARENT, root);
        PreventMinimize(hwnd);

        // Same self-healing as GlueToDesktop: after an Explorer restart the new taskbar may classify
        // the re-glued surface as a regular window and give it a taskbar button. WS_EX_TOOLWINDOW
        // excludes it permanently.
        MakeToolWindow(hwnd);

        // Hit-test + activate: surface is hit-testable (#01000000) and now activatable so any
        // click deactivates Start Menu reliably (also covers hidden-icons where PostMessage to hidden
        // list-view does not dismiss shell). Previous WS_EX_NOACTIVATE avoided focus war (~10ms
        // Explorer KILLFOCUS) that broke double-click/context menu; now handled via WM_MOUSEACTIVATE
        // -> MA_ACTIVATE for any button while keeping OLE drop path intact.
        // Also strip WS_EX_TOPMOST if anything set it: a topmost-band window floats above every normal
        // app window regardless of what insert-after handle the z-order guard forces.
        int ex = User32.GetWindowLong(hwnd, GWL_EXSTYLE);
        ex &= ~(int)WS_EX_TOPMOST;
        ex &= ~(int)WS_EX_NOACTIVATE;
        User32.SetWindowLong(hwnd, GWL_EXSTYLE, ex);
        //User32.SetWindowLong(hwnd, GWL_EXSTYLE, ex | WS_EX_NOACTIVATE);

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

        var hmon = User32.MonitorFromWindow(hwnd, MONITOR_DEFAULTTOPRIMARY);
        MONITORINFO mi = default;
        mi.Size = (uint)Marshal.SizeOf<MONITORINFO>();
        if (!User32.GetMonitorInfo(hmon, ref mi))
        {
            return;
        }

        RECT wa = mi.Work;
        // Top-level window: screen coordinates, inserted just above the desktop root.
        User32.SetWindowPos(
            hwnd,
            root,
            wa.Left,
            wa.Top,
            wa.Right - wa.Left,
            wa.Bottom - wa.Top,
            SWP_NOACTIVATE);
    }

    /// <summary>
    /// Returns the work-area rectangle (physical screen pixels) of the monitor that contains
    /// <paramref name="hwnd"/>, or <see langword="null"/> if it cannot be determined. Used to place
    /// dialog/surface windows on the same monitor as the window that triggered them.
    /// </summary>
    public static (int left, int top, int right, int bottom)? GetMonitorWorkArea(IntPtr hwnd)
    {
        var hmon = User32.MonitorFromWindow(hwnd, MONITOR_DEFAULTTONEAREST);
        if (hmon == IntPtr.Zero)
        {
            return null;
        }

        MONITORINFO mi = default;
        mi.Size = (uint)Marshal.SizeOf<MONITORINFO>();
        if (!User32.GetMonitorInfo(hmon, ref mi))
        {
            return null;
        }

        return (mi.Work.Left, mi.Work.Top, mi.Work.Right, mi.Work.Bottom);
    }

    /// <summary>Work area (physical screen pixels) of the monitor containing a point, taskbar excluded.
    /// Used to keep popups anchored at raw points inside visible desktop space.</summary>
    public static (int left, int top, int right, int bottom)? GetMonitorWorkAreaAtPoint(POINT pt)
    {
        var hmon = User32.MonitorFromRect(
            new RECT(pt.X, pt.Y, pt.X + 1, pt.Y + 1),
            MONITOR_DEFAULTTONEAREST);
        if (hmon == IntPtr.Zero)
        {
            return null;
        }

        MONITORINFO mi = default;
        mi.Size = (uint)Marshal.SizeOf<MONITORINFO>();
        if (!User32.GetMonitorInfo(hmon, ref mi))
        {
            return null;
        }

        return (mi.Work.Left, mi.Work.Top, mi.Work.Right, mi.Work.Bottom);
    }

    /// <summary>
    /// Removes the minimize/maximize boxes so the window cannot be minimized (by Win+D / Show Desktop
    /// or by the user). The style change is flushed with SWP_FRAMECHANGED so it takes effect immediately.
    /// </summary>
    public static void PreventMinimize(IntPtr hwnd)
    {
        int style = User32.GetWindowLong(hwnd, GWL_STYLE);
        style = (style & ~WS_MAXIMIZEBOX) & ~WS_MINIMIZEBOX;
        User32.SetWindowLong(hwnd, GWL_STYLE, style);

        // Flush the style change without disturbing the z-order (SWP_NOZORDER keeps whatever position the
        // caller established) so this never bumps the surface above application windows.
        User32.SetWindowPos(
            hwnd,
            HWND_TOP,
            0,
            0,
            0,
            0,
            SWP_NOMOVE | SWP_NOSIZE | SWP_FRAMECHANGED | SWP_NOACTIVATE | SWP_NOZORDER);
    }

    /// <summary>
    /// Forces WS_EX_NOACTIVATE on a window that must never steal activation (the widget chrome
    /// overlay: it forwards activation to its owner instead). The style change is flushed with
    /// SWP_FRAMECHANGED so it takes effect immediately, without moving, sizing, reordering or
    /// activating the window.
    /// </summary>
    public static void EnforceNoActivate(IntPtr hwnd)
    {
        int ex = User32.GetWindowLong(hwnd, GWL_EXSTYLE);
        User32.SetWindowLong(hwnd, GWL_EXSTYLE, ex | (int)WS_EX_NOACTIVATE);
        User32.SetWindowPos(
            hwnd,
            HWND_TOP,
            0,
            0,
            0,
            0,
            SWP_NOMOVE | SWP_NOSIZE | SWP_NOZORDER | SWP_FRAMECHANGED | SWP_NOACTIVATE);
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

    /// <summary>Set while the app/session is tearing down (logoff, shutdown, explicit Exit): the
    /// minimize-prevention hook stops countering hides so Windows can close our windows freely.</summary>
    public static bool SystemTeardown;

    /// <summary>
    /// HwndSource hook that keeps our desktop windows from being minimized/hidden by Show Desktop /
    /// Win+D. Blocks SC_MINIMIZE and counters a shell-initiated WM_SHOWWINDOW hide (lParam
    /// <c>SW_PARENTCLOSING</c>) by re-showing the window. Hides we trigger ourselves are preceded by
    /// <see cref="AllowHide"/>; hides from other sources (display changes, shell restarts) are not fought.
    /// </summary>
    public static IntPtr MinimizePreventionHook(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        // Teardown in progress (shutdown/logoff/app exit): owner chains are collapsing by design —
        // countering those hides would fight Windows and delay the session from ending.
        if (SystemTeardown)
        {
            return IntPtr.Zero;
        }

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
                    User32.ShowWindow(hwnd, SW_SHOWNOACTIVATE);
                    handled = true;
                    return IntPtr.Zero;
                }
            }
        }

        return IntPtr.Zero;
    }

    // --- Low-level mouse hook (used to detect clicks outside the app, e.g. the bare desktop) ---

    public const int WH_MOUSE_LL = 14;

    public static IntPtr SetWindowsHookEx(int idHook, HookProc lpfn, IntPtr hMod, uint dwThreadId)
        => User32.SetWindowsHookEx(idHook, lpfn, hMod, dwThreadId);

    public static bool UnhookWindowsHookEx(IntPtr hhk) => User32.UnhookWindowsHookEx(hhk);

    public static IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam)
        => User32.CallNextHookEx(hhk, nCode, wParam, lParam);

    public static uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId)
        => User32.GetWindowThreadProcessId(hWnd, out lpdwProcessId);

    public static IntPtr GetModuleHandle(string? moduleName) => Kernel32.GetModuleHandle(moduleName);

    public static IntPtr WindowFromPoint(POINT pt) => User32.WindowFromPoint(pt);

    /// <summary>Moves focus to <paramref name="hWnd"/> (e.g. the desktop shell window) so a previously
    /// focused window loses focus. Used to defocus the active box when the user clicks empty desktop.</summary>
    public static bool SetForegroundWindow(IntPtr hWnd) => User32.SetForegroundWindow(hWnd);

    /// <summary>Registers the window to receive <c>WM_MOUSELEAVE</c> when the cursor leaves its bounds.</summary>
    public static bool TrackMouseEvent(IntPtr hwnd)
    {
        var tme = new TRACKMOUSEEVENT
        {
            cbSize = (uint)System.Runtime.InteropServices.Marshal.SizeOf<TRACKMOUSEEVENT>(),
            dwFlags = TME_LEAVE,
            hwndTrack = hwnd,
            dwHoverTime = 0,
        };
        return User32.TrackMouseEvent(ref tme);
    }

    /// <summary>True when <paramref name="hwnd"/> is a <c>SysListView32</c> (the desktop list-view among others).</summary>
    public static bool IsSysListView32(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero)
        {
            return false;
        }

        var sb = new StringBuilder(256);
        return User32.GetClassName(hwnd, sb, 256) != 0 && sb.ToString() == ShellWindowClasses.SysListView32; //"SysListView32";
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
            if (User32.GetClassName(h, sb, 256) != 0)
            {
                string cls = sb.ToString();
                if (cls == ShellWindowClasses.ShellDefView || cls == ShellWindowClasses.Progman || cls == ShellWindowClasses.WorkerW || cls == ShellWindowClasses.SysListView32)
                {
                    return true;
                }
            }

            h = User32.GetParent(h);
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
    public static bool TryHitTestDesktopList(IntPtr listViewHwnd, POINT clientPt, out uint flags, out int item)
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
        bool sameProcess = procId == Kernel32.GetCurrentProcessId();
        if (sameProcess)
        {
            var local = new LVHITTESTINFO { pt = clientPt };
            User32.SendMessage(listViewHwnd, LVM_HITTEST, IntPtr.Zero, ref local);
            if (local.flags != 0 || local.iItem != 0)
            {
                flags = local.flags;
                item = local.iItem;
                return true;
            }

            return false;
        }

        // Remote path.
        IntPtr hProc = Kernel32.OpenProcess(
            PROCESS_VM_OPERATION | PROCESS_VM_READ | PROCESS_VM_WRITE,
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

            uint size = (uint)Marshal.SizeOf<LVHITTESTINFO>();
            IntPtr remote = Kernel32.VirtualAllocEx(hProc, IntPtr.Zero, size, MEM_COMMIT | MEM_RESERVE, PAGE_READWRITE);
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
                if (!Kernel32.WriteProcessMemory(hProc, remote, input, (uint)input.Length, out _))
                {
                    return false;
                }

                User32.SendMessage(listViewHwnd, LVM_HITTEST, IntPtr.Zero, remote);
                var outBytes = new byte[size];
                if (!Kernel32.ReadProcessMemory(hProc, remote, outBytes, size, out _) || outBytes.Length < 16)
                {
                    return false;
                }

                flags = BitConverter.ToUInt32(outBytes, 8);
                item = BitConverter.ToInt32(outBytes, 12);
                return true;
            }
            finally
            {
                Kernel32.VirtualFreeEx(hProc, remote, 0, MEM_RELEASE);
            }
        }
        finally
        {
            Kernel32.CloseHandle(hProc);
        }
    }

    /// <summary>
    /// True when <paramref name="screenPt"/> (physical screen pixels) falls on empty desktop area
    /// within the given desktop list-view — i.e. not on a desktop icon. Uses <c>LVM_HITTEST</c> so a
    /// double-click that hits an icon leaves the icon's own open behavior intact.
    /// </summary>
    public static bool IsDesktopEmptyPoint(IntPtr listViewHwnd, POINT screenPt)
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
        if (!User32.ScreenToClient(listViewHwnd, ref client))
        {
            return false;
        }

        if (!TryHitTestDesktopList(listViewHwnd, client, out uint flags, out int item))
        {
            return false;
        }

        return (flags & LVHT_NOWHERE) != 0 || item < 0;
    }

    /// <summary>
    /// Best-effort: pushes the immersive dark mode + menu theme for the calling thread / window so a
    /// native HMENU shown via TrackPopupMenuEx renders dark when the app is dark (and light when light),
    /// even when the OS system theme is the opposite. All native calls are guarded; failure is silent.
    /// Returns true when a FlushMenuThemes was issued and the caller should restore to Default after the popup.
    /// </summary>
    public static bool TryPushDarkMenuMode(IntPtr hwnd, bool isDark)
    {
        bool flushed = false;
        try
        {
            // 1) Per-window immersive dark (titlebar + popup frame on Win11 22H2+). Harmless if unsupported.
            int v = isDark ? 1 : 0;
            DwmApi.DwmSetWindowAttribute(hwnd, DWMWA_USE_IMMERSIVE_DARK_MODE, ref v, sizeof(int));
        }
        catch { }

        try
        {
            // 2) Per-theme for classic menus: "DarkMode_Explorer" vs "Explorer" controls menu rendering.
            UxTheme.SetWindowTheme(hwnd, isDark ? "DarkMode_Explorer" : "Explorer", null);
        }
        catch { }

        // 3) Undocumented thread-wide menu mode (uxtheme ordinals #135/#136). This is the only mechanism
        // that reliably forces an HMENU dark on Win10/11 even when the OS light theme is active.
        // Values: 0 Default, 1 AllowDark, 2 ForceDark, 3 ForceLight, 4 Max. Guarded for older OS.
        try
        {
            UxTheme.SetPreferredAppMode(isDark ? 2 /*ForceDark*/ : 3 /*ForceLight*/);
            UxTheme.FlushMenuThemes();
            flushed = true;
        }
        catch { }

        return flushed;
    }

    public static void TryPopDarkMenuMode(IntPtr _)
    {
        try { UxTheme.SetPreferredAppMode(0 /*Default*/); UxTheme.FlushMenuThemes(); } catch { }
        // Do not touch SetWindowTheme / DwmSetWindowAttribute here: those are per-window and owned by
        // Wpf.Ui's ApplicationThemeManager. Resetting them to "Explorer" / 0 would fight the app theme
        // (e.g. leave a dark window with a light popup theme until the next theme apply).
    }

    /// <summary>
    /// Best-effort Start Menu visibility check. On Win10/11 the menu is a visible top-level
    /// <c>Windows.UI.Core.CoreWindow</c> titled "Start" (Win10) or hosted in an
    /// <c>XamlExplorerHostIslandWindow</c>. Enum top-level windows and match those.
    /// </summary>
    public static bool IsStartMenuVisible()
    {
        bool visible = false;
        try
        {
            User32.EnumWindows((hWnd, _) =>
            {
                if (!User32.IsWindowVisible(hWnd)) return true;
                var sb = new StringBuilder(256);
                User32.GetClassName(hWnd, sb, sb.Capacity);
                string cls = sb.ToString();
                if (cls == "Windows.UI.Core.CoreWindow" || cls == "XAML Explorer Host Island Window" || cls == "XamlExplorerHostIslandWindow")
                {
                    var tb = new StringBuilder(256);
                    User32.GetWindowText(hWnd, tb, tb.Capacity);
                    string title = tb.ToString();
                    if (title.Equals("Start", StringComparison.OrdinalIgnoreCase) || title.Equals("Search", StringComparison.OrdinalIgnoreCase))
                    {
                        visible = true;
                        return false;
                    }
                }
                // Fallback: Windows 11 Start uses ApplicationFrameWindow hosting Start
                if (cls == "ApplicationFrameWindow")
                {
                    var tb = new StringBuilder(256);
                    User32.GetWindowText(hWnd, tb, tb.Capacity);
                    if (tb.ToString().Contains("Start", StringComparison.OrdinalIgnoreCase))
                    {
                        visible = true;
                        return false;
                    }
                }
                return true;
            }, IntPtr.Zero);
        }
        catch { }
        return visible;
    }

    public static void DismissStartMenu()
    {
        try
        {
            // ESC dismisses Start without side effects; safe even if not visible.
            User32.keybd_event(VK_ESCAPE, 0, 0, UIntPtr.Zero);
            User32.keybd_event(VK_ESCAPE, 0, KEYEVENTF_KEYUP, UIntPtr.Zero);
        }
        catch { }
    }
}
