using DevNotes.Domain.Notes;

namespace DevNotes.Application.Indexing;

/// <summary>
/// Notifications of one open vault. Events can be raised from background threads; UI
/// subscribers are responsible for marshalling to their own thread.
/// </summary>
public sealed class VaultEventHub
{
    private IndexStatus _status = IndexStatus.Idle;

    public event EventHandler<NotesChangedEventArgs>? NotesChanged;

    public event EventHandler<IndexStatus>? IndexStatusChanged;

    public IndexStatus Status => Volatile.Read(ref _status);

    public void PublishNotesChanged(NotesChangeSource source, params IReadOnlyCollection<NotePath> paths) =>
        NotesChanged?.Invoke(this, new NotesChangedEventArgs(source, paths));

    public void PublishStatus(IndexStatus status)
    {
        ArgumentNullException.ThrowIfNull(status);
        Volatile.Write(ref _status, status);
        IndexStatusChanged?.Invoke(this, status);
    }
}
