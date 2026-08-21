namespace DesktopBoxesUI.Core.Interfaces;

/// <summary>
/// Marshals work onto the UI thread. Abstracts the WPF <c>Dispatcher</c> so Core services stay
/// WPF-free.
/// </summary>
public interface IDispatcher
{
    void Invoke(System.Action action);
}
