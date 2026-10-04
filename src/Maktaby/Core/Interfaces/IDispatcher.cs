namespace Maktaby.Core.Interfaces;

/// <summary>
/// Marshals work onto the UI thread. Abstracts the WPF <c>Dispatcher</c> so Core services stay
/// WPF-free.
/// </summary>
public interface IDispatcher
{
    /// <summary>
    /// Queues the delegate to run on the UI thread without waiting for completion.
    /// </summary>
    void BeginInvoke(System.Action action);

    /// <summary>
    /// Queues the task-returning delegate to run on the UI thread without waiting for completion.
    /// </summary>
    void BeginInvoke(Func<Task> asyncAction);

    /// <summary>
    /// Invokes the delegate on the UI thread and waits for it to finish.
    /// </summary>
    void Invoke(System.Action action);

    /// <summary>
    /// Invokes the delegate on the UI thread and returns its result after completion.
    /// </summary>
    T Invoke<T>(Func<T> func);

    /// <summary>
    /// Executes the action on the UI thread and awaits completion.
    /// </summary>
    Task InvokeAsync(Action action);

    /// <summary>
    /// Executes the task-returning delegate on the UI thread and awaits the full task completion.
    /// </summary>
    Task InvokeAsync(Func<Task> asyncAction);

    /// <summary>
    /// Executes the task-returning delegate on the UI thread and awaits the resulting value.
    /// </summary>
    Task<T> InvokeAsync<T>(Func<Task<T>> asyncAction);
}
