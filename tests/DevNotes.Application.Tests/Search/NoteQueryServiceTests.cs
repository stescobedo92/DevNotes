using DevNotes.Application.Abstractions;
using DevNotes.Application.Search;
using DevNotes.Domain.Notes;
using NSubstitute;

namespace DevNotes.Application.Tests.Search;

public sealed class NoteQueryServiceTests
{
    private readonly INoteIndex _index = Substitute.For<INoteIndex>();
    private readonly NoteQueryService _service;

    public NoteQueryServiceTests()
    {
        _service = new NoteQueryService(_index);
        _index.ListAsync(Arg.Any<NoteListQuery>(), Arg.Any<CancellationToken>()).Returns(call => Summaries(call.Arg<NoteListQuery>().Limit));
        _index.SearchAsync(Arg.Any<SearchQuery>(), Arg.Any<CancellationToken>()).Returns(call => Hits(call.Arg<SearchQuery>().Limit));
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Theory]
    [InlineData(null)]
    [InlineData("   ")]
    [InlineData("***")]
    public async Task QueryAsync_NoUsableSearchText_ListsNotes(string? text)
    {
        var result = await _service.QueryAsync(text, NoteFilter.Empty, NoteSortOrder.TitleAscending, 5, Ct);

        result.IsSearch.Should().BeFalse();
        result.Entries.Should().HaveCount(5);
        await _index.Received(1).ListAsync(new NoteListQuery(NoteSortOrder.TitleAscending, 6), Ct);
        await _index.DidNotReceive().SearchAsync(Arg.Any<SearchQuery>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task QueryAsync_ListingByRelevance_FallsBackToMostRecent()
    {
        await _service.QueryAsync(null, NoteFilter.Empty, NoteSortOrder.Relevance, 5, Ct);

        await _index.Received(1).ListAsync(new NoteListQuery(NoteSortOrder.UpdatedDescending, 6), Ct);
    }

    [Fact]
    public async Task QueryAsync_Listing_UsesTheExcerptAsAPlainSingleLineSnippet()
    {
        var result = await _service.QueryAsync(null, NoteFilter.Empty, NoteSortOrder.UpdatedDescending, 2, Ct);

        result.Entries[0].Snippet.Should().Equal(new SnippetSegment("line one line two", IsMatch: false));
        result.Entries[0].Title.Should().Equal(new SnippetSegment("Title 1", IsMatch: false));
    }

    [Fact]
    public async Task QueryAsync_WithSearchText_SendsSanitizedQueryToTheIndex()
    {
        var result = await _service.QueryAsync("dead\"lock OR x", NoteFilter.Empty, NoteSortOrder.Relevance, 10, Ct);

        result.IsSearch.Should().BeTrue();
        result.Entries.Should().HaveCount(10);
        result.Entries[0].Snippet.Should().ContainSingle().Which.IsMatch.Should().BeTrue();
        result.Entries[0].Title.Should().HaveCount(2, "the highlighted title of the hit is passed through");
        await _index.Received(1).SearchAsync(
            Arg.Is<SearchQuery>(query =>
                query.Query.Match == "\"deadlock\" \"OR\" \"x\"*"
                && query.Query.TrigramMatch == "\"deadlock\""
                && query.Sort == NoteSortOrder.Relevance
                && query.Limit == 11
                && query.Filter.IsEmpty),
            Ct);
    }

    [Fact]
    public async Task QueryAsync_TypedFilters_AreCombinedWithTheGivenFilter()
    {
        var sidebar = new NoteFilter { Tags = [Tag.Create("perf")], Types = [NoteType.Bug, NoteType.Adr] };

        var result = await _service.QueryAsync("deadlock project:cslinq type:bug", sidebar, NoteSortOrder.Relevance, 10, Ct);

        result.IsSearch.Should().BeTrue();
        await _index.Received(1).SearchAsync(
            Arg.Is<SearchQuery>(query =>
                query.Query.Match == "\"deadlock\"*"
                && query.Filter.Projects.SequenceEqual(new[] { "cslinq" })
                && query.Filter.Tags.Select(tag => tag.Value).SequenceEqual(new[] { "perf" })
                && query.Filter.Types.SequenceEqual(new[] { NoteType.Bug })),
            Ct);
    }

    [Fact]
    public async Task QueryAsync_FilterWithoutText_SearchesWithAnEmptyQueryAndShowsExcerpts()
    {
        _index.SearchAsync(Arg.Any<SearchQuery>(), Arg.Any<CancellationToken>()).Returns(call => PlainHits(call.Arg<SearchQuery>().Limit));

        var result = await _service.QueryAsync("  ", new NoteFilter { Projects = ["cslinq"] }, NoteSortOrder.TitleAscending, 3, Ct);

        result.IsSearch.Should().BeTrue("a filter narrows the list, so an empty result means no matches rather than no notes");
        result.Entries.Should().HaveCount(3);
        result.Entries[0].Snippet.Should().Equal(new SnippetSegment("line one line two", IsMatch: false));
        await _index.Received(1).SearchAsync(
            Arg.Is<SearchQuery>(query => query.Query.IsEmpty && query.Filter.Projects.SequenceEqual(new[] { "cslinq" }) && query.Sort == NoteSortOrder.TitleAscending),
            Ct);
        await _index.DidNotReceive().ListAsync(Arg.Any<NoteListQuery>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task QueryAsync_OnlyExclusions_SearchesWithTheExclusion()
    {
        await _service.QueryAsync("-draft", NoteFilter.Empty, NoteSortOrder.UpdatedDescending, 3, Ct);

        await _index.Received(1).SearchAsync(Arg.Is<SearchQuery>(query => query.Query.IsEmpty && query.Query.Exclude == "\"draft\""), Ct);
    }

    [Fact]
    public async Task QueryAsync_UnsatisfiableFilter_ReturnsNothingWithoutAskingTheIndex()
    {
        var result = await _service.QueryAsync("project:a", new NoteFilter { Projects = ["b"] }, NoteSortOrder.Relevance, 10, Ct);

        result.Entries.Should().BeEmpty();
        result.IsSearch.Should().BeTrue();
        result.IsTruncated.Should().BeFalse();
        await _index.DidNotReceive().SearchAsync(Arg.Any<SearchQuery>(), Arg.Any<CancellationToken>());
        await _index.DidNotReceive().ListAsync(Arg.Any<NoteListQuery>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task QueryAsync_MoreRowsThanLimit_FlagsTruncation()
    {
        var result = await _service.QueryAsync(null, NoteFilter.Empty, NoteSortOrder.UpdatedDescending, 3, Ct);

        result.IsTruncated.Should().BeTrue("the index returned limit + 1 rows");
        result.Entries.Should().HaveCount(3);
    }

    [Fact]
    public async Task QueryAsync_FewerRowsThanLimit_IsNotTruncated()
    {
        _index.ListAsync(Arg.Any<NoteListQuery>(), Arg.Any<CancellationToken>()).Returns(Summaries(2));

        var result = await _service.QueryAsync(null, NoteFilter.Empty, NoteSortOrder.UpdatedDescending, 3, Ct);

        result.IsTruncated.Should().BeFalse();
        result.Entries.Should().HaveCount(2);
    }

    [Theory]
    [InlineData(0, 2)]
    [InlineData(-10, 2)]
    [InlineData(int.MaxValue, NoteQueryService.MaxLimit + 1)]
    public async Task QueryAsync_Limit_IsClamped(int requested, int expectedFetch)
    {
        await _service.QueryAsync(null, NoteFilter.Empty, NoteSortOrder.UpdatedDescending, requested, Ct);

        await _index.Received(1).ListAsync(Arg.Is<NoteListQuery>(query => query.Limit == expectedFetch), Ct);
    }

    [Fact]
    public async Task CountAsync_DelegatesToTheIndex()
    {
        _index.CountAsync(Ct).Returns(42);

        (await _service.CountAsync(Ct)).Should().Be(42);
    }

    [Fact]
    public async Task GetFacetsAsync_DelegatesToTheIndex()
    {
        var facets = new NoteFacets([new FacetCount("cslinq", 2)], [], []);
        _index.GetFacetsAsync(Ct).Returns(facets);

        (await _service.GetFacetsAsync(Ct)).Should().BeSameAs(facets);
    }

    private static IReadOnlyList<NoteSummary> Summaries(int count) =>
        [.. Enumerable.Range(1, count).Select(Summary)];

    private static IReadOnlyList<SearchHit> Hits(int count) =>
        [.. Enumerable.Range(1, count).Select(i => new SearchHit(
            Summary(i),
            [new SnippetSegment("Title ", IsMatch: false), new SnippetSegment($"{i}", IsMatch: true)],
            [new SnippetSegment("hit", IsMatch: true)],
            -i))];

    /// <summary>What the index returns for a search without text: no fragment, the excerpt on the summary.</summary>
    private static IReadOnlyList<SearchHit> PlainHits(int count) =>
        [.. Enumerable.Range(1, count).Select(i => new SearchHit(Summary(i), [], [], 0))];

    private static NoteSummary Summary(int i) => new(
        NoteId.Parse($"ID-{i}"),
        NotePath.Create($"n{i}.md"),
        $"Title {i}",
        Project: null,
        NoteType.Note,
        Tags: [],
        Created: null,
        Updated: null,
        DateTimeOffset.UnixEpoch,
        "line one\nline two");
}
