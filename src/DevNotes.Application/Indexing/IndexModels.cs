using DevNotes.Domain.Notes;

namespace DevNotes.Application.Indexing;

public readonly record struct IndexProgress(int Processed, int Total);

/// <summary>Outcome of one scan of the vault.</summary>
public sealed record IndexRunSummary(
    int Scanned,
    int Indexed,
    int Unchanged,
    int Removed,
    int Failed,
    TimeSpan Elapsed);

public enum IndexState
{
    Idle,
    Indexing,
    Failed,
}

/// <summary>Snapshot of the background indexer, shown in the status bar.</summary>
public sealed record IndexStatus(IndexState State, IndexProgress Progress, string? Error)
{
    public static IndexStatus Idle { get; } = new(IndexState.Idle, default, null);
}

public enum NotesChangeSource
{
    /// <summary>An operation performed through the app (save, create, rename, move, delete, restore).</summary>
    Local,

    /// <summary>A change made outside the app and reported by the file watcher.</summary>
    External,

    /// <summary>A scan finished; any note may have changed.</summary>
    Scan,
}

public sealed class NotesChangedEventArgs(NotesChangeSource source, IReadOnlyCollection<NotePath> paths) : EventArgs
{
    /// <summary>What caused the change.</summary>
    public NotesChangeSource Source { get; } = source;

    /// <summary>Affected notes; ignored for <see cref="NotesChangeSource.Scan"/>, where any note may have changed.</summary>
    public IReadOnlyCollection<NotePath> Paths { get; } = paths ?? throw new ArgumentNullException(nameof(paths));

    public bool AffectsEverything => Source == NotesChangeSource.Scan;

    public bool Affects(NotePath path) => AffectsEverything || Paths.Contains(path);
}
