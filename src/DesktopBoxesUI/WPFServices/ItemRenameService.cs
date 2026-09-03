using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;

namespace DesktopBoxesUI.WPFServices;

/// <summary>
/// Service to manage floating rename popup. Handles IsEditing, StartEdit, Commit and Dismiss with auto-dismiss on lost focus.
/// </summary>
public sealed class ItemRenameService
{
    private Popup? _popup;
    private TextBox? _textBox;
    private string _oldName = string.Empty;
    private Action<string>? _onCommit;
    private Action<string>? _onDismiss;
    private bool _commitOnDismiss;
    private bool _isCommitting;

    public bool IsEditing => _popup?.IsOpen == true;

    /// <summary>
    /// Starts editing with a floating popup over the target element.
    /// </summary>
    /// <param name="oldName">Current name to edit</param>
    /// <param name="targetElement">Element to anchor popup (e.g., Border or ListViewItem)</param>
    /// <param name="minWidth">Min width for TextBox</param>
    /// <param name="maxWidth">Max width for TextBox</param>
    /// <param name="fontSize">Font size</param>
    /// <param name="onCommit">Called with new name when committed (Enter or commitOnDismiss)</param>
    /// <param name="onDismiss">Called when dismissed without commit (Esc or click away when commitOnDismiss=false)</param>
    /// <param name="commitOnDismiss">If true, clicking outside/lost focus commits; otherwise dismisses</param>
    public void StartEdit(string oldName, FrameworkElement targetElement, double minWidth, double maxWidth, double fontSize, Action<string> onCommit, Action<string>? onDismiss = null, bool commitOnDismiss = true)
    {
        DismissInternal(false);

        _oldName = oldName;
        _onCommit = onCommit;
        _onDismiss = onDismiss;
        _commitOnDismiss = commitOnDismiss;

        var border = new Border
        {
            Background = (Brush)Application.Current.Resources["BoxBackground"] ?? new SolidColorBrush(Color.FromRgb(45, 45, 48)),
            BorderBrush = (Brush)Application.Current.Resources["BoxBorder"] ?? new SolidColorBrush(Color.FromRgb(63, 63, 70)),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(4),
            Padding = new Thickness(6, 4, 6, 4),
            Effect = new System.Windows.Media.Effects.DropShadowEffect { Color = Colors.Black, Opacity = 0.3, BlurRadius = 8, ShadowDepth = 2, Direction = 270 }
        };

        _textBox = new TextBox
        {
            Text = oldName,
            MinWidth = minWidth,
            MaxWidth = maxWidth,
            FontSize = fontSize,
            Foreground = (Brush)Application.Current.Resources["BoxForeground"] ?? Brushes.White,
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            TextAlignment = TextAlignment.Center,
            TextWrapping = TextWrapping.Wrap,
            AcceptsReturn = false,
            VerticalContentAlignment = VerticalAlignment.Center,
            Padding = new Thickness(10, 4, 10, 4),
            Margin = new Thickness(2)
        };
        border.Child = _textBox;

        _popup = new Popup
        {
            Child = border,
            PlacementTarget = targetElement,
            Placement = PlacementMode.Center,
            AllowsTransparency = true,
            StaysOpen = false,
            PopupAnimation = PopupAnimation.Fade,
            Focusable = true
        };

        _textBox.KeyDown += OnTextBoxKeyDown;
        _textBox.LostFocus += OnTextBoxLostFocus;
        _popup.Closed += OnPopupClosed;
        _popup.Opened += OnPopupOpened;

        _popup.IsOpen = true;
    }

    private void OnPopupOpened(object? sender, EventArgs e)
    {
        if (_textBox == null) return;
        _textBox.Focus();
        _textBox.SelectAll();
        Keyboard.Focus(_textBox);
    }

    private void OnTextBoxKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            CommitRename();
            e.Handled = true;
        }
        else if (e.Key == Key.Escape)
        {
            DismissInternal(false);
            e.Handled = true;
        }
    }

    private void OnTextBoxLostFocus(object sender, RoutedEventArgs e)
    {
        // Auto-dismiss on lost focus
        if (_commitOnDismiss)
            CommitRename();
        else
            DismissInternal(false);
    }

    private void OnPopupClosed(object? sender, EventArgs e)
    {
        // When StaysOpen=false, clicking outside closes popup without going through Dismiss -> handle commit/dismiss
        if (_textBox != null && _popup != null && !_isCommitting)
        {
            // Popup closed via outside click
            if (_commitOnDismiss)
                CommitRename();
            else
                DismissInternal(false);
        }
        Cleanup();
    }

    public void CommitRename()
    {
        if (_textBox == null || _popup == null) return;
        string newName = _textBox.Text?.Trim() ?? string.Empty;
        _isCommitting = true;
        try
        {
            _onCommit?.Invoke(newName);
        }
        finally
        {
            _isCommitting = false;
        }
        DismissInternal(true);
    }

    public void Dismiss()
    {
        DismissInternal(false);
    }

    private void DismissInternal(bool wasCommit)
    {
        if (_popup != null)
        {
            _popup.Closed -= OnPopupClosed;
            _popup.IsOpen = false;
        }
        if (_textBox != null)
        {
            _textBox.KeyDown -= OnTextBoxKeyDown;
            _textBox.LostFocus -= OnTextBoxLostFocus;
        }
        var onDismiss = _onDismiss;
        var old = _oldName;
        Cleanup();
        if (!wasCommit)
            onDismiss?.Invoke(old);
    }

    private void Cleanup()
    {
        if (_popup != null)
        {
            _popup.Closed -= OnPopupClosed;
            _popup.Opened -= OnPopupOpened;
        }
        if (_textBox != null)
        {
            _textBox.KeyDown -= OnTextBoxKeyDown;
            _textBox.LostFocus -= OnTextBoxLostFocus;
        }
        _popup = null;
        _textBox = null;
        _onCommit = null;
        _onDismiss = null;
        _oldName = string.Empty;
        _isCommitting = false;
    }
}
