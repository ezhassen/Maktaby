using DesktopBoxesUI.Core.Interfaces;
using DesktopBoxesUI.Win32.NativeMethods;
using System.Diagnostics;
using System.IO;
using System.Runtime.Versioning;
using System.Windows;
using System.Windows.Interop;
using WindowsNative;

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

        var op = new SHFILEOPSTRUCT
        {
            hwnd = OwnerHandle(),
            wFunc = Win32Constants.FO_MOVE,
            pFrom = path + "\0",
            pTo = newPath + "\0",
            fFlags = (ushort)(Win32Constants.FOF_ALLOWUNDO | Win32Constants.FOF_NOCONFIRMATION | Win32Constants.FOF_NOERRORUI),
        };

        int hr = Win32Apis.FileOperation(ref op);
        return hr == 0 && op.fAnyOperationsAborted == 0;
    }

    [Obsolete("Use DeleteAsync instead", error: true)]
    public bool Delete(string path, bool permanent)
    {
        if (string.IsNullOrEmpty(path))
        {
            return false;
        }

        // Permanent delete: no FOF_ALLOWUNDO and FOF_WANTNUKEWARNING forces the native
        // "Permanently delete?" confirmation. Recycle delete: silent + undoable.
        ushort flags = permanent
            ? Win32Constants.FOF_WANTNUKEWARNING
            : (ushort)(Win32Constants.FOF_ALLOWUNDO | Win32Constants.FOF_NOCONFIRMATION | Win32Constants.FOF_SILENT | Win32Constants.FOF_NOERRORUI);

        var op = new SHFILEOPSTRUCT
        {
            hwnd = OwnerHandle(),
            wFunc = Win32Constants.FO_DELETE,
            pFrom = path + "\0",
            fFlags = flags,
        };

        int hr = Win32Apis.FileOperation(ref op);
        return hr == 0 && op.fAnyOperationsAborted == 0;
    }

    [Obsolete("Use CopyAsync instead", error: true)]
    public bool Copy(string source, string destination)
    {
        if (string.IsNullOrEmpty(source) || string.IsNullOrEmpty(destination))
        {
            return false;
        }

        var op = new SHFILEOPSTRUCT
        {
            hwnd = OwnerHandle(),
            wFunc = Win32Constants.FO_COPY,
            pFrom = source + "\0",
            pTo = destination + "\0",
            fFlags = (ushort)(Win32Constants.FOF_NOCONFIRMATION | Win32Constants.FOF_NOERRORUI | Win32Constants.FOF_ALLOWUNDO),
        };

        int hr = Win32Apis.FileOperation(ref op);
        return hr == 0 && op.fAnyOperationsAborted == 0;
    }

    public Task<bool> DeleteAsync(IReadOnlyList<string> paths, bool permanent, CancellationToken cancellationToken = default)
    {
        if (paths == null || paths.Count == 0) return Task.FromResult(false);
        var valid = paths.Where(p => !string.IsNullOrEmpty(p)).ToArray();
        if (valid.Length == 0) return Task.FromResult(false);
        if (cancellationToken.IsCancellationRequested) return Task.FromResult(false);

        return RunOnStaThread(() =>
        {
            if (cancellationToken.IsCancellationRequested) return false;
            ushort flags = permanent
                ? Win32Constants.FOF_WANTNUKEWARNING
                : (ushort)(Win32Constants.FOF_ALLOWUNDO | Win32Constants.FOF_NOCONFIRMATION | Win32Constants.FOF_SILENT | Win32Constants.FOF_NOERRORUI);

            string multiFrom = BuildMultiString(valid);
            var op = new SHFILEOPSTRUCT
            {
                hwnd = OwnerHandle(),
                wFunc = Win32Constants.FO_DELETE,
                pFrom = multiFrom,
                pTo = null,
                fFlags = flags,
            };
            int hr = Win32Apis.FileOperation(ref op);
            return hr == 0 && op.fAnyOperationsAborted == 0;
        }, cancellationToken);
    }

    public Task<bool> CopyAsync(IReadOnlyList<string> sources, IReadOnlyList<string> destinations, CancellationToken cancellationToken = default)
    {
        if (sources == null || sources.Count == 0 || destinations == null || destinations.Count == 0) return Task.FromResult(false);
        var validSources = sources.Where(s => !string.IsNullOrEmpty(s)).ToArray();
        var validDests = destinations.Where(d => !string.IsNullOrEmpty(d)).ToArray();
        if (validSources.Length == 0 || validDests.Length == 0) return Task.FromResult(false);
        if (cancellationToken.IsCancellationRequested) return Task.FromResult(false);

        // If single destination for multiple sources, treat as folder target for SHFileOperation
        string multiFrom = BuildMultiString(validSources);
        string multiTo;
        if (validDests.Length == 1 && validSources.Length > 1)
        {
            // Single folder destination for multiple sources
            multiTo = validDests[0] + "\0\0";
        }
        else if (validSources.Length == validDests.Length)
        {
            multiTo = BuildMultiString(validDests);
        }
        else
        {
            // Mismatched counts - fallback to single dest folder if possible
            multiTo = BuildMultiString(validDests);
        }

        return RunOnStaThread(() =>
        {
            if (cancellationToken.IsCancellationRequested) return false;
            var op = new SHFILEOPSTRUCT
            {
                hwnd = OwnerHandle(),
                wFunc = Win32Constants.FO_COPY,
                pFrom = multiFrom,
                pTo = multiTo,
                fFlags = (ushort)(Win32Constants.FOF_NOCONFIRMATION | Win32Constants.FOF_NOERRORUI | Win32Constants.FOF_ALLOWUNDO),
            };
            int hr = Win32Apis.FileOperation(ref op);
            return hr == 0 && op.fAnyOperationsAborted == 0;
        }, cancellationToken);
    }

    private static string BuildMultiString(IReadOnlyList<string> paths)
    {
        // SHFileOperation expects double-null terminated multi-string: "p1\0p2\0\0"
        // The marshaler will add its own terminating null, so we build "p1\0p2\0\0" and let it add one more.
        return string.Join("\0", paths) + "\0\0";
    }

    private static Task<T> RunOnStaThread<T>(Func<T> func, CancellationToken ct)
    {
        var tcs = new TaskCompletionSource<T>();
        var thread = new Thread(() =>
        {
            try
            {
                if (ct.IsCancellationRequested) tcs.TrySetCanceled(ct);
                else tcs.TrySetResult(func());
            }
            catch (OperationCanceledException ex) { tcs.TrySetCanceled(ex.CancellationToken); }
            catch (Exception ex) { tcs.TrySetException(ex); }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.IsBackground = true;
        thread.Start();
        ct.Register(() => tcs.TrySetCanceled(ct));
        return tcs.Task;
    }

    private static IntPtr OwnerHandle()
    {
        var app = Application.Current;
        if (app is null)
        {
            return IntPtr.Zero;
        }
        IntPtr best = IntPtr.Zero;
        if (app.Dispatcher != null && !app.Dispatcher.CheckAccess())
        {
            app.Dispatcher.Invoke(() =>
            {
                // Use the app's own window handle as the operation owner rather than GetForegroundWindow(): the
                // latter can return the desktop/explorer if focus has already shifted, which would let SHFileOperation
                // parent its (progress/confirmation) UI to another process and pull focus away from us.
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
            });
        }
        else
        {
            // Use the app's own window handle as the operation owner rather than GetForegroundWindow(): the
            // latter can return the desktop/explorer if focus has already shifted, which would let SHFileOperation
            // parent its (progress/confirmation) UI to another process and pull focus away from us.
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
        }

        return best;
    }
}
