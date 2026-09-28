using Maktaby.Core.Interfaces;
using Maktaby.Native;
using Maktaby.Win32.NativeMethods;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Threading;
using System.Threading.Tasks;

namespace Maktaby.Shell.Services;

/// <summary>
/// Extracts shell icons by resolving the item to its PIDL (via IShellItem) and calling
/// SHGetFileInfo with SHGFI_PIDL, so virtual items (This PC, Recycle Bin, ...) get correct icons
/// too — not just filesystem paths. The resulting HICON (boxed as <see cref="System.IntPtr"/>) is
/// returned to the WPF layer, which converts it to an ImageSource and frees the handle.
/// </summary>
[SupportedOSPlatform("windows10.0.14393")]
public sealed class ShellIconService : IShellIconService
{
    // NOTE: LargeIcon (32px class) — tiles render icons at 32px, and SmallIcon returned 16px bitmaps
    // that WPF had to upscale (soft/blurry). No AddOverlays: see history below.
    private const SHGFI IconFlags = SHGFI.Icon | SHGFI.LargeIcon;

    public async ValueTask<object?> GetIconAsync(string path, int size = 32, CancellationToken cancellationToken = default)
    {
        IntPtr hicon = await Task.Run(() => ExtractIcon(path));
        return hicon;
        //return new ValueTask<object?>(hicon);
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
            if (Shell32.SHCreateItemFromParsingName(path, System.IntPtr.Zero, Shell32.IID_IShellItem, out IShellItem item) != 0 ||
                item is null)
            {
                return System.IntPtr.Zero;
            }

            try
            {
                System.IntPtr pUnk = Marshal.GetIUnknownForObject(item);
                try
                {
                    if (Shell32.SHGetIDListFromObject(pUnk, out System.IntPtr pidl) != 0 || pidl == System.IntPtr.Zero)
                    {
                        return System.IntPtr.Zero;
                    }

                    try
                    {
                        var psfiPidl = new SHFILEINFOW();
                        int result = Win32Apis.SHGetFileInfo(pidl, 0, ref psfiPidl, cb, IconFlags | SHGFI.Pidl);
                        return result != 0 ? psfiPidl.hIcon : System.IntPtr.Zero;
                    }
                    finally
                    {
                        Shell32.ILFree(pidl);
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
