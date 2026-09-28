namespace Maktaby.Core.Interfaces;

/// <summary>
/// Where, relative to other windows, a window should be placed in the Z-order.
/// Implemented in the Win32 layer; kept here so Core can drive layout decisions.
/// </summary>
public enum ZOrderTarget
{
    Top,
    Bottom,
    TopMost,
    NoTopMost,

    /// <summary>Place the window just above the desktop/Explorer layer (so it sits behind icons-free).</summary>
    BehindDesktopIcons,
}
