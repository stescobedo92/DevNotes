using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Engines;
using DevNotes.Application.Indexing;

namespace DevNotes.Benchmarks;

/// <summary>
/// Indexing a vault from the Markdown files: the first scan (everything is read, parsed and written
/// to SQLite/FTS5) and the scan of every later start (nothing changed, so nothing is read).
/// Target of the specification: 5,000 notes indexed in seconds, in the background.
/// </summary>
[MemoryDiagnoser]
[SimpleJob(RunStrategy.Monitoring, launchCount: 1, warmupCount: 1, iterationCount: 5)]
public class IndexingBenchmarks
{
    private SyntheticVault _vault = null!;
    private BenchmarkIndex _index = null!;

    [Params(1_000, 5_000)]
    public int Notes { get; set; }

    [GlobalSetup]
    public void CreateVault() => _vault = SyntheticVault.Create(Notes);

    [GlobalCleanup]
    public void DeleteVault() => _vault.Dispose();

    [IterationSetup(Target = nameof(InitialScan))]
    public void OpenEmptyIndex() => _index = BenchmarkIndex.OpenAsync(_vault).GetAwaiter().GetResult();

    [IterationSetup(Target = nameof(RescanWithoutChanges))]
    public void OpenPopulatedIndex()
    {
        _index = BenchmarkIndex.OpenAsync(_vault).GetAwaiter().GetResult();
        _index.Indexer.SynchronizeAsync(progress: null, CancellationToken.None).GetAwaiter().GetResult();
    }

    [IterationCleanup]
    public void CloseIndex() => _index.DisposeAsync().AsTask().GetAwaiter().GetResult();

    [Benchmark(Description = "First scan (index everything)")]
    public Task<IndexRunSummary> InitialScan() => _index.Indexer.SynchronizeAsync(progress: null, CancellationToken.None);

    [Benchmark(Description = "Rescan, nothing changed")]
    public Task<IndexRunSummary> RescanWithoutChanges() => _index.Indexer.SynchronizeAsync(progress: null, CancellationToken.None);
}
