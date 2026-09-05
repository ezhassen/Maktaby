using DesktopBoxesUI.Core.Interfaces;
using DesktopBoxesUI.Core.Models;
using DesktopBoxesUI.ViewModels;
using Microsoft.Extensions.DependencyInjection;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Text;
using System.Windows;
using System.Windows.Interop;

namespace DesktopBoxesUI.Views.Containers;

/// <summary>
/// base windows used for common props and methods for the Containers
/// </summary>
public abstract class WidgetWindow : Window
{

    public abstract ContainerViewModel ContainerViewModel { get; }

    public abstract void UpdateChrome();

    /// <summary>
    /// Applies the window geometry from the view-model bounds <see cref="ContainerViewModel"/> in a
    /// single native placement (<see cref="IWindowPositioningService.SetBounds"/>), instead of four
    /// separate <c>Left/Top/Width/Height</c> assignments that would each run layout and fire
    /// <c>SizeChanged</c>/<c>LocationChanged</c> with intermediate states. Model bounds are DIPs, so
    /// they are scaled to physical pixels with the window's DPI first. Falls back to the property
    /// sets when no handle exists yet (pre-show).
    /// </summary>
    public virtual void ApplyGeometry()
    {
        var bounds = ContainerViewModel.Bounds;
        var hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd != IntPtr.Zero)
        {
            try
            {
                double scale = App.Services.GetRequiredService<IDpiService>().GetDpiForWindow(hwnd) / 96.0;
                App.Services.GetRequiredService<IWindowPositioningService>().SetBounds(hwnd, RectD.FromXYWH(
                    bounds.X * scale, bounds.Y * scale, bounds.Width * scale, bounds.Height * scale));
                UpdateChrome();
                return;
            }
            catch { }
        }
        this.Left = bounds.X;
        this.Top = bounds.Y;
        this.Width = bounds.Width;
        this.Height = bounds.Height;
        //
        UpdateChrome();
    }
}
