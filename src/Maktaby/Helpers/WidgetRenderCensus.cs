using System.Collections.Generic;
using System.Text;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Effects;

namespace Maktaby.Helpers;

/// <summary>
/// Black-box census of the rendering resources a widget window is holding, so a
/// native-memory climb around a WPF render-tier transition is attributable from the
/// log alone. Counts only what is reachable through the visual tree — cached
/// render-target bitmaps (<see cref="System.Windows.Media.BitmapCache"/>) and
/// effects (which each own a cached intermediate surface) are the two that strand
/// native memory when the tier flips underneath them, so they are the signal.
/// <para>
/// Storyboards and DispatcherTimers are deliberately NOT counted: neither is
/// reachable from the visual tree and neither has a public enumeration. A widget
/// leaking a running animation clock will show up as climbing CPU with a flat
/// census, not as a climbing census.
/// </para>
/// </summary>
public static class WidgetRenderCensus
{
    /// <summary>One window's worth of resource counts, plus the cached elements
    /// themselves so the tier-0 path can drop and later restore them.</summary>
    public sealed class Entry
    {
        public string Label = "?";
        public int Visuals;
        public int CachedBitmaps;
        public int Effects;
        /// <summary>Elements whose <see cref="UIElement.CacheMode"/> is non-null, with the
        /// mode to hand back on recovery. Empty unless a census ran in collect mode.</summary>
        public readonly List<(UIElement Element, CacheMode Mode)> SavedCaches = new();
    }

    /// <summary>Walks <paramref name="root"/> and counts visuals, cached bitmaps and effects.
    /// When <paramref name="collect"/> is non-null, the cached elements are recorded in it
    /// (so a later call can restore exactly what was dropped) instead of on a throwaway entry.</summary>
    public static void Walk(FrameworkElement root, Entry entry, List<(UIElement, CacheMode)>? collect = null)
    {
        if (root is null) return;
        // Iterative walk: a deep visual tree would blow the stack on recursion, and plugin
        // visuals are arbitrary depth.
        var stack = new Stack<DependencyObject>();
        stack.Push(root);
        while (stack.Count > 0)
        {
            var node = stack.Pop();
            entry.Visuals++;
            if (node is UIElement fe)
            {
                var cache = fe.CacheMode;
                if (cache is not null)
                {
                    entry.CachedBitmaps++;
                    collect?.Add((fe, cache));
                }
                if (fe.Effect is Effect) entry.Effects++;
            }
            int count = 0;
            try { count = VisualTreeHelper.GetChildrenCount(node); } catch { continue; }
            for (int i = 0; i < count; i++)
            {
                try
                {
                    var child = VisualTreeHelper.GetChild(node, i);
                    if (child is not null) stack.Push(child);
                }
                catch { }
            }
        }
    }

    /// <summary>Census of every window passed, for the tier-transition log line.</summary>
    public static void WalkAll(IEnumerable<(string Label, FrameworkElement Root)> roots, List<Entry> into)
    {
        foreach (var (label, root) in roots)
        {
            var entry = new Entry { Label = label };
            Walk(root, entry);
            into.Add(entry);
        }
    }

    /// <summary>"clock=cache:1,fx:2 vis:214" style per-window summary.</summary>
    public static string Describe(IReadOnlyList<Entry> entries)
    {
        if (entries.Count == 0) return "no widget windows";
        var sb = new StringBuilder();
        int totalCache = 0, totalFx = 0, totalVis = 0;
        for (int i = 0; i < entries.Count; i++)
        {
            var e = entries[i];
            totalCache += e.CachedBitmaps;
            totalFx += e.Effects;
            totalVis += e.Visuals;
            if (i > 0) sb.Append(", ");
            sb.Append(e.Label).Append("=cache:").Append(e.CachedBitmaps)
              .Append(",fx:").Append(e.Effects)
              .Append(",vis:").Append(e.Visuals);
        }
        sb.Append(" | total cache=").Append(totalCache)
          .Append(" fx=").Append(totalFx)
          .Append(" visuals=").Append(totalVis);
        return sb.ToString();
    }
}
