using System;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;
using DesktopBoxesUI.Core.Interfaces;
using DesktopBoxesUI.Core.Models;
using DesktopBoxesUI.Core.Services;
using DesktopBoxesUI.Shell.Interop;
using DesktopBoxesUI.Win32.NativeMethods;

namespace DesktopBoxesUI.Shell.Services;

/// <summary>
/// Watches the desktop namespace for additions/removals of ANY item (files, folders and virtual
/// shell items such as "This PC" or "Recycle Bin") using <c>SHChangeNotifyRegister</c>. A hidden
/// message-only window receives the notifications; each affected PIDL is resolved to a
/// <see cref="BoxItem"/> (mirroring <see cref="DesktopService"/>'s shape, so items seeded at startup
/// and items observed live share the same <see cref="BoxItem.Path"/> and de-duplicate correctly).
/// </summary>
[SupportedOSPlatform("windows10.0.14393")]
public sealed class ShellDesktopWatcher : IShellWatcherService, IDisposable
{
    private const uint WmNotify = ManualApis.WM_USER + 1;

    private readonly object _gate = new();
    private ManualApis.WndProc? _wndProc;
    private string? _className;
    private ushort _atom;
    private IntPtr _hwnd;
    private uint _regId;

    public event Action<BoxItem>? ItemCreated;
    public event Action<BoxItem>? ItemDeleted;

    public void Start()
    {
        if (_hwnd != IntPtr.Zero)
        {
            return;
        }

        EnsureWindow();
    }

    private void EnsureWindow()
    {
        _wndProc = WndProc;
        _className = "DesktopBoxesShellWatch_" + Guid.NewGuid().ToString("N");

        var wc = new ManualApis.WNDCLASSEX
        {
            cbSize = Marshal.SizeOf<ManualApis.WNDCLASSEX>(),
            lpfnWndProc = _wndProc,
            hInstance = ManualApis.GetModuleHandle(null),
            lpszClassName = _className,
        };

        _atom = ManualApis.RegisterClassEx(ref wc);
        if (_atom == 0)
        {
            return;
        }

        _hwnd = ManualApis.CreateWindowEx(
            0,
            _className,
            "DesktopBoxesShellWatch",
            0,
            0, 0, 0, 0,
            ManualApis.HWND_MESSAGE,
            IntPtr.Zero,
            IntPtr.Zero,
            IntPtr.Zero);

        if (_hwnd == IntPtr.Zero)
        {
            return;
        }

        if (ManualApis.SHGetSpecialFolderLocation(IntPtr.Zero, 0 /* CSIDL_DESKTOP */, out IntPtr deskPidl) != 0)
        {
            return;
        }

        var entry = new ShellNative.SHChangeNotifyEntry
        {
            pidl = deskPidl,
            fRecursive = 0,
        };

        _regId = ShellNative.SHChangeNotifyRegister(
            _hwnd,
            ShellNative.SHCNRF.InterruptLevel | ShellNative.SHCNRF.ShellLevel | ShellNative.SHCNRF.NewDelivery,
            ShellNative.SHCNE.CREATE | ShellNative.SHCNE.DELETE | ShellNative.SHCNE.RENAMEITEM | ShellNative.SHCNE.RENAMEFOLDER,
            WmNotify,
            1,
            ref entry);

        ManualApis.ILFree(deskPidl);
    }

    private IntPtr WndProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam)
    {
        if (msg == WmNotify)
        {
            IntPtr lockHandle = ShellNative.SHChangeNotification_Lock(wParam, (uint)lParam, out IntPtr pppidl, out int lEvent);
            if (lockHandle != IntPtr.Zero)
            {
                try
                {
                    HandleEvent(lEvent, pppidl);
                }
                finally
                {
                    ShellNative.SHChangeNotification_Unlock(lockHandle);
                }
            }

            return IntPtr.Zero;
        }

        return ManualApis.DefWindowProc(hWnd, msg, wParam, lParam);
    }

    private void HandleEvent(int lEvent, IntPtr pppidl)
    {
        var pidl1 = Marshal.ReadIntPtr(pppidl, 0);

        switch (lEvent)
        {
            case (int)ShellNative.SHCNE.CREATE:
            case (int)ShellNative.SHCNE.UPDATEITEM:
                RaiseCreated(Resolve(pidl1));
                break;

            case (int)ShellNative.SHCNE.DELETE:
                RaiseDeleted(Resolve(pidl1));
                break;

            case (int)ShellNative.SHCNE.RENAMEITEM:
            case (int)ShellNative.SHCNE.RENAMEFOLDER:
                var pidl2 = Marshal.ReadIntPtr(pppidl, IntPtr.Size);
                RaiseDeleted(Resolve(pidl1));
                RaiseCreated(Resolve(pidl2));
                break;
        }
    }

    private void RaiseCreated(BoxItem? item)
    {
        if (item != null)
        {
            ItemCreated?.Invoke(item);
        }
    }

    private void RaiseDeleted(BoxItem? item)
    {
        if (item != null)
        {
            ItemDeleted?.Invoke(item);
        }
    }

    private BoxItem? Resolve(IntPtr pidl)
    {
        if (pidl == IntPtr.Zero)
        {
            return null;
        }

        // Preferred: resolve the (still-present) item through the Shell to a BoxItem identical in
        // shape to DesktopService's items (Path = parsing name, DisplayName = friendly name).
        if (ShellNative.SHCreateItemFromIDList(pidl, ShellNative.IID_IShellItem, out IShellItem item) == 0 && item != null)
        {
            try
            {
                item.GetDisplayName(SHGDNF.NORMAL, out IntPtr pName);
                string name = Marshal.PtrToStringUni(pName) ?? string.Empty;
                Marshal.FreeCoTaskMem(pName);

                item.GetDisplayName(SHGDNF.FORPARSING, out IntPtr pPath);
                string parse = Marshal.PtrToStringUni(pPath) ?? string.Empty;
                Marshal.FreeCoTaskMem(pPath);

                item.GetAttributes(SFGAO.FOLDER, out SFGAO attrs);
                bool isFolder = (attrs & SFGAO.FOLDER) != 0;

                return new BoxItem
                {
                    Path = parse,
                    DisplayName = name,
                    ItemType = isFolder ? BoxItemType.Folder : BoxItemType.File,
                };
            }
            finally
            {
                Marshal.ReleaseComObject(item);
            }
        }

        // Fallback for a deleted item that can no longer be resolved: recover a real filesystem path
        // if possible so an existing box entry can still be matched and removed.
        var sb = new StringBuilder(260);
        if (ManualApis.SHGetPathFromIDListW(pidl, sb) != 0 && sb.Length > 0)
        {
            return BoxItemFactory.FromPath(sb.ToString());
        }

        return null;
    }

    public void Stop()
    {
        if (_regId != 0)
        {
            ShellNative.SHChangeNotifyDeregister(_regId);
            _regId = 0;
        }

        if (_hwnd != IntPtr.Zero)
        {
            ManualApis.DestroyWindow(_hwnd);
            _hwnd = IntPtr.Zero;
        }

        _atom = 0;
        _className = null;
    }

    public void Dispose() => Stop();
}
