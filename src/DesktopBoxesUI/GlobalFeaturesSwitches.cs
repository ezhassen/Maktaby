using System;
using System.Collections.Generic;
using System.Text;

namespace DesktopBoxesUI;

public static class GlobalFeaturesSwitches
{

    /// <summary>
    /// null means do nothing
    /// </summary>
    public static bool? UseGlobalMouseHookInsteadOfCustomSurface { get; } = true;

    public static bool ShowDebugTree { get; } = false;

    public static bool TrayIcon_ShowReset { get; } = false;

}
