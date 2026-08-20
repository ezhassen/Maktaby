using System;
using System.Runtime.InteropServices;
using System.Windows;
using DesktopBoxesUI.Shell.Interop;
using DesktopBoxesUI.Win32.NativeMethods;

namespace DesktopBoxesUI.Shell.Services;

/// <summary>
/// Shows the real Windows shell context menu for a file/folder (or any shell item identified by its
/// absolute PIDL). This is what you get when right-clicking an item on the normal desktop.
/// </summary>
internal static class ShellContextMenu
{
    public static void ShowForPath(IntPtr hwnd, string path, Point screenPoint)
    {
        if (!TryGetAbsolutePidl(path, out var pidl))
        {
            return;
        }

        try
        {
            ShowForPidl(hwnd, pidl, screenPoint);
        }
        finally
        {
            ShellNative.ILFree(pidl);
        }
    }

    public static void ShowForPidl(IntPtr hwnd, byte[] pidlBytes, Point screenPoint)
    {
        var handle = GCHandle.Alloc(pidlBytes, GCHandleType.Pinned);
        try
        {
            ShowForPidl(hwnd, handle.AddrOfPinnedObject(), screenPoint);
        }
        finally
        {
            handle.Free();
        }
    }

    private static void ShowForPidl(IntPtr hwnd, IntPtr absolutePidl, Point screenPoint)
    {
        if (ShellNative.SHGetDesktopFolder(out IShellFolder desktop) != 0 || desktop is null)
        {
            return;
        }

        try
        {
            var pidls = new[] { absolutePidl };
            desktop.GetUIObjectOf(hwnd, 1, pidls, ShellNative.IID_IContextMenu, IntPtr.Zero, out IntPtr ctxPtr);
            if (ctxPtr == IntPtr.Zero)
            {
                return;
            }

            var ctx = (IContextMenu)Marshal.GetTypedObjectForIUnknown(ctxPtr, typeof(IContextMenu));
            try
            {
                var hMenu = ManualApis.CreatePopupMenu();
                if (hMenu == IntPtr.Zero)
                {
                    return;
                }

                try
                {
                    const uint cmfExplore = 0x00000004; // CMF_EXPLORE
                    ctx.QueryContextMenu(hMenu, 0, 1, 0x7FFF, cmfExplore);

                    int cmd = ManualApis.TrackPopupMenuEx(
                        hMenu,
                        ManualApis.TPM_RETURNCMD | ManualApis.TPM_RIGHTBUTTON,
                        (int)Math.Round(screenPoint.X),
                        (int)Math.Round(screenPoint.Y),
                        hwnd,
                        IntPtr.Zero);

                    if (cmd > 0)
                    {
                        var pici = new CMINVOKECOMMANDINFO
                        {
                            cbSize = Marshal.SizeOf<CMINVOKECOMMANDINFO>(),
                            hwnd = hwnd,
                            lpVerb = (IntPtr)(cmd - 1),
                            nShow = 1,
                        };

                        ctx.InvokeCommand(ref pici);
                    }
                }
                finally
                {
                    ManualApis.DestroyMenu(hMenu);
                }
            }
            finally
            {
                Marshal.ReleaseComObject(ctx);
            }
        }
        catch
        {
            // ignore — if the shell can't build a menu we just show nothing
        }
        finally
        {
            Marshal.ReleaseComObject(desktop);
        }
    }

    private static bool TryGetAbsolutePidl(string path, out IntPtr pidl)
    {
        pidl = IntPtr.Zero;
        if (ShellNative.SHCreateItemFromParsingName(path, IntPtr.Zero, ShellNative.IID_IShellItem, out IShellItem item) != 0 || item is null)
        {
            return false;
        }

        try
        {
            var pUnk = Marshal.GetIUnknownForObject(item);
            try
            {
                return ShellNative.SHGetIDListFromObject(pUnk, out pidl) == 0 && pidl != IntPtr.Zero;
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
}
