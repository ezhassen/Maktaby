using System.Collections.Concurrent;
using System.Runtime.Versioning;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using DesktopBoxesUI.Core.Interfaces;
using DesktopBoxesUI.Win32.NativeMethods;
using HICON = Windows.Win32.UI.WindowsAndMessaging.HICON;

namespace DesktopBoxesUI;

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
        if (token is IntPtr hicon && hicon != IntPtr.Zero)
        {
            source = Imaging.CreateBitmapSourceFromHIcon(hicon, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
            source.Freeze();
            Win32Apis.DestroyIcon(hicon);
        }

        _cache[path] = source;
        return source;
    }

    /// <summary>Loads an icon for a stored shell PIDL (virtual items such as UWP/Store apps).</summary>
    public async Task<ImageSource?> GetIconFromPidlAsync(string pidlBase64, CancellationToken cancellationToken = default)
    {
        if (_pidlCache.TryGetValue(pidlBase64, out var cached))
        {
            return cached;
        }

        ImageSource? source = null;
        IntPtr hicon = Win32Apis.GetIconForPidl(pidlBase64);
        if (hicon != IntPtr.Zero)
        {
            source = Imaging.CreateBitmapSourceFromHIcon(hicon, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
            source.Freeze();
            Win32Apis.DestroyIcon(hicon);
        }

        _pidlCache[pidlBase64] = source;
        return source;
    }
}
