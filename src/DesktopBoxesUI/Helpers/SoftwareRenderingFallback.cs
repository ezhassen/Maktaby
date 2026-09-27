using DesktopBoxesUI.Views.Containers;
using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Media;

namespace DesktopBoxesUI.Helpers;

/// <summary>
/// Shared "render tier fell back to software" state for widget windows. One instance per
/// window, owned by the window, driven from <c>WidgetWindow.OnRenderTierChanged</c>.
/// <para>
/// Two things are released when WPF drops to tier 0 (GDI rasterization), and handed back on
/// recovery:
/// </para>
/// <list type="number">
/// <item>Cached render-target bitmaps. Any non-null <c>CacheMode</c> in the window's content
/// visual owns a cached intermediate surface, and a tier flip underneath it can strand that
/// surface permanently — the same class of leak seen in the wallpaper engine's frame-server
/// paths, where only a driver reset reclaims the memory. Dropping <c>CacheMode</c> is the only
/// lever the host has. Elements are remembered so recovery restores exactly what was dropped;
/// an element with no <see cref="PresentationSource"/> is skipped, so a torn-down plugin is
/// never written to (that would resurrect it and pin its collectible ALC).</item>
/// <item>The chrome overlay. It is a second top-level WPF window with its own render surface,
/// and it re-acquires that surface on every Show/Hide and every <c>SyncFromOwner</c> — all of
/// which a resume DPI-remap triggers. Freezing it hidden stops that churn while the driver is
/// already degraded.</item>
/// </list>
/// <para>
/// No-ops when there is nothing to do, so a window whose content holds no caches (the web
/// widgets: a <c>WebView2CompositionControl</c> with no <c>CacheMode</c>) only pays for the
/// overlay freeze.
/// </para>
/// </summary>
public sealed class SoftwareRenderingFallback
{
    private readonly List<(UIElement Element, CacheMode Mode)> _droppedCaches = new();
    private bool _frozen;

    /// <summary>True while the chrome overlay is frozen (hidden, and not synced). Gate
    /// <c>UpdateChrome</c> on this — otherwise the next hover or activation event re-shows the
    /// overlay and the freeze does nothing.</summary>
    public bool IsFrozen => _frozen;

    /// <summary>Applies (or, with <paramref name="software"/> = false, reverses) the fallback.
    /// <paramref name="reevaluateChrome"/> runs after recovery so the window re-decides whether
    /// chrome should be visible; it is not called while entering the frozen state, because the
    /// freeze is a forced decision that normal hover/activation logic would immediately undo.</summary>
    public void Apply(FrameworkElement? contentRoot, WidgetChromeOverlay? overlay, bool software, Action reevaluateChrome)
    {
        if (software)
        {
            DropCaches(contentRoot);
            Freeze(overlay);
        }
        else
        {
            RestoreCaches();
            Unfreeze(overlay, reevaluateChrome);
        }
    }

    /// <summary>Forgets any dropped cache modes without restoring them. Call when the content
    /// visual goes away (plugin swapped, WebView2 torn down, window closing): the modes belonged
    /// to a tree that no longer exists, and writing them back would resurrect it.</summary>
    public void ForgetCaches() => _droppedCaches.Clear();

    private void DropCaches(FrameworkElement? contentRoot)
    {
        // Clear first: a repeated tier-0 event would otherwise overwrite the saved modes with
        // the nulls we are about to write.
        RestoreCaches();
        if (contentRoot is null) return;
        WidgetRenderCensus.Walk(contentRoot, new WidgetRenderCensus.Entry(), _droppedCaches);
        for (int i = 0; i < _droppedCaches.Count; i++)
        {
            try { _droppedCaches[i].Element.CacheMode = null; } catch { }
        }
    }

    private void RestoreCaches()
    {
        for (int i = 0; i < _droppedCaches.Count; i++)
        {
            var (element, mode) = _droppedCaches[i];
            try
            {
                if (mode is not null && PresentationSource.FromVisual(element) is not null)
                    element.CacheMode = mode;
            }
            catch { }
        }
        _droppedCaches.Clear();
    }

    private void Freeze(WidgetChromeOverlay? overlay)
    {
        if (_frozen) return;
        _frozen = true;
        if (overlay is null) return;
        try
        {
            if (overlay.IsVisible) overlay.Hide();
            overlay.SyncSuppressed = true;
        }
        catch { }
    }

    private void Unfreeze(WidgetChromeOverlay? overlay, Action reevaluateChrome)
    {
        if (!_frozen) return;
        _frozen = false;
        try { if (overlay is not null) overlay.SyncSuppressed = false; } catch { }
        // Also re-syncs the overlay to the owner's post-resume geometry.
        try { reevaluateChrome(); } catch { }
    }
}
