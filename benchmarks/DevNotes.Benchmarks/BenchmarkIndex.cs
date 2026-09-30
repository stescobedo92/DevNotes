using DevNotes.Application.Indexing;
using DevNotes.Infrastructure.Indexing;
using DevNotes.Infrastructure.Storage;
using Microsoft.Extensions.Logging.Abstractions;

namespace DevNotes.Benchmarks;

/// <summary>The production file store, SQLite index and indexer wired over a synthetic vault.</summary>
public sealed class BenchmarkIndex : IAsyncDisposable
{
    private readonly string _databaseFolder;

    private BenchmarkIndex(string databaseFolder, SqliteNoteIndex index, VaultIndexer indexer)
    {
        _databaseFolder = databaseFolder;
        Index = index;
        Indexer = indexer;
    }

    public SqliteNoteIndex Index { get; }

    public VaultIndexer Indexer { get; }

    /// <summary>Opens a brand-new, empty index database for the vault.</summary>
    public static async Task<BenchmarkIndex> OpenAsync(SyntheticVault vault)
    {
        ArgumentNullException.ThrowIfNull(vault);

        var databaseFolder = Directory.CreateTempSubdirectory("devnotes-bench-index-").FullName;
        var index = SqliteNoteIndex.ForFile(Path.Combine(databaseFolder, "index.db"), NullLogger<SqliteNoteIndex>.Instance);
        await index.InitializeAsync(CancellationToken.None);

        var files = new VaultFileStore(vault.Root, TimeProvider.System);
        var indexer = new VaultIndexer(files, index, new IndexingOptions(), NullLogger<VaultIndexer>.Instance);
        return new BenchmarkIndex(databaseFolder, index, indexer);
    }

    public async ValueTask DisposeAsync()
    {
        await Index.DisposeAsync();
        if (Directory.Exists(_databaseFolder))
        {
            Directory.Delete(_databaseFolder, recursive: true);
        }
    }
}
