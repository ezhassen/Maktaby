using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace DesktopBoxesUI.ViewModels;

public partial class WindowExceptionHandlerViewModel : ObservableObject
{
    [ObservableProperty]
    private Exception _exception = null!;

    [ObservableProperty]
    private string? _exceptionType;

    /// <summary>True after the user explicitly chooses Continue.</summary>
    public bool HasChosenContinue { get; private set; }

    /// <summary>Raised when the view should close. <c>true</c> = Continue, <c>false</c> = Exit.</summary>
    public event Action<bool>? RequestClose;

    public WindowExceptionHandlerViewModel(Exception exception)
    {
        Exception = exception;
        ExceptionType = exception?.GetType()?.FullName;
    }

    partial void OnExceptionChanged(Exception value)
    {
        ExceptionType = value?.GetType()?.FullName;
    }

    // For XAML binding compatibility, expose RelayCommands as public generated members:
    // CopyDetailsCommand, ContinueCommand, ExitCommand are auto-generated from [RelayCommand] methods.

    [RelayCommand]
    private void CopyDetails()
    {
        try
        {
            var text = BuildDetailsText();
            Clipboard.SetText(text);
        }
        catch
        {
            // Clipboard may be locked.
        }
    }

    [RelayCommand]
    private void Continue()
    {
        HasChosenContinue = true;
        RequestClose?.Invoke(true);
    }

    [RelayCommand]
    private void Exit()
    {
        HasChosenContinue = false;
        RequestClose?.Invoke(false);
    }

    private string BuildDetailsText()
    {
        var ex = Exception;
        if (ex is null) return string.Empty;
        return $"Type: {ExceptionType}{Environment.NewLine}" +
               $"Message: {ex.Message}{Environment.NewLine}" +
               $"StackTrace:{Environment.NewLine}{ex.StackTrace}{Environment.NewLine}" +
               (ex.InnerException is not null
                   ? $"{Environment.NewLine}InnerException: {ex.InnerException}{Environment.NewLine}"
                   : string.Empty) +
               $"{Environment.NewLine}--- Full ToString ---{Environment.NewLine}{ex}";
    }
}
