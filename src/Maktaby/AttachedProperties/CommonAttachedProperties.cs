using System.Windows;
using System.Windows.Input;

namespace Maktaby.AttachedProperties;
/// <summary>
/// Common <see cref="FrameworkElement"/> Attached Properties
/// </summary>
public static class CommonAttachedProperties
{

    public static bool GetIsLoading(DependencyObject obj) => (bool)obj.GetValue(IsLoadingProperty);

    public static void SetIsLoading(DependencyObject obj, bool value) => obj.SetValue(IsLoadingProperty, value);

    // Using a DependencyProperty as the backing store for IsLoading.  This enables animation, styling, binding, etc...
    public static readonly DependencyProperty IsLoadingProperty =
        DependencyProperty.RegisterAttached("IsLoading", typeof(bool), typeof(CommonAttachedProperties), new PropertyMetadata(false));

    public static bool GetIsWorking(DependencyObject obj) => (bool)obj.GetValue(IsWorkingProperty);

    public static void SetIsWorking(DependencyObject obj, bool value) => obj.SetValue(IsWorkingProperty, value);

    // Using a DependencyProperty as the backing store for IsWorking.  This enables animation, styling, binding, etc...
    public static readonly DependencyProperty IsWorkingProperty =
        DependencyProperty.RegisterAttached("IsWorking", typeof(bool), typeof(CommonAttachedProperties), new PropertyMetadata(false));



    public static object GetIcon(DependencyObject obj) => obj.GetValue(IconProperty);

    public static void SetIcon(DependencyObject obj, object value) => obj.SetValue(IconProperty, value);

    // Using a DependencyProperty as the backing store for Icon.  This enables animation, styling, binding, etc...
    public static readonly DependencyProperty IconProperty =
        DependencyProperty.RegisterAttached("Icon", typeof(object), typeof(CommonAttachedProperties), new PropertyMetadata(null));



    public static object GetText(DependencyObject obj)
    {
        return (object)obj.GetValue(TextProperty);
    }

    public static void SetText(DependencyObject obj, object value)
    {
        obj.SetValue(TextProperty, value);
    }

    // Using a DependencyProperty as the backing store for Text.  This enables animation, styling, binding, etc...
    public static readonly DependencyProperty TextProperty =
        DependencyProperty.RegisterAttached("Text", typeof(object), typeof(CommonAttachedProperties), new PropertyMetadata(null));



    public static object GetTag1(DependencyObject obj) => obj.GetValue(Tag1Property);

    public static void SetTag1(DependencyObject obj, object value) => obj.SetValue(Tag1Property, value);

    // Using a DependencyProperty as the backing store for Tag1.  This enables animation, styling, binding, etc...
    public static readonly DependencyProperty Tag1Property =
        DependencyProperty.RegisterAttached("Tag1", typeof(object), typeof(CommonAttachedProperties), new PropertyMetadata(null));

    public static object GetTag2(DependencyObject obj) => obj.GetValue(Tag2Property);

    public static void SetTag2(DependencyObject obj, object value) => obj.SetValue(Tag2Property, value);

    // Using a DependencyProperty as the backing store for Tag2.  This enables animation, styling, binding, etc...
    public static readonly DependencyProperty Tag2Property =
        DependencyProperty.RegisterAttached("Tag2", typeof(object), typeof(CommonAttachedProperties), new PropertyMetadata(null));


    #region MouseWheel

    public static bool GetMouseWheelCommandOnControlKey(DependencyObject obj) => (bool)obj.GetValue(MouseWheelCommandOnControlKeyProperty);

    public static void SetMouseWheelCommandOnControlKey(DependencyObject obj, bool value) => obj.SetValue(MouseWheelCommandOnControlKeyProperty, value);

    // Using a DependencyProperty as the backing store for MouseWheelCommandOnControlKey.  This enables animation, styling, binding, etc...
    public static readonly DependencyProperty MouseWheelCommandOnControlKeyProperty =
        DependencyProperty.RegisterAttached("MouseWheelCommandOnControlKey", typeof(bool), typeof(CommonAttachedProperties), new PropertyMetadata(true));


    public static ICommand GetMouseWheelCommand(DependencyObject obj) => (ICommand)obj.GetValue(MouseWheelCommandProperty);

    public static void SetMouseWheelCommand(DependencyObject obj, ICommand value) => obj.SetValue(MouseWheelCommandProperty, value);

    // Using a DependencyProperty as the backing store for MouseWheelCommand.  This enables animation, styling, binding, etc...
    public static readonly DependencyProperty MouseWheelCommandProperty =
        DependencyProperty.RegisterAttached("MouseWheelCommand", typeof(ICommand), typeof(CommonAttachedProperties), new PropertyMetadata(null, new PropertyChangedCallback(OnMouseWheelCommandPropertyChanged)));

    private static void OnMouseWheelCommandPropertyChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (!(d is FrameworkElement control)) return;
        control.PreviewMouseWheel -= Control_MouseWheel;
        if (e.NewValue is ICommand)
        {
            control.PreviewMouseWheel += Control_MouseWheel;
        }
    }

    private static void Control_MouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (!(sender is FrameworkElement control)) return;
        if (!(GetMouseWheelCommand(control) is ICommand command)) return;
        if (GetMouseWheelCommandOnControlKey(control) && !Keyboard.Modifiers.HasFlag(ModifierKeys.Control)) return;
        if (command.CanExecute(e.Delta))
        {
            e.Handled = true;//Handle if there is scroll bar
            command.Execute(e.Delta);
        }
    }

    #endregion

    #region ControlOnLoad


    public static ICommand GetOnLoadCommand(DependencyObject obj) => (ICommand)obj.GetValue(OnLoadCommandProperty);

    public static void SetOnLoadCommand(DependencyObject obj, ICommand value) => obj.SetValue(OnLoadCommandProperty, value);

    // Using a DependencyProperty as the backing store for OnLoadCommand.  This enables animation, styling, binding, etc...
    public static readonly DependencyProperty OnLoadCommandProperty =
        DependencyProperty.RegisterAttached("OnLoadCommand", typeof(ICommand), typeof(CommonAttachedProperties), new PropertyMetadata(null, new PropertyChangedCallback(OnOnLoadCommandPropertyChanged)));

    private static void OnOnLoadCommandPropertyChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (!(d is FrameworkElement control)) return;
        control.Loaded -= Control_Loaded;
        if (e.NewValue is ICommand)
        {
            control.Loaded += Control_Loaded;
        }
    }


    public static object GetOnLoadCommandParameter(DependencyObject obj) => (object)obj.GetValue(OnLoadCommandParameterProperty);

    public static void SetOnLoadCommandParameter(DependencyObject obj, object value) => obj.SetValue(OnLoadCommandParameterProperty, value);

    // Using a DependencyProperty as the backing store for OnLoadCommandParameter.  This enables animation, styling, binding, etc...
    public static readonly DependencyProperty OnLoadCommandParameterProperty =
        DependencyProperty.RegisterAttached("OnLoadCommandParameter", typeof(object), typeof(CommonAttachedProperties), new PropertyMetadata(null));


    private static void Control_Loaded(object sender, RoutedEventArgs e)
    {
        if (!(sender is DependencyObject control)) return;
        var command = GetOnLoadCommand(control);
        var commandParam = GetOnLoadCommandParameter(control);
        if (command?.CanExecute(commandParam) == true) command?.Execute(commandParam);
    }

    #endregion

    #region ControlUnload

    public static ICommand GetOnUnloadedCommand(DependencyObject obj) => (ICommand)obj.GetValue(OnUnloadedCommandProperty);

    public static void SetOnUnloadedCommand(DependencyObject obj, ICommand value) => obj.SetValue(OnUnloadedCommandProperty, value);

    // Using a DependencyProperty as the backing store for OnUnloadedCommand.  This enables animation, styling, binding, etc...
    public static readonly DependencyProperty OnUnloadedCommandProperty =
        DependencyProperty.RegisterAttached("OnUnloadedCommand", typeof(ICommand), typeof(CommonAttachedProperties), new PropertyMetadata(null, new PropertyChangedCallback(OnOnUnloadedCommandPropertyChanged)));

    private static void OnOnUnloadedCommandPropertyChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (!(d is FrameworkElement control)) return;
        control.Unloaded -= Control_Unloaded;
        if (e.NewValue is ICommand)
        {
            control.Unloaded += Control_Unloaded;
        }
    }
    private static void Control_Unloaded(object sender, RoutedEventArgs e)
    {
        if (!(sender is FrameworkElement control)) return;
        var unloadCommand = GetOnUnloadedCommand(control);
        if (unloadCommand is null) return;
        if (unloadCommand.CanExecute(e))
        {
            unloadCommand.Execute(e);
        }
        else
        {
            e.Handled = true;
            var onHandledCommand = GetOnUnloadHandledCommand(control);
            if (!(onHandledCommand is null) && onHandledCommand.CanExecute(null)) onHandledCommand.Execute(null);
        }
    }

    public static ICommand GetOnUnloadHandledCommand(DependencyObject obj) => (ICommand)obj.GetValue(OnUnloadHandledCommandProperty);

    public static void SetOnUnloadHandledCommand(DependencyObject obj, ICommand value) => obj.SetValue(OnUnloadHandledCommandProperty, value);

    // Using a DependencyProperty as the backing store for OnUnloadHandledCommand.  This enables animation, styling, binding, etc...
    public static readonly DependencyProperty OnUnloadHandledCommandProperty =
        DependencyProperty.RegisterAttached("OnUnloadHandledCommand", typeof(ICommand), typeof(CommonAttachedProperties), new PropertyMetadata(null));

    #endregion

    #region Drag & Drop


    public static ICommand GetDragNDropCommand(DependencyObject obj) => (ICommand)obj.GetValue(DragNDropCommandProperty);

    public static void SetDragNDropCommand(DependencyObject obj, ICommand value) => obj.SetValue(DragNDropCommandProperty, value);

    // Using a DependencyProperty as the backing store for DragNDropCommand.  This enables animation, styling, binding, etc...
    public static readonly DependencyProperty DragNDropCommandProperty =
        DependencyProperty.RegisterAttached("DragNDropCommand", typeof(ICommand), typeof(CommonAttachedProperties), new PropertyMetadata(null, new PropertyChangedCallback(OnDragNDropCommandPropertyChanged)));

    private static void OnDragNDropCommandPropertyChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (!(d is FrameworkElement control)) return;
        control.DragEnter -= Control_DragEnter;
        control.Drop -= Control_Drop;
        if (e.NewValue is ICommand)
        {
            if (!control.AllowDrop) control.AllowDrop = true;
            control.DragEnter += Control_DragEnter;
            control.Drop += Control_Drop;
        }
    }

    private static void Control_DragEnter(object sender, DragEventArgs e)
    {
        if (!(sender is FrameworkElement control)) return;
        var command = GetDragNDropCommand(control);
        if (command is null) return;

        if (!command.CanExecute(e))
        {
            e.Effects = DragDropEffects.None;
        }

    }

    private static void Control_Drop(object sender, DragEventArgs e)
    {
        if (!(sender is FrameworkElement control)) return;
        //var command = GetDragNDropCommand(control);
        //if (command is null) return;
        GetDragNDropCommand(control)?.Execute(e);
    }

    #endregion

}
