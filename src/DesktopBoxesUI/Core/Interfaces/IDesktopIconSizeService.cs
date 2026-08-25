using System;

namespace DesktopBoxesUI.Core.Interfaces;

/// <summary>
/// Resolves and watches the CURRENT desktop icon size (the pixel size Explorer's desktop view is
/// rendering icons at right now). Holds the last resolved value so consumers can fall back to it
/// without touching shell windows, and raises <see cref="Changed"/> when the size changes
/// (desktop View-menu slider, DPI change, Explorer restart).
/// </summary>
public interface IDesktopIconSizeService
{
    /// <summary>Last resolved desktop icon size in pixels (always &gt; 0; 32 as last-resort).</summary>
    int Current { get; }

    /// <summary>Raised after <see cref="Current"/> changes. Value = new size.</summary>
    event Action<int>? Changed;

    /// <summary>Begins monitoring (safe to call multiple times; also re-binds after shell restarts).</summary>
    void Start();

    /// <summary>Suspends monitoring: unhooks events and stops the safety-net timer (used when the
    /// user pinned an explicit DefaultBoxIconSize, making the live value irrelevant).</summary>
    void Stop();

    /// <summary>Re-resolves immediately and raises <see cref="Changed"/> when the value changed.</summary>
    void Refresh();

    /// <summary>Queue a debounced refresh from input paths that observe a possible icon-size gesture
    /// (e.g. Ctrl+wheel being forwarded to Explorer). Cheap when idle.</summary>
    void NotifyPossibleChange();
}
