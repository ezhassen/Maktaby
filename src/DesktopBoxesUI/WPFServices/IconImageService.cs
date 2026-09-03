using DesktopBoxesUI.Core.Interfaces;
using DesktopBoxesUI.Win32.NativeMethods;
using System.Collections.Concurrent;
using System.Runtime.Versioning;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using HICON = Windows.Win32.UI.WindowsAndMessaging.HICON;

namespace DesktopBoxesUI.WPFServices;

/// <summary>
/// Converts shell HICON handles (returned by <see cref="IShellIconService"/>) into WPF
/// ImageSources, caching the result so the underlying GDI handle is extracted and destroyed
/// exactly once. Lives in the UI layer (not Core), so it is allowed to touch WPF and the native
/// wrapper. Keeps the icon GDI handle lifetime short to avoid exhausting GDI objects.
/// </summary>
[SupportedOSPlatform("windows10.0.14393")]
public sealed class IconImageService
{
    private readonly IShellIconService _shellIcon;
    private readonly ConcurrentDictionary<string, ImageSource?> _cache = new();
    private readonly ConcurrentDictionary<string, ImageSource?> _pidlCache = new();

    public IconImageService(IShellIconService shellIcon) => _shellIcon = shellIcon;

    public async Task<ImageSource?> GetIconAsync(string path, CancellationToken cancellationToken = default)
    {
        if (_cache.TryGetValue(path, out var cached))
        {
            return cached;
        }

        object? token = await _shellIcon.GetIconAsync(path, cancellationToken: cancellationToken).ConfigureAwait(false);

        ImageSource? source = null;
        await Task.Run(() =>
        {
            if (token is IntPtr hicon && hicon != IntPtr.Zero)
            {
                source = Imaging.CreateBitmapSourceFromHIcon(hicon, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
                source.Freeze();
                Win32Apis.DestroyIcon(hicon);
                return;
            }

            // SHGetFileInfo couldn't resolve a PIDL-only link (e.g. a .lnk we created for a Start Menu app).
            // IShellItemImageFactory follows the link and returns a proper alpha icon for the target.
            IntPtr hbitmap = Win32Apis.GetIconBitmapForPath(path);
            if (hbitmap != IntPtr.Zero)
            {
                source = Imaging.CreateBitmapSourceFromHBitmap(hbitmap, IntPtr.Zero, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
                source.Freeze();
                Win32Apis.DeleteObject(hbitmap);
            }
        }, cancellationToken);

        // Only cache successes: a transient failure (shell busy, overlay handler glitch, startup race)
        // must stay retryable instead of being frozen as a permanent blank icon.
        if (source is not null)
        {
            _cache[path] = source;
        }

        return source;
    }

    /// <summary>Loads an icon for a stored shell PIDL (virtual items such as UWP/Store apps). When
    /// <paramref name="filePath"/> is supplied (a desktop <c>.lnk</c> we created for the item) it is used as
    /// an additional resolution source so the icon follows PIDL-only link targets.</summary>
    public async Task<ImageSource?> GetIconFromPidlAsync(string pidlBase64, string? filePath = null, CancellationToken cancellationToken = default)
    {
        if (_pidlCache.TryGetValue(pidlBase64, out var cached))
        {
            return cached;
        }

        ImageSource? source = null;
        await Task.Run(() =>
        {
            // Preferred: IShellItemImageFactory yields a proper alpha icon for any shell item (incl. virtual
            // Start Menu apps). Falls back to SHGetFileInfo for compatibility.
            IntPtr hbitmap = Win32Apis.GetIconBitmapForPidl(pidlBase64);
            if (hbitmap != IntPtr.Zero)
            {
                source = Imaging.CreateBitmapSourceFromHBitmap(hbitmap, IntPtr.Zero, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
                source.Freeze();
                Win32Apis.DeleteObject(hbitmap);
                return;
            }

            if (!string.IsNullOrEmpty(filePath))
            {
                IntPtr hbmpPath = Win32Apis.GetIconBitmapForPath(filePath);
                if (hbmpPath != IntPtr.Zero)
                {
                    source = Imaging.CreateBitmapSourceFromHBitmap(hbmpPath, IntPtr.Zero, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
                    source.Freeze();
                    Win32Apis.DeleteObject(hbmpPath);
                    return;
                }
            }

            IntPtr hicon = Win32Apis.GetIconForPidl(pidlBase64);
            if (hicon != IntPtr.Zero)
            {
                source = Imaging.CreateBitmapSourceFromHIcon(hicon, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
                source.Freeze();
                Win32Apis.DestroyIcon(hicon);
            }
        }, cancellationToken);


        // Successes only — see GetIconAsync.
        if (source is not null)
        {
            _pidlCache[pidlBase64] = source;
        }

        return source;
    }
}
