using DesktopBoxesUI.ViewModels;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace DesktopBoxesUI.Controls;

public partial class IconContainer : UserControl
{
    /*public IconContainer()
    {
        InitializeComponent();
        // Bind size to parent FolderPortalControl IconSize via code (set in FolderPortalControl)
        Loaded += OnLoaded;
        // Handle input at container level
        PreviewMouseLeftButtonDown += OnPreviewMouseLeftButtonDown;
        MouseLeftButtonDown += OnMouseLeftButtonDown;
        MouseLeftButtonUp += OnMouseLeftButtonUp;
        MouseMove += OnMouseMove;
        MouseRightButtonUp += OnMouseRightButtonUp;
        PreviewMouseDown += OnPreviewMouseDown;
        GotFocus += OnGotFocus;
        KeyDown += OnKeyDown;
        MouseDoubleClick += OnDoubleClick;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        UpdateSize();
        if (FindParent<FolderPortalControl>(this) is { } host)
            host.RegisterIconContainer(this);
    }

    public void UpdateSize()
    {
        if (FindParent<FolderPortalControl>(this) is not { } host) return;
        double tile = host.IconSize + 36;
        Width = tile;
        Height = tile;
        if (IconImage != null)
        {
            IconImage.Width = host.IconSize;
            IconImage.Height = host.IconSize;
        }
    }

    private FolderItemViewModel? Vm => DataContext as FolderItemViewModel;
    private FolderPortalControl? Host => FindParent<FolderPortalControl>(this);

    private void OnPreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        // Let host handle selection before DataGrid-like logic? Host will handle via Root preview, but we also need to ensure drag start
        // This is handled at host level via IconContainer events; just ensure focus
    }

    private void OnMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        var vm = Vm;
        var host = Host;
        if (vm == null || host == null) return;
        Focus();
        if (vm.IsEditing) { e.Handled = true; return; }

        // Delegate selection to host (handles Ctrl/Shift/Alt, anchor, drag start)
        host.HandleIconLeftButtonDown(this, vm, e);
        // e.Handled set by host
    }

    private void OnMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        var vm = Vm;
        var host = Host;
        if (vm == null || host == null) return;
        host.HandleIconLeftButtonUp(this, vm, e);
    }

    private void OnMouseMove(object sender, MouseEventArgs e)
    {
        var vm = Vm;
        var host = Host;
        if (vm == null || host == null) return;
        host.HandleIconMouseMove(this, vm, e);
    }

    private void OnPreviewMouseDown(object sender, MouseButtonEventArgs e)
    {
        var vm = Vm;
        var host = Host;
        if (vm == null || host == null) return;
        host.HandleIconPreviewMouseDown(this, vm, e);
    }

    private void OnDoubleClick(object sender, MouseButtonEventArgs e)
    {
        var vm = Vm;
        var host = Host;
        if (vm == null || host == null) return;
        host.HandleIconDoubleClick(this, vm, e);
    }

    private void OnMouseRightButtonUp(object sender, MouseButtonEventArgs e)
    {
        var vm = Vm;
        var host = Host;
        if (vm == null || host == null) return;
        host.HandleIconRightButtonUp(this, vm, e);
    }

    private void OnGotFocus(object sender, RoutedEventArgs e)
    {
        var vm = Vm;
        var host = Host;
        if (vm == null || host == null) return;
        host.HandleIconGotFocus(this, vm);
    }

    private void OnKeyDown(object sender, KeyEventArgs e)
    {
        var vm = Vm;
        var host = Host;
        if (vm == null || host == null) return;
        host.HandleIconKeyDown(this, vm, e);
    }

    private static T? FindParent<T>(DependencyObject child) where T : DependencyObject
    {
        DependencyObject? cur = child;
        while (cur != null)
        {
            if (cur is T t) return t;
            cur = VisualTreeHelper.GetParent(cur);
        }
        return null;
    }*/
}
