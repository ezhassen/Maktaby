namespace DesktopBoxesUI.Core.Interfaces;

/// <summary>
/// Well-known settings keys and their global defaults.
/// </summary>
public static class SettingsKeys
{
    /// <summary>
    /// Global default box transparency, 0 = fully opaque .. 1 = fully transparent.
    /// Used when a <see cref="Core.Models.Box"/> does not override it.
    /// </summary>
    public const string DefaultBoxTransparency = "DefaultBoxTransparency";

    public const double DefaultBoxTransparencyValue = 0.5d;
}
