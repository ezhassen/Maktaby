using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Interop;
using WindowsNative;
using static WindowsNative.Win32Constants;
using DesktopBoxesUI.Win32.NativeMethods;
using Wpf.Ui.Appearance;

namespace DesktopBoxesUI.Shell.Services;

/// <summary>
/// Shows the real Windows shell context menu for a file/folder (or any shell item identified by its
/// PIDL). This is what you get when right-clicking an item on the normal desktop.
/// </summary>
internal static class ShellContextMenu
{
    public static void ShowForPath(IntPtr hwnd, string path)
    {
        if (Shell32.SHCreateItemFromParsingName(path, IntPtr.Zero, Shell32.IID_IShellItem, out IShellItem item) != 0 || item is null)
        {
            return;
        }

        try
        {
            ShowContextMenuForItem(hwnd, item);
        }
        finally
        {
            Marshal.ReleaseComObject(item);
        }
    }

    public static void ShowForPidl(IntPtr hwnd, byte[] pidlBytes)
    {
        var handle = GCHandle.Alloc(pidlBytes, GCHandleType.Pinned);
        try
        {
            ShowContextMenuForRelativePidl(hwnd, handle.AddrOfPinnedObject());
        }
        finally
        {
            handle.Free();
        }
    }

    // The PIDL persisted on a BoxItem is relative to the Desktop namespace folder. To obtain a fully
    // resolved IShellItem (and a context menu whose verbs — including a .lnk's "Open" — actually
    // execute) we must build an absolute PIDL by combining it with the Desktop folder's own PIDL.
    private static void ShowContextMenuForRelativePidl(IntPtr hwnd, IntPtr relativePidl)
    {
        if (Shell32.SHGetSpecialFolderLocation(IntPtr.Zero, 0 /* CSIDL_DESKTOP */, out IntPtr deskPidl) != 0 || deskPidl == IntPtr.Zero)
        {
            return;
        }

        try
        {
            IntPtr absPidl = Shell32.ILCombine(deskPidl, relativePidl);
            if (absPidl == IntPtr.Zero)
            {
                return;
            }

            try
            {
                if (Shell32.SHCreateItemFromIDList(absPidl, Shell32.IID_IShellItem, out IShellItem item) != 0 || item is null)
                {
                    return;
                }

                try
                {
                    ShowContextMenuForItem(hwnd, item);
                }
                finally
                {
                    Marshal.ReleaseComObject(item);
                }
            }
            finally
            {
                Shell32.ILFree(absPidl);
            }
        }
        finally
        {
            Shell32.ILFree(deskPidl);
        }
    }

    private static void ShowContextMenuForItem(IntPtr hwnd, IShellItem item)
    {
        // Obtain the context menu via BindToHandler(BHID_SFUIObject) — exactly what Explorer does. This
        // resolves every item type correctly (crucially .lnk files, whose "Open" fails when the menu is
        // taken via the desktop folder's GetUIObjectOf with a relative PIDL).
        var bhId = BHID_SFUIObject;
        var iid = Shell32.IID_IContextMenu;
        if (item.BindToHandler(IntPtr.Zero, ref bhId, ref iid, out IntPtr ctxPtr) != 0 || ctxPtr == IntPtr.Zero)
        {
            return;
        }

        var ctx = (IContextMenu)Marshal.GetTypedObjectForIUnknown(ctxPtr, typeof(IContextMenu));
        IContextMenu2? ctx2 = TryGetContextMenu2(ctxPtr);
        try
        {
            // Forward owner-draw / menu-setup messages to the shell so items such as "Run as
            // administrator" (shield icon) render correctly instead of corrupting state and crashing us.
            HwndSource? src = HwndSource.FromHwnd(hwnd);
            HwndSourceHook? hook = null;
            if (ctx2 is not null && src is not null)
            {
                hook = (h, msg, wParam, lParam, ref handled) =>
                {
                    if (msg is 0x0117 or 0x002C or 0x002B or 0x0120) // WM_INITMENUPOPUP, WM_MEASUREITEM, WM_DRAWITEM, WM_MENUCHAR
                    {
                        ctx2.HandleMenuMsg((uint)msg, wParam, lParam);
                        handled = true;
                    }

                    return IntPtr.Zero;
                };
                src.AddHook(hook);
            }

            try
            {
                var hMenu = User32.CreatePopupMenu();
                if (hMenu == IntPtr.Zero)
                {
                    return;
                }

                try
                {
                    const uint cmfExplore = 0x00000004; // CMF_EXPLORE
                    ctx.QueryContextMenu(hMenu, 0, 1, 0x7FFF, cmfExplore);

                    // Push dark menu rendering to match the app theme (Win32 HMENU is otherwise light
                    // even when the WPF app is dark). Native calls are best-effort and silently ignored
                    // on OS versions that don't support them.
                    bool isDark = IsAppDark();
                    bool pushed = Win32Apis.TryPushDarkMenuMode(hwnd, isDark);

                    // Anchor the menu at the real cursor position. GetCursorPos returns true physical
                    // screen pixels (what TrackPopupMenuEx expects); using WPF's PointToScreen with a
                    // manual DPI scale was landing the menu too far to the right.
                    User32.GetCursorPos(out POINT cursor);
                    int cmd;
                    try
                    {
                        cmd = User32.TrackPopupMenuEx(
                            hMenu,
                            TPM_RETURNCMD | TPM_RIGHTBUTTON,
                            cursor.X,
                            cursor.Y,
                            hwnd,
                            IntPtr.Zero);
                    }
                    finally
                    {
                        if (pushed)
                        {
                            Win32Apis.TryPopDarkMenuMode(hwnd);
                        }
                    }

                    if (cmd > 0)
                    {
                        uint offset = (uint)(cmd - 1);
                        string? verb = GetVerb(ctx, offset);

                        if (string.Equals(verb, "runas", StringComparison.OrdinalIgnoreCase))
                        {
                            // Elevation must go through ShellExecuteEx with the "runas" verb. Invoking it via
                            // IContextMenu.InvokeCommand with our layered, desktop-owned window as hwnd crashes
                            // the shell's UAC flow and takes down this process.
                            string? parseName = GetParsingName(item);
                            if (parseName is not null)
                            {
                                RunAsAdmin(parseName);
                            }
                        }
                        else
                        {
                            var pici = new CMINVOKECOMMANDINFO
                            {
                                cbSize = Marshal.SizeOf<CMINVOKECOMMANDINFO>(),
                                hwnd = hwnd,
                                nShow = 1,
                            };

                            IntPtr verbPtr = IntPtr.Zero;
                            if (!string.IsNullOrEmpty(verb))
                            {
                                // Invoke by canonical verb name (e.g. "open"). Passing the raw offset alone fails
                                // for some items such as .lnk files, whose "Open" only resolves via the verb.
                                verbPtr = Marshal.StringToHGlobalAnsi(verb);
                                pici.lpVerb = verbPtr;
                            }
                            else
                            {
                                pici.lpVerb = (IntPtr)offset;
                            }

                            try
                            {
                                ctx.InvokeCommand(ref pici);
                            }
                            catch
                            {
                                // Never let a shell invocation failure take down the app.
                            }
                            finally
                            {
                                if (verbPtr != IntPtr.Zero)
                                {
                                    Marshal.FreeHGlobal(verbPtr);
                                }
                            }
                        }
                    }
                }
                finally
                {
                    User32.DestroyMenu(hMenu);
                }
            }
            finally
            {
                if (src is not null && hook is not null)
                {
                    src.RemoveHook(hook);
                }
            }
        }
        finally
        {
            if (ctx2 is not null)
            {
                Marshal.ReleaseComObject(ctx2);
            }

            Marshal.ReleaseComObject(ctx);
        }
    }

    private static IContextMenu2? TryGetContextMenu2(IntPtr ctxPtr)
    {
        var iid = new Guid("000214f4-0000-0000-c000-000000000046"); // IID_IContextMenu2
        if (Marshal.QueryInterface(ctxPtr, in iid, out IntPtr p) != 0 || p == IntPtr.Zero)
        {
            return null;
        }

        try
        {
            return (IContextMenu2)Marshal.GetTypedObjectForIUnknown(p, typeof(IContextMenu2));
        }
        finally
        {
            // Release the extra reference taken by QueryInterface; the RCW keeps its own.
            Marshal.Release(p);
        }
    }

    private static readonly Guid BHID_SFUIObject = new("3981e225-f559-11d3-8e3a-00c04f6837d5");

    private const uint GCS_VERBW = 0x00000004; // GetCommandString: return the Unicode verb name

    private static string? GetParsingName(IShellItem item)
    {
        try
        {
            if (item.GetDisplayName(SHGDNF.FORPARSING, out IntPtr ptr) == 0 && ptr != IntPtr.Zero)
            {
                try
                {
                    return Marshal.PtrToStringUni(ptr);
                }
                finally
                {
                    Marshal.FreeCoTaskMem(ptr);
                }
            }
        }
        catch
        {
            // ignore
        }

        return null;
    }

    private static void RunAsAdmin(string path)
    {
        try
        {
            // Use the BCL launcher rather than a hand-rolled SHELLEXECUTEINFO: it sizes the structure
            // correctly and reliably triggers UAC for the "runas" verb.
            var psi = new ProcessStartInfo(path)
            {
                UseShellExecute = true,
                Verb = "runas",
            };

            Process.Start(psi);
        }
        catch
        {
            // User dismissed the UAC prompt or the launch failed — never take down the app.
        }
    }

    private static string? GetVerb(IContextMenu ctx, uint offset)
    {
        try
        {
            var sb = new StringBuilder(256);
            if (ctx.GetCommandString(offset, GCS_VERBW, IntPtr.Zero, sb, (uint)sb.Capacity) == 0)
            {
                return sb.ToString();
            }
        }
        catch
        {
            // ignore — fall back to invoking by offset
        }

        return null;
    }

    private static bool IsAppDark()
    {
        try
        {
            var appTheme = ApplicationThemeManager.GetAppTheme();
            if (appTheme == ApplicationTheme.Dark) return true;
            if (appTheme == ApplicationTheme.Light) return false;
            // System / Unknown — fall back to OS theme; Wpf.Ui already applied system theme to windows.
            return ApplicationThemeManager.GetSystemTheme() == SystemTheme.Dark;
        }
        catch
        {
            // Fallback: inspect app resources (BoxBackground is dark in dark theme) or assume system light.
            try
            {
                if (Application.Current?.Resources["BoxBackground"] is System.Windows.Media.SolidColorBrush b)
                {
                    // Dark header #2D2D30 vs light #F3F3F3; luminance check.
                    double lum = 0.2126 * b.Color.R + 0.7152 * b.Color.G + 0.0722 * b.Color.B;
                    return lum < 128;
                }
            }
            catch { }
            return false;
        }
    }
}
