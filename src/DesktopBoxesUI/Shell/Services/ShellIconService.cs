using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Threading;
using System.Threading.Tasks;
using DesktopBoxesUI.Core.Interfaces;
using DesktopBoxesUI.Shell.Interop;
using DesktopBoxesUI.Win32.NativeMethods;

namespace DesktopBoxesUI.Shell.Services;

/// <summary>
/// Extracts shell icons by resolving the item to its PIDL (via IShellItem) and calling
/// SHGetFileInfo with SHGFI_PIDL, so virtual items (This PC, Recycle Bin, ...) get correct icons
/// too — not just filesystem paths. The resulting HICON (boxed as <see cref="System.IntPtr"/>) is
/// returned to the WPF layer, which converts it to an ImageSource and frees the handle.
/// </summary>
[SupportedOSPlatform("windows10.0.14393")]
public sealed class ShellIconService : IShellIconService
{
    private const SHGFI IconFlags = SHGFI.Icon | SHGFI.SmallIcon | SHGFI.AddOverlays;

    private readonly ConcurrentDictionary<string, IntPtr> _cache = new();

    public ValueTask<object?> GetIconAsync(string path, int size = 32, CancellationToken cancellationToken = default)
    {
        if (_cache.TryGetValue(path, out var cached) && cached != System.IntPtr.Zero)
        {
            return new ValueTask<object?>(cached);
        }

        IntPtr hicon = ExtractIcon(path);

        if (hicon != System.IntPtr.Zero)
        {
            _cache[path] = hicon;
        }

        return new ValueTask<object?>(hicon);
    }

    private static IntPtr ExtractIcon(string path)
    {
        // Fast, reliable path for filesystem items (SHGetFileInfo with a path string).
        var psfi = new SHFILEINFOW();
        uint cb = (uint)Marshal.SizeOf<SHFILEINFOW>();
        int byPath = Win32Apis.SHGetFileInfo(path, 0, ref psfi, cb, IconFlags);
        if (byPath != 0 && psfi.hIcon != System.IntPtr.Zero)
        {
            return psfi.hIcon;
        }

        // Fallback to PIDL resolution for virtual shell items (This PC, Recycle Bin, ...).
        try
        {
            if (ShellNative.SHCreateItemFromParsingName(path, System.IntPtr.Zero, ShellNative.IID_IShellItem, out IShellItem item) != 0 ||
                item is null)
            {
                return System.IntPtr.Zero;
            }

            try
            {
                System.IntPtr pUnk = Marshal.GetIUnknownForObject(item);
                try
                {
                    if (ShellNative.SHGetIDListFromObject(pUnk, out System.IntPtr pidl) != 0 || pidl == System.IntPtr.Zero)
                    {
                        return System.IntPtr.Zero;
                    }

                    try
                    {
                        var psfiPidl = new SHFILEINFOW();
                        int result = Win32Apis.SHGetFileInfo(pidl, 0, ref psfiPidl, cb, IconFlags);
                        return result != 0 ? psfiPidl.hIcon : System.IntPtr.Zero;
                    }
                    finally
                    {
                        ShellNative.ILFree(pidl);
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
            return System.IntPtr.Zero;
        }
    }
}
