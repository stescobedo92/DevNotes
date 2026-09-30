namespace DevNotes.Application.Indexing;

public sealed class IndexingOptions
{
    public const string SectionName = "Indexing";

    /// <summary>Quiet period after the last file-system event before the changes are indexed.</summary>
    public int WatcherDebounceMilliseconds { get; set; } = 300;

    /// <summary>Notes written to the index per transaction during a scan.</summary>
    public int BatchSize { get; set; } = 200;

    /// <summary>Files read and parsed concurrently during a scan.</summary>
    public int MaxDegreeOfParallelism { get; set; } = 4;

    /// <summary>Above this many buffered changes a full incremental scan is cheaper than per-file updates.</summary>
    public int MaxBufferedChanges { get; set; } = 500;

    public TimeSpan WatcherDebounce => TimeSpan.FromMilliseconds(Math.Clamp(WatcherDebounceMilliseconds, 0, 60_000));

    public int EffectiveBatchSize => Math.Clamp(BatchSize, 1, 5_000);

    public int EffectiveParallelism => Math.Clamp(MaxDegreeOfParallelism, 1, 32);

    public int EffectiveMaxBufferedChanges => Math.Clamp(MaxBufferedChanges, 1, 100_000);
}
