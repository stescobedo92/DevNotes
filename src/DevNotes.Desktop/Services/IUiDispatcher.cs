using Avalonia.Threading;

namespace DevNotes.Desktop.Services;

/// <summary>
/// Marshals work to the UI thread. View models never touch <see cref="Dispatcher"/> directly so
/// they can be tested without a running UI.
/// </summary>
public interface IUiDispatcher
{
    bool CheckAccess();

    /// <summary>Queues <paramref name="action"/> on the UI thread (runs inline when already there).</summary>
    void Post(Action action);

    /// <summary>Runs asynchronous work on the UI thread and completes when it has finished.</summary>
    Task InvokeAsync(Func<Task> action);
}

public sealed class AvaloniaUiDispatcher : IUiDispatcher
{
    public bool CheckAccess() => Dispatcher.UIThread.CheckAccess();

    public void Post(Action action)
    {
        ArgumentNullException.ThrowIfNull(action);
        if (Dispatcher.UIThread.CheckAccess())
        {
            action();
        }
        else
        {
            Dispatcher.UIThread.Post(action);
        }
    }

    public Task InvokeAsync(Func<Task> action)
    {
        ArgumentNullException.ThrowIfNull(action);
        return Dispatcher.UIThread.CheckAccess() ? action() : Dispatcher.UIThread.InvokeAsync(action);
    }
}
