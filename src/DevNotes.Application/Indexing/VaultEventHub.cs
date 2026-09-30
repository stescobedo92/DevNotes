using System.Diagnostics.CodeAnalysis;
using DevNotes.Domain.Notes;

namespace DevNotes.Application.Indexing;

/// <summary>
/// Notifications of one open vault. Events can be raised from background threads; UI
/// subscribers are responsible for marshalling to their own thread.
/// <para>
/// Publishers are a save that already reached the disk and the indexing worker: neither may
/// fail because of a subscriber. Every subscriber is notified even when another one throws.
/// </para>
/// </summary>
/// <param name="onSubscriberFailure">
/// Receives the exception thrown by a subscriber. Without it the failures are rethrown after all
/// subscribers have been notified, so they are never silent.
/// </param>
public sealed class VaultEventHub(Action<Exception>? onSubscriberFailure = null)
{
    private IndexStatus _status = IndexStatus.Idle;

    public event EventHandler<NotesChangedEventArgs>? NotesChanged;

    public event EventHandler<IndexStatus>? IndexStatusChanged;

    public IndexStatus Status => Volatile.Read(ref _status);

    public void PublishNotesChanged(NotesChangeSource source, params IReadOnlyCollection<NotePath> paths) =>
        Raise(NotesChanged, new NotesChangedEventArgs(source, paths));

    public void PublishStatus(IndexStatus status)
    {
        ArgumentNullException.ThrowIfNull(status);
        Volatile.Write(ref _status, status);
        Raise(IndexStatusChanged, status);
    }

    [SuppressMessage(
        "Design",
        "CA1031:Do not catch general exception types",
        Justification = "A subscriber failure is reported (or rethrown once everyone was notified); it must not stop the other subscribers.")]
    private void Raise<TArgs>(EventHandler<TArgs>? handlers, TArgs args)
    {
        if (handlers is null)
        {
            return;
        }

        List<Exception>? failures = null;
        foreach (var handler in Delegate.EnumerateInvocationList(handlers))
        {
            try
            {
                handler(this, args);
            }
            catch (Exception exception)
            {
                if (onSubscriberFailure is null)
                {
                    (failures ??= []).Add(exception);
                }
                else
                {
                    onSubscriberFailure(exception);
                }
            }
        }

        if (failures is not null)
        {
            throw new AggregateException("One or more subscribers of a vault event failed.", failures);
        }
    }
}
