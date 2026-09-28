using System;
using System.Windows;

namespace Maktaby.Views;

/// <summary>
/// Global modal loading splash (fixed dark skin, intentionally not themed — same exemption the old
/// <c>LoadingWindow</c> had from the <c>AppFluentWindow</c> rule). Always centered on the primary
/// screen's work area; the service owns its lifetime. Closing is refused while busy (Alt+F4,
/// system close) — only the service ends it, or Cancel when the caller opted into cancellation.
/// </summary>
public partial class LoadingDialog : Window
{
    private bool _allowClose;
    private Action? _onCancel;

    public LoadingDialog()
    {
        InitializeComponent();
        CenterOnPrimary();
        Closing += (_, e) =>
        {
            if (!_allowClose) e.Cancel = true;
        };
    }

    private void CenterOnPrimary()
    {
        // SystemParameters.WorkArea is the primary screen's work area in DIPs.
        var area = SystemParameters.WorkArea;
        Left = area.Left + Math.Max(0, (area.Width - Width) / 2);
        Top = area.Top + Math.Max(0, (area.Height - Height) / 2);
    }

    /// <summary>Thread-safe: the loading task may report from any thread.</summary>
    public void SetMessage(string message)
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.BeginInvoke(() => SetMessage(message));
            return;
        }
        MessageText.Text = message ?? string.Empty;
    }

    /// <summary>Reveals the Cancel button; must be called before showing.</summary>
    public void EnableCancel(Action onCancel)
    {
        _onCancel = onCancel;
        CancelButton.Visibility = Visibility.Visible;
        Height = 210;
        // Grow downward so the dialog stays anchored where it was centered.
    }

    public void AllowClose()
    {
        _allowClose = true;
        // Stop the loop itself, not just visibility (dialog closes right after).
        Spinner.IsIndeterminate = false;
    }

    private void CancelButton_Click(object sender, RoutedEventArgs e)
    {
        CancelButton.IsEnabled = false;
        try { _onCancel?.Invoke(); } catch { }
    }
}
