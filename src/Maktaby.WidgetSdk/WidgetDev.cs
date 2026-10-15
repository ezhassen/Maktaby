namespace Maktaby.WidgetSdk;

/// <summary>Development-only hooks the host sets and a native widget may read. Exists so a
/// widget can be exercised without calling a live service on every window open, every resume
/// and every refresh tick — which makes layout work impossible to eyeball twice in a row.
///
/// Nothing here changes behaviour unless the host sets it: the host wires it from
/// <c>GlobalFeaturesSwitches.SimulateNativeWidgetData</c>, which is <c>false</c> in a release
/// build. A widget MUST still check the flag itself before skipping its own I/O; this only
/// publishes the decision, it does not perform it.</summary>
public static class WidgetDev
{
    /// <summary>When true, a widget that fetches from a live service may serve generated data
    /// instead. Default false; set by the host at startup, never by a plugin.</summary>
    public static bool SimulateData { get; set; }

    /// <summary>Resets every hook to its shipping value. For tests and for the host's own
    /// shutdown path, so a value set once cannot leak into a later window.</summary>
    public static void Reset() => SimulateData = false;
}
