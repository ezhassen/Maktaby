using DesktopBoxesUI.Core.Interfaces;
using DesktopBoxesUI.Core.Models;
using DesktopBoxesUI.ViewModels;
using Microsoft.Extensions.DependencyInjection;
using System;
using System.Collections.Generic;
using System.ComponentModel;
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
    /// The on-screen rectangle this window should currently display, derived from the model.
    /// Used by post-rescale verification to detect live windows that drifted from the layout
    /// (stale overlay sync, late DPI remapping, coercion) and push them back.
    /// </summary>
    public virtual RectD GetExpectedDisplayRect() => ContainerViewModel.Bounds;

    /// <summary>
    /// Applies the window geometry from the view-model bounds <see cref="ContainerViewModel"/> using WPF
    /// DIPs. WPF maps those DIPs to physical pixels with each window's current DPI context, so callers
    /// must not pre-scale them through a separately queried monitor DPI (that scale can be stale
    /// mid-transition and shift the window).
    /// </summary>
    public virtual void ApplyGeometry()
    {
        var bounds = ContainerViewModel.Bounds;
        /*var hwnd = new WindowInteropHelper(this).Handle;
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
        }*/
        this.Left = bounds.X;
        this.Top = bounds.Y;
        this.Width = bounds.Width;
        this.Height = bounds.Height;
        //
        UpdateChrome();
    }

    public bool IsClosing { get; set; }
    public bool IsClosed { get; set; }
    protected override void OnClosing(CancelEventArgs e)
    {
        base.OnClosing(e);
        if (!e.Cancel)
        {
            IsClosing = true;
        }
    }
    protected override void OnClosed(EventArgs e)
    {
        base.OnClosed(e);
        IsClosed = true;
    }
}
