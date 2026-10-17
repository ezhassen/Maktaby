using Maktaby.Core.Interfaces;
using Maktaby.Core.Models;
using Maktaby.Core.Services;
using Maktaby.Native;
using Maktaby.Win32.NativeMethods;
using System;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;
using static Maktaby.Native.Win32Constants;

namespace Maktaby.Shell.Services;

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
    /// <summary>Second registration, on the Recycle Bin folder only - see RegisterRecycleBin.</summary>
    private uint _binRegId;

    /// <summary>Bytes of the registered bin FOLDER pidl, so bin-internal events can be recognised.</summary>
    private byte[] _binFolderBytes = Array.Empty<byte>();
    private bool _suppressed;

    public event Action<BoxItem>? ItemCreated;
    public event Action<BoxItem>? ItemDeleted;

    /// <inheritdoc />
    public event Action<BoxItem>? ItemUpdated;

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
        _className = "MaktabyShellWatch_" + Guid.NewGuid().ToString("N");

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
            "MaktabyShellWatch",
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

        RegisterRecycleBin();
    }

    /// <summary>
    /// Subscribes to the Recycle Bin's PHYSICAL folder (<c>C:\$Recycle.Bin</c>), recursively.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This replaces an earlier registration on the shell-namespace PIDL from
    /// <c>SHGetSpecialFolderLocation(CSIDL_BITBUCKET)</c>. That one was the obvious choice and it does
    /// not work: measured on this machine, emptying the bin produced <b>no notification at all</b> -
    /// not <c>UPDATEITEM</c>, not <c>UPDATEDIR</c>, nothing - so the icon stayed stale. The
    /// namespace PIDL represents the bin's appearance, not its contents, and the shell does not
    /// report changes to the latter through it.
    /// </para>
    /// <para>
    /// The real folder does change on every operation that matters: deleting something writes new
    /// <c>$I</c>/<c>$R</c> pairs into it, restoring removes them, emptying removes them all. Those are
    /// ordinary filesystem notifications, so this registration is filesystem-level
    /// (<c>SHCNRF_InterruptLevel</c>) rather than shell-level.
    /// </para>
    /// <para>
    /// Recursive because the interesting entries live in per-drive, per-user subfolders below it.
    /// The descendants are never treated as items: <see cref="HandleEvent"/> recognises them by PIDL
    /// prefix and reports a single update for the bin, so none of this can leak into the boxes as
    /// phantom desktop items.
    /// </para>
    /// </remarks>
    private void RegisterRecycleBin()
    {
        IntPtr folderPidl = IntPtr.Zero;
        try
        {
            // The physical bin path is composed rather than asked for: SHGetSpecialFolderPath
            // faulted with an ExecutionEngineException, and there is nothing to learn from a
            // native call when the answer is "%SystemDrive%\\$Recycle.Bin".
            string? systemDrive = Environment.GetEnvironmentVariable("SystemDrive");
            if (string.IsNullOrWhiteSpace(systemDrive))
            {
                return;
            }

            var binPath = System.IO.Path.Combine(systemDrive, "$Recycle.Bin");
            if (!System.IO.Directory.Exists(binPath)) { return; }

            folderPidl = Shell32.ILCreateFromPathW(binPath);
            if (folderPidl == IntPtr.Zero) { return; }

            // Kept for recognising events that arrive from inside the bin.
            _binFolderBytes = PidlToBytes(folderPidl);

            var binEntry = new SHChangeNotifyEntry { pidl = folderPidl, fRecursive = 1 };
            _binRegId = Shell32.SHChangeNotifyRegister(
                _hwnd,
                SHCNRF.InterruptLevel | SHCNRF.NewDelivery,
                SHCNE.CREATE | SHCNE.DELETE | SHCNE.UPDATEDIR | SHCNE.UPDATEITEM,
                WmNotify,
                1,
                ref binEntry);
        }
        finally
        {
            if (folderPidl != IntPtr.Zero) { Shell32.ILFree(folderPidl); }
        }
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

        // Anything INSIDE the physical bin is a signal about the bin, never an item of its own.
        // Checked first: the bin registration is recursive, so without this the $I/$R pairs would
        // arrive as ordinary CREATE/DELETE and be filed as phantom desktop items in the default
        // box.
        if (IsInsideRecycleBin(pidl1))
        {
            if (ResolveRecycleBin() is { } binItem) { ItemUpdated?.Invoke(binItem); }
            return;
        }

        switch (lEvent)
        {
            case (int)SHCNE.CREATE:
                RaiseCreated(Resolve(pidl1));
                break;

            case (int)SHCNE.UPDATEITEM:
            case (int)SHCNE.UPDATEDIR:
                if (Resolve(pidl1) is { } updated) { ItemUpdated?.Invoke(updated); }
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

                    // The PIDL travels WITH the resolved item, not just the notification. Without it the
                    // item is unmatchable for anything that has no filesystem path: measured on this
                    // machine, the Recycle Bin answers SHGetPathFromIDListW with a failure, so it is
                    // stored as a PIDL-backed entry, while this resolved copy would carry only the
                    // namespace string in Path - and BoxItem.RefersToSame needs both sides to hold the
                    // SAME kind of identity to compare them.
                    Pidl = Convert.ToBase64String(PidlToBytes(pidl)),
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

    /// <summary>
    /// Marshals an absolute PIDL to bytes, the form <see cref="BoxItem.Pidl"/> stores.
    /// </summary>
    /// <remarks>
    /// Walks the SHITEMID chain: each item begins with its own byte count, and a zero-length item
    /// terminates the list. Best-effort - a PIDL that cannot be walked yields an empty array, which
    /// simply leaves the caller with a path-matched item rather than failing the notification.
    /// </remarks>
    private static byte[] PidlToBytes(IntPtr pidl)
    {
        try
        {
            int size = 2;   // terminating null SHITEMID
            for (var item = pidl; item != IntPtr.Zero;)
            {
                short cb = Marshal.ReadInt16(item);
                if (cb <= 0) { break; }
                size += cb;
                item = IntPtr.Add(item, cb);
            }

            var buffer = new byte[size];
            var dst = 0;
            for (var src = pidl; src != IntPtr.Zero;)
            {
                short cb = Marshal.ReadInt16(src);
                if (cb <= 0) { break; }
                Marshal.Copy(src, buffer, dst, cb);
                dst += cb;
                src = IntPtr.Add(src, cb);
            }

            return buffer;
        }
        catch
        {
            return Array.Empty<byte>();
        }
    }

    /// <summary>
    /// True when an event's PIDL lies inside the registered bin folder.
    /// </summary>
    /// <remarks>
    /// Prefix compare on the marshalled PIDL bytes, which is exactly how the shell encodes "this item
    /// is inside that folder". Cheaper and more reliable than resolving every event to a path and
    /// string-matching it.
    /// </remarks>
    private bool IsInsideRecycleBin(IntPtr pidl)
    {
        if (_binFolderBytes.Length == 0 || pidl == IntPtr.Zero) { return false; }

        try
        {
            var bytes = PidlToBytes(pidl);
            if (bytes.Length <= _binFolderBytes.Length) { return false; }

            return _binFolderBytes.AsSpan().SequenceEqual(bytes.AsSpan(0, _binFolderBytes.Length));
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// The Recycle Bin as a <see cref="BoxItem"/>, resolved from its namespace PIDL.
    /// </summary>
    /// <remarks>
    /// Resolved separately from the event's own PIDL because the event points at an $I/$R pair inside
    /// the physical folder - a file the user never sees - and the icon belongs to the bin item.
    /// </remarks>
    private BoxItem? ResolveRecycleBin()
    {
        IntPtr pidl = IntPtr.Zero;
        try
        {
            if (Shell32.SHGetSpecialFolderLocation(IntPtr.Zero, 10 /* CSIDL_BITBUCKET */, out pidl) != 0 ||
                pidl == IntPtr.Zero)
            {
                return null;
            }

            return Resolve(pidl);
        }
        catch
        {
            return null;
        }
        finally
        {
            if (pidl != IntPtr.Zero) { Shell32.ILFree(pidl); }
        }
    }


    public void Stop()
    {
        if (_regId != 0)
        {
            Shell32.SHChangeNotifyDeregister(_regId);
            _regId = 0;
        }

        if (_binRegId != 0)
        {
            Shell32.SHChangeNotifyDeregister(_binRegId);
            _binRegId = 0;
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

    /// <inheritdoc />
    public void NotifyRecycleBinUpdated()
    {
        // Resolve the bin and hand it over as an updated item, so a consumer that reloads icons by
        // matching identity behaves the same for our own deletes as for the shell's own.
        IntPtr pidl = IntPtr.Zero;
        try
        {
            if (Shell32.SHGetSpecialFolderLocation(IntPtr.Zero, 10 /* CSIDL_BITBUCKET */, out pidl) == 0 &&
                pidl != IntPtr.Zero)
            {
                if (Resolve(pidl) is { } item) { ItemUpdated?.Invoke(item); }
            }
        }
        catch
        {
            // Icon freshness is never worth failing a delete over.
        }
        finally
        {
            if (pidl != IntPtr.Zero) { Shell32.ILFree(pidl); }
        }
    }
}
