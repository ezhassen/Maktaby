using DesktopBoxesUI.Helpers;
using DesktopBoxesUI.Win32.NativeMethods;
using System;
using System.Runtime.Versioning;
using System.Windows;
using System.Windows.Interop;
using WindowsNative;
using static WindowsNative.Win32Constants;

namespace DesktopBoxesUI.Win32.Services;

/// <summary>
/// Single home for the "desktop band" glue every desktop window needs: tool-window styles
/// (no taskbar button, no Alt-Tab), desktop ownership, minimize immunity, and the z-order pin
/// that keeps click-activation from raising the window above normal applications.
///
/// This replaces the per-window copies that used to live in <c>WindowDragController.Attach</c>,
/// <c>NativeWidgetWindow</c>, <c>WebWidgetWindow</c>, <c>WidgetChromeOverlay</c> and
/// <c>DesktopSurface</c>. Behaviour is unchanged — the same <see cref="Win32Apis"/> primitives
/// run in the same order — they are just invoked from one place now.
///
/// What this deliberately does NOT do: turn windows into true children of Progman/WorkerW the
/// way the live-wallpaper engine does. The engine sits BELOW the icons; these windows must sit
/// ABOVE them (visible, clickable, focusable), and a layered WPF child paints behind its
/// non-layered Explorer siblings. Owned top-levels plus the pin are the correct mechanism here.
/// </summary>
[SupportedOSPlatform("windows10.0.14393")]
internal static class DesktopLayer
{
    private const int WM_MOUSEACTIVATE = 0x0021;
    private const int WM_ACTIVATE = 0x0006;
    private const int WM_WINDOWPOSCHANGING = 0x0046;
    private const int MA_ACTIVATE = 1;

    internal enum Kind
    {
        /// <summary>A <c>BoxContainerWindow</c>: owned by the surface (or Progman), click-activates.</summary>
        Box,
        /// <summary>A <c>NativeWidgetWindow</c>/<c>WebWidgetWindow</c>: like <see cref="Box"/> plus
        /// active-state tracking for the chrome overlay.</summary>
        Widget,
        /// <summary>The <c>WidgetChromeOverlay</c>: never activates itself (it forwards activation
        /// to its owner) and pins below that owner instead of below the desktop band.</summary>
        Overlay,
        /// <summary>The <c>DesktopSurface</c>: glued via <see cref="Win32Apis.GlueToDesktopSurface"/>
        /// (which applies its own styles); keeps its bespoke input hook, so no layer hook here.</summary>
        Surface,
    }

    internal sealed class Options
    {
        public required Kind Kind { get; init; }
        public required DesktopManager DesktopManager { get; init; }

        /// <summary>Returned for <see cref="WM_MOUSEACTIVATE"/> (after <see cref="OnMouseActivate"/>
        /// runs). Boxes/widgets activate on click (<c>MA_ACTIVATE</c>); the overlay stays
        /// non-activating (<c>MA_NOACTIVATE = 3</c>).</summary>
        public int MouseActivateResult { get; init; } = MA_ACTIVATE;

        /// <summary>Extra click work, e.g. the overlay activating its owner window.</summary>
        public Action? OnMouseActivate { get; init; }

        /// <summary>When true, the z-order pin is skipped for this reposition — e.g. while the
        /// chrome overlay drags its owner via Left/Top with SWP_NOZORDER (a pure z-order change
        /// the pin would otherwise misread and fight).</summary>
        public Func<bool>? KeepBelowSuppressed { get; init; }

        /// <summary>Receives the <c>WM_ACTIVATE</c> active flag (widgets mirror it into the chrome).</summary>
        public Action<bool>? ActiveChanged { get; init; }

        /// <summary>Pin target for <see cref="Kind.Overlay"/>: the owner window, above which the
        /// overlay is inserted directly (an owned window must sit immediately above its owner).
        /// Unused by other kinds (they use the desktop-band walk).</summary>
        public Func<IntPtr>? KeepBelowAnchor { get; init; }

        /// <summary>An owned window (the widget chrome overlay) that must stay directly above this
        /// window. Re-pinned after every reposition of the owner: showing/raising the owner
        /// otherwise inserts it at the top, burying the overlay behind the widget.</summary>
        public Func<IntPtr>? KeepAbove { get; init; }
    }

    /// <summary>
    /// Applies tool-window styles, desktop ownership and minimize immunity, then installs the
    /// shared layer hook plus <see cref="Win32Apis.MinimizePreventionHook"/>. Returns the layer
    /// hook delegate (null for <see cref="Kind.Surface"/>) for <see cref="Detach"/>.
    /// </summary>
    internal static HwndSourceHook? Attach(Window window, Options options)
    {
        var hwnd = new WindowInteropHelper(window).Handle;
        return Attach(hwnd, options);
    }

    internal static HwndSourceHook? Attach(IntPtr hwnd, Options options)
    {
        if (hwnd == IntPtr.Zero)
        {
            return null;
        }

        try
        {
            if (options.Kind == Kind.Surface)
            {
                // Applies tool-window + minimize styles internally.
                Win32Apis.GlueToDesktopSurface(hwnd);
            }
            else
            {
                Win32Apis.MakeToolWindow(hwnd);
                if (options.Kind != Kind.Overlay)
                {
                    // Own the window to the DesktopSurface when the custom surface is live (handle
                    // published); owned windows always float above their owner, so a box can never
                    // sink below (or lose clicks/activation to) the surface. Falls back to Progman
                    // when no surface exists. The overlay is already owned via WPF Owner — re-owning
                    // it here would break the owner chain.
                    Win32Apis.GlueToDesktop(hwnd, Win32Apis.DesktopSurfaceHandle);
                }
                else
                {
                    // Must stay non-activating: Show() must not steal activation from the owner.
                    // XAML already requests WS_EX_NOACTIVATE, but enforce it explicitly for builds
                    // where style changes can be reapplied.
                    Win32Apis.EnforceNoActivate(hwnd);
                }

                Win32Apis.PreventMinimize(hwnd);
            }
        }
        catch
        {
            // Window setup is best-effort; the window still shows.
        }

        var source = HwndSource.FromHwnd(hwnd);
        if (source is null)
        {
            return null;
        }

        HwndSourceHook? layerHook = null;
        if (options.Kind != Kind.Surface)
        {
            layerHook = (h, msg, wParam, lParam, ref handled) => LayerHook(h, msg, wParam, lParam, ref handled, options);
            source.AddHook(layerHook);
        }

        source.AddHook(Win32Apis.MinimizePreventionHook);
        return layerHook;
    }

    internal static void Detach(HwndSource? source, HwndSourceHook? layerHook)
    {
        if (source is null)
        {
            return;
        }

        if (layerHook is not null)
        {
            try { source.RemoveHook(layerHook); } catch { }
        }

        try { source.RemoveHook(Win32Apis.MinimizePreventionHook); } catch { }
    }

    private static IntPtr LayerHook(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled, Options o)
    {
        if (msg == WM_MOUSEACTIVATE)
        {
            // Activate on click but don't let the activation raise the window above normal apps —
            // the WM_WINDOWPOSCHANGING pin below keeps it in the desktop band.
            handled = true;
            try { o.OnMouseActivate?.Invoke(); } catch { }
            return (IntPtr)o.MouseActivateResult;
        }

        if (msg == WM_ACTIVATE)
        {
            if (o.ActiveChanged is not null)
            {
                int low = wParam.ToInt32() & 0xFFFF;
                try { o.ActiveChanged(low != 0); } catch { } // WA_INACTIVE = 0
            }

            return IntPtr.Zero;
        }

        if (msg == WM_WINDOWPOSCHANGING)
        {
            WindowDragController.SuppressShellSnap(lParam);
            bool suppressed = o.KeepBelowSuppressed?.Invoke() == true;
            if (!suppressed)
            {
                var anchor = o.KeepBelowAnchor?.Invoke() ?? IntPtr.Zero;
                if (o.Kind == Kind.Overlay && anchor != IntPtr.Zero)
                {
                    // An overlay pins DIRECTLY above its owner — never the band walk. The walk
                    // descends below the owner and buries the overlay behind the widget (flapping
                    // against EnsureAbove on every hover/sync geometry touch).
                    PinAboveOwner(lParam, anchor);
                }
                else if (o.Kind != Kind.Overlay)
                {
                    WindowDragController.KeepBelowApps(hwnd, lParam, o.DesktopManager);
                }
            }
            else if (o.Kind != Kind.Surface)
            {
                // Drag/resize in progress: geometry flows untouched, but a pure z-order TOP/foreign
                // request mid-gesture must not pass through either (it lifts the dragged window
                // above the app in front). NOZORDER keeps the current z instead of pinning down,
                // so the gesture is never yanked away mid-drag. Fresh shows stay exempt.
                var wpHold = System.Runtime.InteropServices.Marshal.PtrToStructure<WindowPos>(lParam);
                bool pureZ = (wpHold.Flags & SWP_NOZORDER) == 0
                    && (wpHold.Flags & SWP_NOMOVE) != 0 && (wpHold.Flags & SWP_NOSIZE) != 0
                    && (wpHold.Flags & SWP_SHOWWINDOW) == 0;
                if (pureZ && (wpHold.HwndInsertAfter == IntPtr.Zero || !o.DesktopManager.IsDesktopWindow(wpHold.HwndInsertAfter)))
                {
                    wpHold.Flags |= SWP_NOZORDER;
                    System.Runtime.InteropServices.Marshal.StructureToPtr(wpHold, lParam, false);
                }
            }

            // Move/size repositions (TitleDrag, snap, native resize) must never raise the window:
            // WPF issues them asking for TOP, which would lift a dragged widget above the app in
            // front. Forcing NOZORDER keeps the current z while the geometry applies. Fresh shows
            // (SWP_SHOWWINDOW) are exempt — a new window should pop above for feedback.
            if (o.Kind != Kind.Surface)
            {
                var wpMove = System.Runtime.InteropServices.Marshal.PtrToStructure<WindowPos>(lParam);
                bool hasMoveOrSize = (wpMove.Flags & SWP_NOZORDER) == 0
                    && ((wpMove.Flags & SWP_NOMOVE) == 0 || (wpMove.Flags & SWP_NOSIZE) == 0)
                    && (wpMove.Flags & SWP_SHOWWINDOW) == 0;
                if (hasMoveOrSize && (wpMove.HwndInsertAfter == IntPtr.Zero || !o.DesktopManager.IsDesktopWindow(wpMove.HwndInsertAfter)))
                {
                    wpMove.Flags |= SWP_NOZORDER;
                    System.Runtime.InteropServices.Marshal.StructureToPtr(wpMove, lParam, false);
                }
            }

            try
            {
                var above = o.KeepAbove?.Invoke() ?? IntPtr.Zero;
                EnsureAbove(hwnd, above);
            }
            catch { }

            return IntPtr.Zero;
        }

        return IntPtr.Zero;
    }

    /// <summary>
    /// Rewrites a pending reposition to insert directly above <paramref name="owner"/> (in-place
    /// struct edit — no extra <c>SetWindowPos</c>, so geometry and the message flow are untouched
    /// and this cannot recurse). An owned overlay must always sit immediately above its owner.
    /// </summary>
    private static void PinAboveOwner(IntPtr lParam, IntPtr owner)
    {
        var wp = System.Runtime.InteropServices.Marshal.PtrToStructure<WindowPos>(lParam);
        if (wp.HwndInsertAfter != owner)
        {
            wp.HwndInsertAfter = owner;
            System.Runtime.InteropServices.Marshal.StructureToPtr(wp, lParam, false);
        }
    }

    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    private struct WindowPos
    {
        public IntPtr Hwnd;
        public IntPtr HwndInsertAfter;
        public int X;
        public int Y;
        public int CX;
        public int CY;
        public uint Flags;
    }

    /// <summary>
    /// Inserts <paramref name="owned"/> directly above <paramref name="owner"/> (no move, size or
    /// activation change) unless it is already there. "Already there" walks down from the owned
    /// side past topmost/invisible system windows (IME, open menus, drop shadows) to the nearest
    /// ordinary window and expects the owner: testing the immediate neighbour misfires whenever
    /// such a window floats between them.
    /// The already-there check bounds the work to a single correction: the resulting reposition
    /// notifies the owned window's own hook, which performs no further <c>SetWindowPos</c>, so
    /// this cannot recurse.
    /// </summary>
    private static void EnsureAbove(IntPtr owner, IntPtr owned)
    {
        if (owned == IntPtr.Zero || owned == owner || !Win32Apis.IsWindow(owned))
        {
            return;
        }

        if (NextOrdinaryBelow(owned) == owner)
        {
            return;
        }

        User32.SetWindowPos(owned, owner, 0, 0, 0, 0,
            SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE);
    }

    /// <summary>
    /// Nearest window below <paramref name="hwnd"/> that actually paints there: skips topmost
    /// popups (they float above everything by design) and invisible windows. Bounded walk.
    /// </summary>
    internal static IntPtr NextOrdinaryBelow(IntPtr hwnd)
    {
        IntPtr cur = hwnd;
        for (int i = 0; i < 64; i++)
        {
            cur = Win32Apis.GetWindow(cur, GW_HWNDNEXT);
            if (cur == IntPtr.Zero)
            {
                return IntPtr.Zero;
            }

            try
            {
                int ex = User32.GetWindowLong(cur, GWL_EXSTYLE);
                if ((ex & (int)WS_EX_TOPMOST) != 0)
                {
                    continue;
                }

                if (!Win32Apis.IsWindowVisible(cur))
                {
                    continue;
                }

                return cur;
            }
            catch
            {
                return cur;
            }
        }

        return IntPtr.Zero;
    }
}
