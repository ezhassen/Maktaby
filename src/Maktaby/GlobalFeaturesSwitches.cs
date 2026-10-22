using System;
using System.Collections.Generic;
using System.Text;

namespace Maktaby;

public static class GlobalFeaturesSwitches
{

    /// <summary>
    /// null means do nothing
    /// </summary>
    public static bool? UseGlobalMouseHookInsteadOfCustomSurface { get; } = false;

    public static bool ShowDebugTree { get; } = false;

    public static bool TrayIcon_ShowReset { get; } = false;
    public static bool TrayIcon_ShowTestButton { get; } = false;

    public static bool EnableHealthSnapshots { get; } = false;


    //public static bool DisableIsPortableBuildDetection { get; } = false;

    /// <summary>
    /// DEBUG ONLY. Forces the update PROMPT to open with a synthetic release, so the UI and
    /// the choice handling can be exercised on a build whose version matches the published
    /// release — the normal case where a check correctly answers "up to date" and the window
    /// never appears, leaving no way to test the buttons.
    /// </summary>
    /// <remarks>
    /// Applies to INTERACTIVE checks only (tray "Check for updates…", Settings "Check now"),
    /// so it never interrupts the app at startup or on the 6-hour timer. It bypasses the
    /// portable-build gate deliberately: a dev build run from the repository has no installer
    /// and is therefore detected as portable, which would otherwise suppress the very window
    /// being tested.
    /// </remarks>
    public static bool SimulateUpdateAvailable { get; } = false;

    /// <summary>
    /// DEBUG ONLY. Publishes <see cref="Maktaby.WidgetSdk.WidgetDev.SimulateData"/> so a native
    /// widget that hits a live service can serve generated data instead. The point is a
    /// repeatable dev loop: a weather widget otherwise refetches on every window open, every
    /// resume and every 30-minute tick, which makes a layout change impossible to eyeball
    /// twice in a row (and hammers a free-tier endpoint).
    /// </summary>
    /// <remarks>
    /// A single global switch rather than a manifest flag, because it must be impossible for
    /// a shipped widget to ship with simulation on: a release build never sets this, and the
    /// widget's own code still has to opt in by checking the flag before touching the
    /// network. Off in release.
    /// </remarks>
    public static bool SimulateNativeWidgetData { get; } = true;
}