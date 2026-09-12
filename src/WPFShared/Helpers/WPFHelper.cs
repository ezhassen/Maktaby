using System;
using System.Collections.Generic;
using System.Text;
using System.Windows;
using System.Windows.Interop;

namespace WPFShared.Helpers;

public static class WPFHelper
{
    /// <summary>
    /// Don't perform teardown if we're being detached, use this in events like OnIsVisibleChanged to avoid control disposed issues
    /// </summary>
    /// <param name="frameworkElement"></param>
    /// <returns></returns>
    public static bool IsBeingDetached(this FrameworkElement frameworkElement)
    {
        return PresentationSource.FromVisual(frameworkElement) == null;
    }
}
