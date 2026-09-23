using System;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;
using DesktopBoxesUI.Core.Interfaces;
using DesktopBoxesUI.Core.Models;
using DesktopBoxesUI.Core.Services;
using WindowsNative;
using static WindowsNative.Win32Constants;
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
    private const uint WmNotify = WM_USER + 1;

    private readonly object _gate = new();
    private WndProc? _wndProc;
    private string? _className;
    private ushort _atom;
    private IntPtr _hwnd;
    private uint _regId;
    private bool _suppressed;

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
        _wndProc = new WndProc(WndProc);
        _className = "DesktopBoxesShellWatch_" + Guid.NewGuid().ToString("N");

        var wc = new WNDCLASSEX
        {
            Size = (uint)Marshal.SizeOf<WNDCLASSEX>(),
            WndProc = _wndProc,
            Instance = Kernel32.GetModuleHandle(null),
            ClassName = _className,
        };

        _atom = User32.RegisterClassEx(ref wc);
        if (_atom == 0)
        {
            return;
        }

        _hwnd = User32.CreateWindowEx(
            0,
            _className,
            "DesktopBoxesShellWatch",
            0,
            0, 0, 0, 0,
            HWND_MESSAGE,
            IntPtr.Zero,
            IntPtr.Zero,
            IntPtr.Zero);

        if (_hwnd == IntPtr.Zero)
        {
            return;
        }

        if (Shell32.SHGetSpecialFolderLocation(IntPtr.Zero, 0 /* CSIDL_DESKTOP */, out IntPtr deskPidl) != 0)
        {
            return;
        }

        var entry = new SHChangeNotifyEntry
        {
            pidl = deskPidl,
            fRecursive = 0,
        };

        _regId = Shell32.SHChangeNotifyRegister(
            _hwnd,
            SHCNRF.InterruptLevel | SHCNRF.ShellLevel | SHCNRF.NewDelivery,
            SHCNE.CREATE | SHCNE.DELETE | SHCNE.RENAMEITEM | SHCNE.RENAMEFOLDER,
            WmNotify,
            1,
            ref entry);

        Shell32.ILFree(deskPidl);
    }

    private IntPtr WndProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam)
    {
        if (msg == WmNotify)
        {
            IntPtr lockHandle = Shell32.SHChangeNotification_Lock(wParam, (uint)lParam, out IntPtr pppidl, out int lEvent);
            if (lockHandle != IntPtr.Zero)
            {
                try
                {
                    HandleEvent(lEvent, pppidl);
                }
                finally
                {
                    Shell32.SHChangeNotification_Unlock(lockHandle);
                }
            }

            return IntPtr.Zero;
        }

        return User32.DefWindowProc(hWnd, msg, wParam, lParam);
    }

    private void HandleEvent(int lEvent, IntPtr pppidl)
    {
        var pidl1 = Marshal.ReadIntPtr(pppidl, 0);

        switch (lEvent)
        {
            case (int)SHCNE.CREATE:
            case (int)SHCNE.UPDATEITEM:
                RaiseCreated(Resolve(pidl1));
                break;

            case (int)SHCNE.DELETE:
                RaiseDeleted(Resolve(pidl1));
                break;

            case (int)SHCNE.RENAMEITEM:
            case (int)SHCNE.RENAMEFOLDER:
                var pidl2 = Marshal.ReadIntPtr(pppidl, IntPtr.Size);
                RaiseDeleted(Resolve(pidl1));
                RaiseCreated(Resolve(pidl2));
                break;
        }
    }

    private void RaiseCreated(BoxItem? item)
    {
        if (!_suppressed && item != null)
        {
            ItemCreated?.Invoke(item);
        }
    }

    private void RaiseDeleted(BoxItem? item)
    {
        if (!_suppressed && item != null)
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
        if (Shell32.SHCreateItemFromIDList(pidl, Shell32.IID_IShellItem, out IShellItem item) == 0 && item != null)
        {
            try
            {
                string name = string.Empty;
                if (item.GetDisplayName(SHGDNF.NORMAL, out IntPtr pName) == 0 && pName != IntPtr.Zero)
                {
                    try { name = Marshal.PtrToStringUni(pName) ?? string.Empty; }
                    finally { Marshal.FreeCoTaskMem(pName); }
                }

                string parse = string.Empty;
                if (item.GetDisplayName(SHGDNF.FORPARSING, out IntPtr pPath) == 0 && pPath != IntPtr.Zero)
                {
                    try { parse = Marshal.PtrToStringUni(pPath) ?? string.Empty; }
                    finally { Marshal.FreeCoTaskMem(pPath); }
                }

                item.GetAttributes(SFGAO.FOLDER, out SFGAO attrs);
                bool isFolder = (attrs & SFGAO.FOLDER) != 0;

                if (ShellItemFilter.IsExcluded(parse))
                {
                    return null;
                }

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
        if (Shell32.SHGetPathFromIDListW(pidl, sb) != 0 && sb.Length > 0)
        {
            return BoxItemFactory.FromPath(sb.ToString());
        }

        return null;
    }

    public void Stop()
    {
        if (_regId != 0)
        {
            Shell32.SHChangeNotifyDeregister(_regId);
            _regId = 0;
        }

        if (_hwnd != IntPtr.Zero)
        {
            User32.DestroyWindow(_hwnd);
            _hwnd = IntPtr.Zero;
        }

        // The class name embeds a fresh Guid per Start (see EnsureWindow): without an
        // explicit unregister each Start/Stop cycle (disable toggle, Explorer restart)
        // leaks a USER atom + class registration.
        if (!string.IsNullOrEmpty(_className))
        {
            try { User32.UnregisterClass(_className, Kernel32.GetModuleHandle(null)); } catch { }
        }

        _atom = 0;
        _className = null;
    }

    public void Dispose() => Stop();

    public void Pause() => _suppressed = true;

    public void Resume() => _suppressed = false;
}
