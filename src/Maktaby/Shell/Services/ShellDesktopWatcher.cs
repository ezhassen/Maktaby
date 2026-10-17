using Maktaby.Core.Interfaces;
using Maktaby.Core.Models;
using Maktaby.Core.Services;
using Maktaby.Native;
using Maktaby.Win32.NativeMethods;
using System;
using System.IO;
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

    /// <summary>
    /// The Recycle Bin's physical folder, watched through <c>ReadDirectoryChangesW</c>.
    /// </summary>
    /// <remarks>
    /// This is the mechanism that finally works. Three shell-notification routes were tried and none
    /// delivered anything when the bin was emptied: the shell-namespace PIDL from
    /// <c>SHGetSpecialFolderLocation(CSIDL_BITBUCKET)</c> (recursive or not, with or without
    /// <c>UPDATEITEM</c>), and a recursive registration on <c>C:\$Recycle.Bin</c> itself. All of them
    /// go through the shell's notification service, which evidently does not report this.
    /// <para>
    /// <c>FileSystemWatcher</c> sits directly on the kernel's directory-change notifications, so it
    /// does not depend on that service at all. It is still fully event-driven - no polling - and it
    /// watches exactly one folder. The shell registration above is kept as a second, cheap signal.
    /// </para>
    /// </remarks>
    private FileSystemWatcher? _binWatcher;

    /// <summary>Where the bin watch is installed, for logging and for re-arming it.</summary>
    private string? _binWatchPath;
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

        StartBinFolderWatch();
    }

    /// <summary>
    /// Watches <c>C:\$Recycle.Bin</c> with <see cref="FileSystemWatcher"/> - the working mechanism -
    /// and turns any change into one <see cref="ItemUpdated"/> for the bin.
    /// </summary>
    /// <remarks>
    /// Everything under the bin is noise as far as the UI is concerned: only the bin's icon
    /// changes. So the raw events are collapsed here rather than in the consumers, which is also
    /// what keeps the $I/$R churn from turning into a storm of icon reloads.
    /// </remarks>
    private void StartBinFolderWatch()
    {
        // The physical bin path is composed rather than asked for: SHGetSpecialFolderPath faulted
        // with an ExecutionEngineException, and there is nothing to learn from a native call when
        // the answer is "%SystemDrive%\$Recycle.Bin".
        string? systemDrive = Environment.GetEnvironmentVariable("SystemDrive");
        if (string.IsNullOrWhiteSpace(systemDrive)) { return; }

        var binPath = Path.Combine(systemDrive, "$Recycle.Bin");
        if (!Directory.Exists(binPath)) { return; }

        try
        {
                var watcher = new FileSystemWatcher(binPath)
                {
                    // The $I/$R pairs live in per-drive, per-user subfolders.
                    IncludeSubdirectories = true,
                    NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName |
                                    NotifyFilters.LastWrite | NotifyFilters.Size,
                    // A bin can be emptied in one burst; the default 8 KB buffer overflows and the
                    // watcher silently stops reporting until re-armed.
                    InternalBufferSize = 64 * 1024,
                };

                void OnChange(object sender, FileSystemEventArgs e)
                {
                    if (ResolveRecycleBin() is { } item) { ItemUpdated?.Invoke(item); }
                }

                watcher.Created += OnChange;
                watcher.Deleted += OnChange;
                watcher.Changed += OnChange;
                watcher.Renamed += (_, e) => OnChange(watcher, e);

                watcher.Error += (_, e) =>
                {
                    // Overflow (or a dropped handle) stops delivery until events are re-enabled.
                    Serilog.Log.Warning(e.GetException(), "Recycle Bin watch error; re-arming");
                    try { watcher.EnableRaisingEvents = true; } catch { }
                };

                watcher.EnableRaisingEvents = true;

                _binWatcher = watcher;
                _binWatchPath = binPath;

                Serilog.Log.Information("Recycle Bin watch active on {Path}", binPath);
            }
            catch (Exception ex)
            {
                Serilog.Log.Warning(ex, "Recycle Bin folder watch could not start on {Path}", binPath);
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

        // The folder watcher holds an OS handle, so it must be disposed on every Stop - not just on
        // shutdown, or a disable/enable cycle leaks one per cycle.
        if (_binWatcher is not null)
        {
            try
            {
                _binWatcher.EnableRaisingEvents = false;
                _binWatcher.Dispose();
            }
            catch
            {
                // Nothing useful to do while tearing down.
            }

            _binWatcher = null;
            _binWatchPath = null;
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
