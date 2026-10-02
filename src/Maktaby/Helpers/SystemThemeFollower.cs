using System;
using System.Runtime.Versioning;
using System.Windows;
using System.Windows.Interop;
using Wpf.Ui.Appearance;

namespace Maktaby.Helpers;

/// <summary>
/// Keeps the app theme in step with the Windows light/dark setting while the user's theme
/// preference is "System" (<c>SelectedTheme == null</c>).
/// </summary>
/// <remarks>
/// <para>
/// <b>Why this exists.</b> <c>ApplicationThemeManager.ApplySystemTheme()</c> is a ONE-SHOT: it
/// reads the current system theme, applies it, and stops. Nothing re-runs it, so an app left on
/// "System" keeps whatever it resolved at launch and never notices a later
/// Settings → Personalize → Colors change. Re-calling it on the right signal is the whole fix.
/// </para>
/// <para>
/// <b>Why window messages and not <c>SystemEvents.UserPreferenceChanged</c>.</b> The obvious
/// .NET event does not fire for this case. <c>SystemEvents</c> raises
/// <c>UserPreferenceChanged</c> only for recognised <c>SPI_*</c> codes in
/// <c>WM_SETTINGCHANGE</c>'s <c>wParam</c> (high contrast, client-area animation, font
/// smoothing, …). The light/dark "default Windows mode" switch has no SPI code — it broadcasts
/// <c>WM_SETTINGCHANGE</c> with <c>wParam = 0</c> and <c>lParam = "ImmersiveColorSet"</c>,
/// which .NET discards. Verified against the shipped Wpf.Ui 4.3.0 binary: its own
/// <c>SystemThemeWatcher</c> hooks <c>WM_SETTINGCHANGE</c> / <c>WM_THEMECHANGED</c> /
/// <c>WM_SYSCOLORCHANGE</c> and contains no reference to <c>UserPreferenceChanged</c>, nor to
/// "ImmersiveColorSet" or the <c>AppsUseLightTheme</c> registry value. This class watches the
/// same three messages and reuses Wpf.Ui's own cache primitives rather than re-reading the
/// registry.
/// </para>
/// <para>
/// <b>Why not Wpf.Ui's own <c>SystemThemeWatcher.Watch(window)</c>.</b> It is per-window and
/// re-themes only that window. The theme here is app-wide, and Maktaby owns many windows (boxes,
/// widgets, settings, dialogs, the loading splash); watching each one would both miss windows
/// that appear later and fight the app-level apply. One hook on the app's existing hidden
/// message host is the right shape — and that window already exists (<c>App._trayHost</c>), so
/// this adds no new native handle.
/// </para>
/// <para>
/// <b>Why the theme is compared, not just the message.</b> <c>WM_SETTINGCHANGE</c> is extremely
/// chatty — it fires for dozens of unrelated preference changes. Re-applying the theme on every
/// one would swap Application resource dictionaries for nothing, which is visible jank. So the
/// system theme is re-read and the callback only runs on a real transition.
/// </para>
/// </remarks>
[SupportedOSPlatform("windows10.0.14393")]
internal sealed class SystemThemeFollower : IDisposable
{
    private readonly Action _onSystemThemeChanged;
    private HwndSource? _source;
    private HwndSourceHook? _hook;
    private ApplicationTheme? _lastKnownTheme;
    private bool _disposed;

    /// <param name="onSystemThemeChanged">
    /// Invoked on the UI thread when the system light/dark theme actually transitions. Must be
    /// cheap and must not throw; it runs inside a live window-message dispatch.
    /// </param>
    public SystemThemeFollower(Action onSystemThemeChanged)
    {
        _onSystemThemeChanged = onSystemThemeChanged ?? throw new ArgumentNullException(nameof(onSystemThemeChanged));
    }

    /// <summary>
    /// Starts watching on <paramref name="hostWindow"/>, whose HWND must already exist (call
    /// after <c>Show()</c> / <c>SourceInitialized</c>). A no-op if the window has no source yet.
    /// </summary>
    public void Attach(Window hostWindow)
    {
        if (_disposed || _source is not null || hostWindow is null) { return; }

        var hwnd = new WindowInteropHelper(hostWindow).Handle;
        if (hwnd == IntPtr.Zero) { return; }

        var source = HwndSource.FromHwnd(hwnd);
        if (source is null) { return; }

        // Seed from the live theme so a follower attached mid-session still detects the next
        // real change, and so the first (possibly unrelated) message is a no-op.
        _lastKnownTheme = ReadSystemTheme();

        _hook = OnWindowMessage;
        source.AddHook(_hook);
        _source = source;
    }

    /// <summary>
    /// The theme Windows currently asks apps to use, or <c>null</c> when it cannot be read
    /// (or when high contrast is active, which needs Wpf.Ui's own mapping). Deliberately NOT
    /// <c>SystemThemeManager.GetCachedSystemTheme()</c>: that reports the visual-style file
    /// rather than the light/dark preference, so it never changes when the user flips the mode
    /// and the comparison below would never fire. See <see cref="SystemThemeReader"/>.
    /// </summary>
    private static ApplicationTheme? ReadSystemTheme()
    {
        return SystemThemeReader.ReadPreferredAppTheme();
    }

    private IntPtr OnWindowMessage(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        try
        {
            if (_disposed) { return IntPtr.Zero; }

            if (msg is not ((int)Native.Win32Constants.WM_SETTINGCHANGE
                            or (int)Native.Win32Constants.WM_THEMECHANGED
                            or (int)Native.Win32Constants.WM_SYSCOLORCHANGE))
            {
                return IntPtr.Zero;
            }

            var dispatcher = Application.Current?.Dispatcher;
            if (dispatcher is null || dispatcher.HasShutdownStarted || dispatcher.HasShutdownFinished)
            {
                return IntPtr.Zero;
            }

            // A hook runs on the thread owning the HWND, which is the UI thread for the tray
            // host — but the check keeps that an enforced invariant rather than an assumption.
            if (dispatcher.CheckAccess())
            {
                HandlePossibleThemeChange();
            }
            else
            {
                dispatcher.BeginInvoke(new Action(HandlePossibleThemeChange));
            }
        }
        catch
        {
            // Never let a theme broadcast break the message pump: this is cosmetic, and the
            // user can always re-pick the theme from the tray.
        }

        // handled stays false: these messages belong to every other hook and window too, and
        // swallowing them would break unrelated settings propagation.
        return IntPtr.Zero;
    }

    private void HandlePossibleThemeChange()
    {
        if (_disposed) { return; }

        var current = ReadSystemTheme();
        if (current == _lastKnownTheme) { return; }   // WM_SETTINGCHANGE chatter, not a theme change

        // Unknown (registry unreadable, or high contrast is on) means we have no reliable
        // light/dark reading. Re-apply on null would churn the theme on every unrelated
        // WM_SETTINGCHANGE, so only record and act on a real value. A high-contrast toggle
        // still lands, because it changes the value we DO read.
        if (!current.HasValue) { return; }

        _lastKnownTheme = current;
        _onSystemThemeChanged();
    }

    public void Dispose()
    {
        if (_disposed) { return; }
        _disposed = true;

        try
        {
            if (_source is not null && _hook is not null)
            {
                _source.RemoveHook(_hook);
            }
        }
        catch { }
        finally
        {
            _source = null;
            _hook = null;
        }
    }
}
