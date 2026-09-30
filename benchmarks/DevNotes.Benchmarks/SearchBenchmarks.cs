using BenchmarkDotNet.Attributes;
using DevNotes.Application.Search;

namespace DevNotes.Benchmarks;

/// <summary>
/// Queries exactly as the UI issues them (sanitized FTS5 expression, BM25 ranking, snippets).
/// Target of the specification: perceived search latency below 50 ms with thousands of notes.
/// </summary>
[MemoryDiagnoser]
public class SearchBenchmarks
{
    /// <summary>Rows the note list asks for when there is no search (NoteListViewModel.ResultLimit).</summary>
    private const int ListLimit = 2_000;

    /// <summary>Rows the note list asks for while searching (NoteListViewModel.SearchResultLimit).</summary>
    private const int ListSearchLimit = 500;

    /// <summary>Rows the quick-open overlay asks for (QuickOpenViewModel.MaxResults).</summary>
    private const int QuickOpenLimit = 30;

    private SyntheticVault _vault = null!;
    private BenchmarkIndex _index = null!;
    private NoteQueryService _queries = null!;

    [Params(5_000)]
    public int Notes { get; set; }

    [GlobalSetup]
    public async Task BuildIndexAsync()
    {
        _vault = SyntheticVault.Create(Notes);
        _index = await BenchmarkIndex.OpenAsync(_vault);
        await _index.Indexer.SynchronizeAsync(progress: null, CancellationToken.None);
        _queries = new NoteQueryService(_index.Index);
    }

    [GlobalCleanup]
    public async Task CleanupAsync()
    {
        await _index.DisposeAsync();
        _vault.Dispose();
    }

    [Benchmark(Description = "List, most recent first")]
    public Task<NoteQueryResult> ListRecent() =>
        _queries.QueryAsync(searchText: null, NoteSortOrder.UpdatedDescending, ListLimit, CancellationToken.None);

    // "de" is what search-as-you-type sends after two keystrokes: a prefix that matches every note.
    [Benchmark(Description = "Quick open: 2 letters typed, every note matches")]
    public Task<NoteQueryResult> QuickOpenBroadPrefix() =>
        _queries.QueryAsync("de", NoteSortOrder.Relevance, QuickOpenLimit, CancellationToken.None);

    [Benchmark(Description = "Quick open: two terms")]
    public Task<NoteQueryResult> QuickOpenTwoTerms() =>
        _queries.QueryAsync("inventory transac", NoteSortOrder.Relevance, QuickOpenLimit, CancellationToken.None);

    [Benchmark(Description = "Note list: 2 letters typed, most recent first")]
    public Task<NoteQueryResult> ListBroadPrefixByRecent() =>
        _queries.QueryAsync("de", NoteSortOrder.UpdatedDescending, ListSearchLimit, CancellationToken.None);

    [Benchmark(Description = "Note list: common word, by title")]
    public Task<NoteQueryResult> ListCommonWordByTitle() =>
        _queries.QueryAsync("deadlock", NoteSortOrder.TitleAscending, ListSearchLimit, CancellationToken.None);

    [Benchmark(Description = "Note list: common word, by relevance")]
    public Task<NoteQueryResult> ListCommonWordByRelevance() =>
        _queries.QueryAsync("deadlock", NoteSortOrder.Relevance, ListSearchLimit, CancellationToken.None);

    // The word index has no hit for a fragment of an identifier; the trigram index finds it.
    [Benchmark(Description = "Quick open: substring of an identifier (trigram)")]
    public Task<NoteQueryResult> SubstringInIdentifier() =>
        _queries.QueryAsync("InventoryAsy", NoteSortOrder.Relevance, QuickOpenLimit, CancellationToken.None);

    [Benchmark(Description = "No results")]
    public Task<NoteQueryResult> NoResults() =>
        _queries.QueryAsync("zzzzunknownterm", NoteSortOrder.Relevance, QuickOpenLimit, CancellationToken.None);
}
