using DesktopBoxesUI.Core.Interfaces;
using DesktopBoxesUI.Win32.NativeMethods;
using System.Diagnostics;
using System.IO;
using System.Runtime.Versioning;
using System.Windows;
using System.Windows.Interop;

namespace DesktopBoxesUI.Win32.Services;

/// <summary>
/// Renames / deletes items through <c>SHFileOperation</c> so the behaviour matches Explorer (Recycle Bin
/// undo for normal deletes, the native confirmation dialog for permanent deletes). The operation runs on
/// the calling (UI) thread because it may show a modal dialog.
/// </summary>
[SupportedOSPlatform("windows10.0.14393")]
public sealed class FileOperationService : IFileOperationService
{
    public bool Rename(string path, string newName)
    {
        if (string.IsNullOrEmpty(path) || string.IsNullOrWhiteSpace(newName))
        {
            return false;
        }

        var dir = Path.GetDirectoryName(path);
        if (dir is null)
        {
            return false;
        }

        var newPath = Path.Combine(dir, newName.Trim());
        if (string.Equals(Path.GetFullPath(path), Path.GetFullPath(newPath), StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        var op = new ManualApis.SHFILEOPSTRUCT
        {
            hwnd = OwnerHandle(),
            wFunc = Win32Apis.FO_MOVE,
            pFrom = path + "\0",
            pTo = newPath + "\0",
            fFlags = (ushort)(Win32Apis.FOF_ALLOWUNDO | Win32Apis.FOF_NOCONFIRMATION | Win32Apis.FOF_NOERRORUI),
        };

        int hr = Win32Apis.FileOperation(ref op);
        return hr == 0 && op.fAnyOperationsAborted == 0;
    }

    public bool Delete(string path, bool permanent)
    {
        if (string.IsNullOrEmpty(path))
        {
            return false;
        }

        // Permanent delete: no FOF_ALLOWUNDO and FOF_WANTNUKEWARNING forces the native
        // "Permanently delete?" confirmation. Recycle delete: silent + undoable.
        ushort flags = permanent
            ? Win32Apis.FOF_WANTNUKEWARNING
            : (ushort)(Win32Apis.FOF_ALLOWUNDO | Win32Apis.FOF_NOCONFIRMATION | Win32Apis.FOF_SILENT | Win32Apis.FOF_NOERRORUI);

        var op = new ManualApis.SHFILEOPSTRUCT
        {
            hwnd = OwnerHandle(),
            wFunc = Win32Apis.FO_DELETE,
            pFrom = path + "\0",
            fFlags = flags,
        };

        int hr = Win32Apis.FileOperation(ref op);
        return hr == 0 && op.fAnyOperationsAborted == 0;
    }

    public bool Copy(string source, string destination)
    {
        if (string.IsNullOrEmpty(source) || string.IsNullOrEmpty(destination))
        {
            return false;
        }

        var op = new ManualApis.SHFILEOPSTRUCT
        {
            hwnd = OwnerHandle(),
            wFunc = Win32Apis.FO_COPY,
            pFrom = source + "\0",
            pTo = destination + "\0",
            fFlags = (ushort)(Win32Apis.FOF_NOCONFIRMATION | Win32Apis.FOF_NOERRORUI | Win32Apis.FOF_ALLOWUNDO),
        };

        int hr = Win32Apis.FileOperation(ref op);
        return hr == 0 && op.fAnyOperationsAborted == 0;
    }

    private static IntPtr OwnerHandle()
    {
        var app = Application.Current;
        if (app is null)
        {
            return IntPtr.Zero;
        }

        // Use the app's own window handle as the operation owner rather than GetForegroundWindow(): the
        // latter can return the desktop/explorer if focus has already shifted, which would let SHFileOperation
        // parent its (progress/confirmation) UI to another process and pull focus away from us.
        IntPtr best = IntPtr.Zero;
        foreach (Window window in app.Windows)
        {
            if (window is { IsVisible: true } w && new WindowInteropHelper(w).Handle != IntPtr.Zero)
            {
                best = new WindowInteropHelper(w).Handle;
                if (w.IsActive)
                {
                    break;
                }
            }
        }

        return best;
    }
}
