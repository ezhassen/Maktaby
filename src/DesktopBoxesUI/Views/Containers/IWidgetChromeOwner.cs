using DesktopBoxesUI.ViewModels;
using System.Windows;
using System.Windows.Controls;

namespace DesktopBoxesUI.Views.Containers;

/// <summary>What <see cref="CssWidgetChromeOverlay"/> needs from the window it frames.
/// Implemented by <see cref="CssWidgetWindow"/> and <see cref="NativeWidgetWindow"/> so the
/// overlay stays single-sourced: geometry/events flow through <see cref="Window"/>, widget
/// behavior through the rest.</summary>
public interface IWidgetChromeOwner
{
    Window Window { get; }
    ContainerViewModel ContainerViewModel { get; }
    MenuItem? LockMenuItem { get; }
    void ShowWidgetMenu();
    void SetHover(bool hover);
}
