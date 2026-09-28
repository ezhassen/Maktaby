using Maktaby.Helpers;
using System.Windows;
using System.Windows.Input;

namespace Maktaby.AttachedProperties;

public static class WindowAttachedProperties
{

    #region CloseOnESCKey

    public static bool GetCloseOnESCKey(DependencyObject obj) => (bool)obj.GetValue(CloseOnESCKeyProperty);

    public static void SetCloseOnESCKey(DependencyObject obj, bool value) => obj.SetValue(CloseOnESCKeyProperty, value);

    // Using a DependencyProperty as the backing store for CloseOnESCKey.  This enables animation, styling, binding, etc...
    public static readonly DependencyProperty CloseOnESCKeyProperty =
        DependencyProperty.RegisterAttached("CloseOnESCKey", typeof(bool), typeof(WindowAttachedProperties), new PropertyMetadata(false, new PropertyChangedCallback(OnCloseOnESCKeyPropertyChanged)));

    private static void OnCloseOnESCKeyPropertyChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (!(d is Window window)) return;
        window.KeyDown -= Window_PreviewKeyDown;
        if (e.NewValue is bool && (bool)e.NewValue)
        {
            window.KeyDown += Window_PreviewKeyDown;
        }
    }

    private static void Window_PreviewKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key != System.Windows.Input.Key.Escape || e.IsRepeat) return;
        if (!(sender is Window window)) return;
        if (GetCloseOnESCKey(window))//&& window.IsActive
        {
            window.Close();
        }
    }

    #endregion

    #region Widow Closing


    public static ICommand GetOnClosingCommand(DependencyObject obj) => (ICommand)obj.GetValue(OnClosingCommandProperty);

    public static void SetOnClosingCommand(DependencyObject obj, ICommand value) => obj.SetValue(OnClosingCommandProperty, value);

    // Using a DependencyProperty as the backing store for OnClosingCommand.  This enables animation, styling, binding, etc...
    public static readonly DependencyProperty OnClosingCommandProperty =
        DependencyProperty.RegisterAttached("OnClosingCommand", typeof(ICommand), typeof(WindowAttachedProperties), new PropertyMetadata(null, new PropertyChangedCallback(OnClosingCommandPropertyChanged)));

    private static void OnClosingCommandPropertyChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (!(d is Window window)) return;

        window.Closing -= Window_Closing;
        if (e.NewValue is ICommand)
        {
            window.Closing += Window_Closing;
        }
    }

    public static ICommand GetClosedCommand(DependencyObject obj) => (ICommand)obj.GetValue(ClosedCommandProperty);

    public static void SetClosedCommand(DependencyObject obj, ICommand value) => obj.SetValue(ClosedCommandProperty, value);

    // Using a DependencyProperty as the backing store for ClosedCommand.  This enables animation, styling, binding, etc...
    public static readonly DependencyProperty ClosedCommandProperty =
        DependencyProperty.RegisterAttached("ClosedCommand", typeof(ICommand), typeof(WindowAttachedProperties), new PropertyMetadata(null, OnClosedCommandChanged));

    private static void OnClosedCommandChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (!(d is Window window)) return;

        window.Closed -= Window_Closed;
        if (e.NewValue is ICommand command)
        {
            window.Closed += Window_Closed;
        }
    }

    private static void Window_Closed(object? sender, EventArgs e)
    {
        if (!(sender is Window window)) return;
        var closedCommand = GetClosedCommand(window);
        if (closedCommand != null && closedCommand.CanExecute(null)) closedCommand.Execute(null);
    }

    private static void Window_Closing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        if (!(sender is Window window)) return;
        var OnClosingCommand = GetOnClosingCommand(window);
        if (OnClosingCommand != null)
        {
            if (OnClosingCommand.CanExecute(e))
            {
                OnClosingCommand.Execute(e);
            }
            else
            {
                e.Cancel = true;
            }
        }
    }

    #endregion

    #region MoveWindowOnMouseDown


    public static Window GetMoveWindowOnMouseDown(DependencyObject obj) => (Window)obj.GetValue(MoveWindowOnMouseDownProperty);

    public static void SetMoveWindowOnMouseDown(DependencyObject obj, Window value) => obj.SetValue(MoveWindowOnMouseDownProperty, value);

    // Using a DependencyProperty as the backing store for MoveWindowOnMouseDown.  This enables animation, styling, binding, etc...
    public static readonly DependencyProperty MoveWindowOnMouseDownProperty =
        DependencyProperty.RegisterAttached("MoveWindowOnMouseDown", typeof(Window), typeof(WindowAttachedProperties), new PropertyMetadata(null, new PropertyChangedCallback(OnMoveWindowOnMouseDownPropertyChanged)));

    private static void OnMoveWindowOnMouseDownPropertyChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (!(d is UIElement control)) return;
        control.MouseDown -= Control_MouseDown;
        if (e.NewValue is Window)
        {
            control.MouseDown += Control_MouseDown;
        }
    }

    private static void Control_MouseDown(object sender, MouseButtonEventArgs e)
    {
        if (!(sender is DependencyObject control)) return;
        GetMoveWindowOnMouseDown(control)?.MoveWindowOnMouseDown();
    }


    public static Window GetMoveWindowOnPreviewMouseDown(DependencyObject obj) => (Window)obj.GetValue(MoveWindowOnPreviewMouseDownProperty);

    public static void SetMoveWindowOnPreviewMouseDown(DependencyObject obj, Window value) => obj.SetValue(MoveWindowOnPreviewMouseDownProperty, value);

    // Using a DependencyProperty as the backing store for MoveWindowOnPreviewMouseDown.  This enables animation, styling, binding, etc...
    public static readonly DependencyProperty MoveWindowOnPreviewMouseDownProperty =
        DependencyProperty.RegisterAttached("MoveWindowOnPreviewMouseDown", typeof(Window), typeof(WindowAttachedProperties), new PropertyMetadata(null, new PropertyChangedCallback(OnMoveWindowOnPreviewMouseDownPropertyChanged)));

    private static void OnMoveWindowOnPreviewMouseDownPropertyChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (!(d is UIElement control)) return;
        control.PreviewMouseDown -= Control_PreviewMouseDown;
        if (e.NewValue is Window)
        {
            control.PreviewMouseDown += Control_PreviewMouseDown; ;
        }
    }

    private static void Control_PreviewMouseDown(object? sender, MouseButtonEventArgs e)
    {
        if (!(sender is DependencyObject control)) return;
        e.Handled = false;
        GetMoveWindowOnPreviewMouseDown(control)?.MoveWindowOnMouseDown();
    }

    #endregion
}

