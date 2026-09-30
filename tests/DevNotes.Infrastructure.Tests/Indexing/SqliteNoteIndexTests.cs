using DevNotes.Application.Abstractions;
using DevNotes.Application.Search;
using DevNotes.Domain.Notes;
using DevNotes.Infrastructure.Indexing;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;

namespace DevNotes.Infrastructure.Tests.Indexing;

/// <summary>Integration tests against real SQLite, both in memory and on disk.</summary>
public abstract class SqliteNoteIndexTests : IAsyncLifetime
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    protected SqliteNoteIndex Index { get; private set; } = null!;

    protected abstract SqliteNoteIndex CreateIndex();

    public async ValueTask InitializeAsync()
    {
        Index = CreateIndex();
        await Index.InitializeAsync(Ct);
    }

    public virtual async ValueTask DisposeAsync()
    {
        await Index.DisposeAsync();
        GC.SuppressFinalize(this);
    }

    [Fact]
    public async Task InitializeAsync_IsIdempotent_AndStartsEmpty()
    {
        await Index.InitializeAsync(Ct);

        (await Index.CountAsync(Ct)).Should().Be(0);
        (await Index.GetFileStatesAsync(Ct)).Should().BeEmpty();
        (await Index.ListAsync(new NoteListQuery(NoteSortOrder.UpdatedDescending, 10), Ct)).Should().BeEmpty();
        (await Search("anything")).Should().BeEmpty();
    }

    [Fact]
    public async Task UpsertAsync_StoresNoteAndFileState()
    {
        var note = TestNotes.Create(
            "bugs/deadlock.md", "01J8ZQ4M9T3N7K5W2X6Y8V0B1C", "Deadlock en inventario", "El proceso se bloqueaba.",
            tags: ["sql-server", "deadlock"], updated: new DateOnly(2026, 9, 14), created: new DateOnly(2026, 9, 12),
            project: "azure-microservices", type: NoteType.Bug);

        await Index.UpsertAsync([note], Ct);

        var states = await Index.GetFileStatesAsync(Ct);
        states.Should().ContainSingle();
        states[note.Path].Should().Be(new IndexedFileState(note.Id, note.Hash, note.FileSize, note.FileLastWriteUtc));

        var summary = (await Index.ListAsync(new NoteListQuery(NoteSortOrder.UpdatedDescending, 10), Ct)).Should().ContainSingle().Subject;
        summary.Should().BeEquivalentTo(new NoteSummary(
            note.Id, note.Path, "Deadlock en inventario", "azure-microservices", NoteType.Bug, ["sql-server", "deadlock"],
            new DateOnly(2026, 9, 12), new DateOnly(2026, 9, 14), note.FileLastWriteUtc, "El proceso se bloqueaba."));
        (await Index.FindPathByIdAsync(note.Id, Ct)).Should().Be(note.Path);
        (await Index.FindPathByIdAsync(NoteId.Parse("missing"), Ct)).Should().BeNull();
    }

    [Fact]
    public async Task UpsertAsync_SamePathAndId_UpdatesInPlace()
    {
        await Index.UpsertAsync([TestNotes.Create("a.md", "A", "First", "alpha content")], Ct);

        await Index.UpsertAsync([TestNotes.Create("a.md", "A", "Second", "beta content", tags: ["x"])], Ct);

        (await Index.CountAsync(Ct)).Should().Be(1);
        (await Search("alpha")).Should().BeEmpty("the old text must leave the full-text index");
        (await Search("beta")).Should().ContainSingle().Which.Note.Title.Should().Be("Second");
    }

    [Fact]
    public async Task UpsertAsync_SameIdNewPath_MovesTheRow()
    {
        await Index.UpsertAsync([TestNotes.Create("old/name.md", "STABLE", "Note", "body")], Ct);

        await Index.UpsertAsync([TestNotes.Create("new/name.md", "STABLE", "Note", "body")], Ct);

        var states = await Index.GetFileStatesAsync(Ct);
        states.Keys.Select(path => path.Value).Should().Equal("new/name.md");
    }

    [Fact]
    public async Task UpsertAsync_SamePathNewId_ReplacesIdentityAndCascadesToTagsAndLinks()
    {
        var links = new NoteLinks(["a3f9c21"], ["MS-482"], ["other-note"]);
        await Index.UpsertAsync([TestNotes.Create("a.md", "path-abc", "Note", "body", tags: ["one"], links: links)], Ct);

        await Index.UpsertAsync([TestNotes.Create("a.md", "REAL-ID", "Note", "body", tags: ["one", "two"], links: links)], Ct);

        (await Index.CountAsync(Ct)).Should().Be(1);
        (await Index.FindPathByIdAsync(NoteId.Parse("path-abc"), Ct)).Should().BeNull();
        (await Index.FindPathByIdAsync(NoteId.Parse("REAL-ID"), Ct))!.Value.Value.Should().Be("a.md");
    }

    [Fact]
    public async Task UpsertAsync_MovedOntoPathOfAnotherNote_ReplacesTheOccupant()
    {
        await Index.UpsertAsync(
            [TestNotes.Create("target.md", "OCCUPANT", "Occupant", "old"), TestNotes.Create("source.md", "MOVER", "Mover", "new")], Ct);

        await Index.UpsertAsync([TestNotes.Create("target.md", "MOVER", "Mover", "new")], Ct);

        var states = await Index.GetFileStatesAsync(Ct);
        states.Should().ContainSingle();
        states[NotePath.Create("target.md")].Id.Value.Should().Be("MOVER");
        (await Search("old")).Should().BeEmpty();
    }

    [Fact]
    public async Task UpsertAsync_LargeBatch_IsAtomicAndSearchable()
    {
        var notes = Enumerable.Range(1, 500)
            .Select(i => TestNotes.Create($"n/{i:D4}.md", $"ID-{i}", $"Note {i}", $"content number{i} shared", minutesAfterBase: i))
            .ToList();

        await Index.UpsertAsync(notes, Ct);

        (await Index.CountAsync(Ct)).Should().Be(500);
        (await Search("number250")).Should().ContainSingle().Which.Note.Path.Value.Should().Be("n/0250.md");
        (await Index.SearchAsync(new SearchQuery(FtsQueryBuilder.Build("shared"), NoteSortOrder.Relevance, 40), Ct)).Should().HaveCount(40);
    }

    [Fact]
    public async Task UpsertAsync_Cancelled_RollsBackTheWholeBatch()
    {
        using var cts = new CancellationTokenSource();
        var notes = Enumerable.Range(1, 50).Select(i => TestNotes.Create($"{i}.md", $"ID-{i}", $"Note {i}")).ToList();
        await cts.CancelAsync();

        var act = () => Index.UpsertAsync(notes, cts.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
        (await Index.CountAsync(Ct)).Should().Be(0);
    }

    [Fact]
    public async Task TouchAsync_UpdatesFileStateOnly()
    {
        var note = TestNotes.Create("a.md", "A", "Title", "searchable body");
        await Index.UpsertAsync([note], Ct);
        var touchedAt = note.FileLastWriteUtc.AddHours(5);

        await Index.TouchAsync([new NoteFileInfo(note.Path, 999, touchedAt)], Ct);

        var state = (await Index.GetFileStatesAsync(Ct))[note.Path];
        state.Should().Be(new IndexedFileState(note.Id, note.Hash, 999, touchedAt));
        (await Search("searchable")).Should().ContainSingle();
    }

    [Fact]
    public async Task RemoveAsync_DeletesNoteFromEveryStructure()
    {
        await Index.UpsertAsync(
            [TestNotes.Create("a.md", "A", "Alpha", "first body", tags: ["t"]), TestNotes.Create("b.md", "B", "Beta", "second body")], Ct);

        await Index.RemoveAsync([NotePath.Create("a.md"), NotePath.Create("not-indexed.md")], Ct);

        (await Index.CountAsync(Ct)).Should().Be(1);
        (await Search("first")).Should().BeEmpty();
        (await Search("second")).Should().ContainSingle();
    }

    [Fact]
    public async Task ClearAsync_EmptiesTheIndexAndKeepsItUsable()
    {
        await Index.UpsertAsync([TestNotes.Create("a.md", "A", "Alpha", "body")], Ct);

        await Index.ClearAsync(Ct);

        (await Index.CountAsync(Ct)).Should().Be(0);
        (await Search("body")).Should().BeEmpty();
        await Index.UpsertAsync([TestNotes.Create("a.md", "A", "Alpha", "body")], Ct);
        (await Search("body")).Should().ContainSingle();
    }

    [Fact]
    public async Task EmptyCollections_AreNoOps()
    {
        await Index.UpsertAsync([], Ct);
        await Index.TouchAsync([], Ct);
        await Index.RemoveAsync([], Ct);

        (await Index.CountAsync(Ct)).Should().Be(0);
    }

    [Fact]
    public async Task ListAsync_SortsByRecencyOrTitle_AndHonoursLimit()
    {
        await Index.UpsertAsync(
            [
                TestNotes.Create("1.md", "N1", "banana", updated: new DateOnly(2026, 9, 1)),
                TestNotes.Create("2.md", "N2", "Ábaco", updated: new DateOnly(2026, 9, 3)),
                TestNotes.Create("3.md", "N3", "cherry", updated: null, minutesAfterBase: 60 * 24 * 1), // falls back to file date 2026-09-02
                TestNotes.Create("4.md", "N4", "apple", updated: new DateOnly(2026, 9, 3), minutesAfterBase: 5),
            ],
            Ct);

        var recent = await Index.ListAsync(new NoteListQuery(NoteSortOrder.UpdatedDescending, 10), Ct);
        var byTitle = await Index.ListAsync(new NoteListQuery(NoteSortOrder.TitleAscending, 10), Ct);
        var limited = await Index.ListAsync(new NoteListQuery(NoteSortOrder.TitleAscending, 2), Ct);

        recent.Select(note => note.Title).Should().Equal("apple", "Ábaco", "cherry", "banana");
        byTitle.Select(note => note.Title).Should().Equal("Ábaco", "apple", "banana", "cherry");
        limited.Select(note => note.Title).Should().Equal("Ábaco", "apple");
    }

    [Theory]
    [InlineData("deadlock", "bug.md")]
    [InlineData("DEADLOCK", "bug.md")]
    [InlineData("dead", "bug.md")] // prefix of the term being typed
    [InlineData("actualizacion", "bug.md")] // diacritics are folded
    [InlineData("actualización inventario", "bug.md")]
    [InlineData("sql-server", "bug.md")] // tag
    [InlineData("Inventory", "code.md")] // substring of an identifier: only the trigram index finds it
    [InlineData("nventoryAsy", "code.md")]
    [InlineData("runbook", "ops.md")] // title
    public async Task SearchAsync_FindsNotesByWordPrefixDiacriticsTagsAndSubstring(string text, string expectedPath)
    {
        await SeedSearchCorpusAsync();

        var hits = await Search(text);

        hits.Select(hit => hit.Note.Path.Value).Should().Contain(expectedPath);
    }

    [Theory]
    [InlineData("nonexistentterm")]
    [InlineData("deadlock nonexistentterm")] // every term must match
    [InlineData("zzz")]
    public async Task SearchAsync_NoMatch_ReturnsEmpty(string text)
    {
        await SeedSearchCorpusAsync();

        (await Search(text)).Should().BeEmpty();
    }

    [Fact]
    public async Task SearchAsync_RanksTitleMatchesAboveBodyMatches()
    {
        await Index.UpsertAsync(
            [
                TestNotes.Create("body.md", "B", "Unrelated heading", "a long text that mentions deadlock once among many other words " + new string('x', 40)),
                TestNotes.Create("title.md", "T", "Deadlock analysis", "nothing special here"),
                TestNotes.Create("tag.md", "G", "Tagged note", "nothing special either", tags: ["deadlock"]),
            ],
            Ct);

        var hits = await Search("deadlock");

        hits.Select(hit => hit.Note.Path.Value).Should().Equal("title.md", "tag.md", "body.md");
        hits.Select(hit => hit.Score).Should().BeInAscendingOrder("BM25 scores are lower for better matches");
    }

    [Fact]
    public async Task SearchAsync_ReturnsHighlightedSnippetAndTitle()
    {
        await Index.UpsertAsync(
            [TestNotes.Create("a.md", "A", "Deadlock en inventario", "Primera línea.\nEl deadlock ocurría al actualizar el inventario cada noche.")], Ct);

        var hit = (await Search("deadlock")).Should().ContainSingle().Subject;

        hit.Title.Should().Equal(new SnippetSegment("Deadlock", true), new SnippetSegment(" en inventario", false));
        hit.Snippet.Should().Contain(new SnippetSegment("deadlock", true));
        string.Concat(hit.Snippet.Select(segment => segment.Text)).Should().Contain("El deadlock ocurría").And.NotContain("\n");
    }

    [Fact]
    public async Task SearchAsync_SortOrders_AreApplied()
    {
        await Index.UpsertAsync(
            [
                TestNotes.Create("1.md", "N1", "zeta common", updated: new DateOnly(2026, 9, 1)),
                TestNotes.Create("2.md", "N2", "alpha", "common common common", updated: new DateOnly(2026, 9, 5)),
                TestNotes.Create("3.md", "N3", "mid common", updated: new DateOnly(2026, 9, 3)),
            ],
            Ct);
        var query = FtsQueryBuilder.Build("common");

        var recent = await Index.SearchAsync(new SearchQuery(query, NoteSortOrder.UpdatedDescending, 10), Ct);
        var byTitle = await Index.SearchAsync(new SearchQuery(query, NoteSortOrder.TitleAscending, 10), Ct);
        var limited = await Index.SearchAsync(new SearchQuery(query, NoteSortOrder.TitleAscending, 2), Ct);

        recent.Select(hit => hit.Note.Path.Value).Should().Equal("2.md", "3.md", "1.md");
        byTitle.Select(hit => hit.Note.Path.Value).Should().Equal("2.md", "3.md", "1.md");
        limited.Should().HaveCount(2);
    }

    [Fact]
    public async Task SearchAsync_MoreMatchesThanTheLimit_ReturnsTheFirstOnesOfEachOrder()
    {
        // Forty notes match; the limit must keep the first three of the requested order, not any three.
        var notes = Enumerable.Range(0, 40).Select(i => TestNotes.Create(
            $"n{i:D2}.md",
            $"ID{i:D2}",
            i == 7 ? "Common heading 07" : $"Heading {i:D2}",
            "some common text",
            updated: new DateOnly(2026, 8, 1).AddDays(i)));
        await Index.UpsertAsync([.. notes], Ct);
        var query = FtsQueryBuilder.Build("common");

        var recent = await Index.SearchAsync(new SearchQuery(query, NoteSortOrder.UpdatedDescending, 3), Ct);
        var byTitle = await Index.SearchAsync(new SearchQuery(query, NoteSortOrder.TitleAscending, 3), Ct);
        var relevant = await Index.SearchAsync(new SearchQuery(query, NoteSortOrder.Relevance, 3), Ct);

        recent.Select(hit => hit.Note.Path.Value).Should().Equal("n39.md", "n38.md", "n37.md");
        byTitle.Select(hit => hit.Note.Path.Value).Should().Equal("n07.md", "n00.md", "n01.md");
        relevant.Should().HaveCount(3);
        relevant[0].Note.Path.Value.Should().Be("n07.md", "a match in the title outranks matches in the body");
        relevant.Skip(1).Select(hit => hit.Note.Updated).Should().BeInDescendingOrder("equally relevant notes are listed most recent first");
        recent.Concat(byTitle).Concat(relevant).Should().OnlyContain(
            hit => hit.Snippet.Any(segment => segment.IsMatch) || hit.Title.Any(segment => segment.IsMatch),
            "every returned note carries its highlighted fragment");
    }

    [Fact]
    public async Task SearchAsync_SubstringMatchesComeAfterWordMatches_WithoutDuplicates()
    {
        await Index.UpsertAsync(
            [
                TestNotes.Create("identifier.md", "I", "Code", "call UpdateInventoryAsync(ct) here"),
                TestNotes.Create("word.md", "W", "Words", "the inventory was wrong"),
            ],
            Ct);

        var hits = await Search("inventory");

        hits.Select(hit => hit.Note.Path.Value).Should().Equal("word.md", "identifier.md");
    }

    [Theory]
    [InlineData("\"")]
    [InlineData("\"unbalanced")]
    [InlineData("a AND")]
    [InlineData("NEAR(")]
    [InlineData("title: x")]
    [InlineData("(((")]
    [InlineData("*")]
    [InlineData("a* OR b*")]
    [InlineData("-- ; DROP TABLE notes; --")]
    [InlineData("' OR 1=1 --")]
    [InlineData("{title body}: deadlock")]
    [InlineData("^deadlock")]
    public async Task SearchAsync_HostileInput_NeverFailsNorDamagesTheIndex(string text)
    {
        await SeedSearchCorpusAsync();

        var act = () => Search(text);

        await act.Should().NotThrowAsync();
        (await Index.CountAsync(Ct)).Should().Be(3);
    }

    [Fact]
    public async Task SearchAsync_EmptyQueryOrZeroLimit_ReturnsNothing()
    {
        await SeedSearchCorpusAsync();

        (await Index.SearchAsync(new SearchQuery(FtsQuery.Empty, NoteSortOrder.Relevance, 10), Ct)).Should().BeEmpty();
        (await Index.SearchAsync(new SearchQuery(FtsQueryBuilder.Build("deadlock"), NoteSortOrder.Relevance, 0), Ct)).Should().BeEmpty();
    }

    [Fact]
    public async Task StoredText_CannotForgeHighlightMarkers()
    {
        var forged = $"plain {SnippetParser.MatchStart}fake{SnippetParser.MatchEnd} word\0after";
        await Index.UpsertAsync([TestNotes.Create("a.md", "A", "Title", forged)], Ct);

        var hit = (await Search("word")).Should().ContainSingle().Subject;

        hit.Snippet.Where(segment => segment.IsMatch).Select(segment => segment.Text).Should().Equal("word");
        string.Concat(hit.Snippet.Select(segment => segment.Text)).Should().Contain("after", "NUL must not truncate the stored text");
    }

    [Fact]
    public async Task ConcurrentReadersAndWriters_DoNotFail()
    {
        await SeedSearchCorpusAsync();

        var writers = Enumerable.Range(0, 4).Select(w => Task.Run(
            async () =>
            {
                for (var i = 0; i < 25; i++)
                {
                    await Index.UpsertAsync([TestNotes.Create($"w{w}/{i}.md", $"W{w}-{i}", $"Writer {w} note {i}", "concurrent body")], Ct);
                }
            },
            Ct));
        var readers = Enumerable.Range(0, 4).Select(_ => Task.Run(
            async () =>
            {
                for (var i = 0; i < 25; i++)
                {
                    await Search("deadlock");
                    await Index.ListAsync(new NoteListQuery(NoteSortOrder.UpdatedDescending, 20), Ct);
                }
            },
            Ct));

        await Task.WhenAll(writers.Concat(readers));

        (await Index.CountAsync(Ct)).Should().Be(103);
    }

    [Fact]
    public async Task DisposeAsync_IsIdempotent_AndBlocksFurtherUse()
    {
        await Index.DisposeAsync();
        await Index.DisposeAsync();

        await FluentActions.Invoking(() => Index.CountAsync(Ct)).Should().ThrowAsync<ObjectDisposedException>();
        await FluentActions.Invoking(() => Index.InitializeAsync(Ct)).Should().ThrowAsync<ObjectDisposedException>();
    }

    [Fact]
    public async Task DisposeAsync_WhileWritersAreQueued_LetsThemFinishOrRejectsThemCleanly()
    {
        // Closing a vault while saves are still being indexed: every writer either completes or is told
        // the index is closed. No other failure (such as releasing a lock that no longer exists) may leak.
        var writers = Enumerable.Range(0, 40).Select(i => Task.Run(
            async () =>
            {
                try
                {
                    await Index.UpsertAsync([TestNotes.Create($"n{i}.md", $"N{i}", $"Note {i}", new string('x', 2_000))], Ct);
                    return (Exception?)null;
                }
                catch (Exception exception)
                {
                    return exception;
                }
            },
            Ct)).ToList();

        await Index.DisposeAsync();
        var outcomes = await Task.WhenAll(writers);

        outcomes.Where(outcome => outcome is not null).Should().AllBeOfType<ObjectDisposedException>();
    }

    [Theory]
    [InlineData("Ábaco", "ABACO")]
    [InlineData("ñandú", "NANDU")]
    [InlineData("plain", "PLAIN")]
    public void ToSortKey_FoldsCaseAndDiacritics(string title, string expected)
    {
        SqliteNoteIndex.ToSortKey(title).Should().Be(expected);
    }

    protected Task<IReadOnlyList<SearchHit>> Search(string text) =>
        Index.SearchAsync(new SearchQuery(FtsQueryBuilder.Build(text), NoteSortOrder.Relevance, 50), Ct);

    private Task SeedSearchCorpusAsync() =>
        Index.UpsertAsync(
            [
                TestNotes.Create(
                    "bug.md", "BUG", "Deadlock en actualización de inventario", "El proceso nocturno se bloqueaba con SQL Server.",
                    tags: ["sql-server", "deadlock", "performance"], type: NoteType.Bug),
                TestNotes.Create("code.md", "CODE", "Snippet de repositorio", "await repository.UpdateInventoryAsync(items, ct);", type: NoteType.Snippet),
                TestNotes.Create("ops.md", "OPS", "Runbook de despliegue", "Pasos para publicar el servicio en Azure.", type: NoteType.Runbook),
            ],
            Ct);
}

public sealed class InMemorySqliteNoteIndexTests : SqliteNoteIndexTests
{
    protected override SqliteNoteIndex CreateIndex() => SqliteNoteIndex.InMemory(NullLogger<SqliteNoteIndex>.Instance);
}

public sealed class OnDiskSqliteNoteIndexTests : SqliteNoteIndexTests
{
    private readonly TempDirectory _temp = new();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private string DatabasePath => _temp.Combine("indexes", "vault.db");

    protected override SqliteNoteIndex CreateIndex() => SqliteNoteIndex.ForFile(DatabasePath, NullLogger<SqliteNoteIndex>.Instance);

    public override async ValueTask DisposeAsync()
    {
        await base.DisposeAsync();
        _temp.Dispose();
    }

    [Fact]
    public async Task Database_UsesWalForeignKeysSchemaVersionAndARecentSqlite()
    {
        await using var connection = await OpenRawAsync();

        (await ScalarAsync(connection, "PRAGMA journal_mode")).Should().Be("wal");
        (await ScalarAsync(connection, "PRAGMA user_version")).Should().Be((long)IndexSchema.Version);

        var version = Version.Parse((string)(await ScalarAsync(connection, "SELECT sqlite_version()"))!);
        version.Should().BeGreaterThanOrEqualTo(new Version(3, 45), "the trigram tokenizer needs remove_diacritics support");
        var options = await ScalarAsync(connection, "SELECT group_concat(compile_options, ' ') FROM pragma_compile_options");
        options.Should().BeOfType<string>().Which.Should().Contain("ENABLE_FTS5");
    }

    [Fact]
    public async Task TagsAndLinks_AreNormalizedIntoTheirTables_AndCascadeOnDelete()
    {
        var links = new NoteLinks(["a3f9c21", "7be04d8"], ["MS-482"], ["benchmark-where-select-2026-08"]);
        await Index.UpsertAsync([TestNotes.Create("a.md", "A", "Title", "body", tags: ["sql-server", "deadlock"], links: links)], Ct);
        await using var connection = await OpenRawAsync();

        (await ScalarAsync(connection, "SELECT group_concat(tag, ',') FROM (SELECT tag FROM tags WHERE note_id = 'A' ORDER BY tag)"))
            .Should().Be("deadlock,sql-server");
        (await ScalarAsync(connection, "SELECT group_concat(kind || ':' || target, ',') FROM (SELECT kind, target FROM links ORDER BY kind, target)"))
            .Should().Be("commit:7be04d8,commit:a3f9c21,note:benchmark-where-select-2026-08,ticket:MS-482");

        await Index.RemoveAsync([NotePath.Create("a.md")], Ct);

        (await ScalarAsync(connection, "SELECT (SELECT COUNT(*) FROM tags) + (SELECT COUNT(*) FROM links)")).Should().Be(0L);
    }

    [Fact]
    public async Task FullTextIndexes_StayConsistentWithTheContentTable()
    {
        await Index.UpsertAsync([TestNotes.Create("a.md", "A", "One", "first"), TestNotes.Create("b.md", "B", "Two", "second")], Ct);
        await Index.UpsertAsync([TestNotes.Create("a.md", "A", "One", "changed")], Ct);
        await Index.RemoveAsync([NotePath.Create("b.md")], Ct);
        await using var connection = await OpenRawAsync();

        // FTS5 'integrity-check' compares an external-content index with its content table and fails on any mismatch.
        var act = async () =>
        {
            await ScalarAsync(connection, "INSERT INTO notes_fts (notes_fts, rank) VALUES ('integrity-check', 1)");
            await ScalarAsync(connection, "INSERT INTO notes_trigram (notes_trigram, rank) VALUES ('integrity-check', 1)");
        };

        await act.Should().NotThrowAsync();
    }

    [Fact]
    public async Task Data_SurvivesReopening()
    {
        await Index.UpsertAsync([TestNotes.Create("a.md", "A", "Persistent", "body")], Ct);
        await Index.DisposeAsync();

        await using var reopened = CreateIndex();
        await reopened.InitializeAsync(Ct);

        (await reopened.CountAsync(Ct)).Should().Be(1);
    }

    [Fact]
    public async Task InitializeAsync_CorruptFile_IsReplacedByAFreshIndex()
    {
        await Index.DisposeAsync();
        await File.WriteAllTextAsync(DatabasePath, "this is definitely not a SQLite database, just some text long enough to be read", Ct);

        await using var recovered = CreateIndex();
        await recovered.InitializeAsync(Ct);

        (await recovered.CountAsync(Ct)).Should().Be(0);
        await recovered.UpsertAsync([TestNotes.Create("a.md", "A", "After recovery", "body")], Ct);
        (await recovered.CountAsync(Ct)).Should().Be(1);
    }

    [Fact]
    public async Task InitializeAsync_DatabaseFromAnotherSchemaVersion_IsRebuilt()
    {
        await Index.UpsertAsync([TestNotes.Create("a.md", "A", "Old schema", "body")], Ct);
        await using (var connection = await OpenRawAsync())
        {
            await ScalarAsync(connection, "PRAGMA user_version = 99");
        }

        await Index.DisposeAsync();
        await using var upgraded = CreateIndex();
        await upgraded.InitializeAsync(Ct);

        (await upgraded.CountAsync(Ct)).Should().Be(0, "a disposable index is rebuilt instead of migrated");
        await using var check = await OpenRawAsync();
        (await ScalarAsync(check, "PRAGMA user_version")).Should().Be((long)IndexSchema.Version);
    }

    [Fact]
    public async Task DisposeAsync_ReleasesTheFile()
    {
        await Index.UpsertAsync([TestNotes.Create("a.md", "A", "Title", "body")], Ct);

        await Index.DisposeAsync();

        FluentActions.Invoking(() => SqliteNoteIndex.DeleteDatabaseFiles(DatabasePath)).Should().NotThrow();
        File.Exists(DatabasePath).Should().BeFalse();
        File.Exists(DatabasePath + "-wal").Should().BeFalse();
    }

    private async Task<SqliteConnection> OpenRawAsync()
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = DatabasePath, Pooling = false, ForeignKeys = true }.ToString());
        await connection.OpenAsync(Ct);
        return connection;
    }

    private static async Task<object?> ScalarAsync(SqliteConnection connection, string sql)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        return await command.ExecuteScalarAsync(Ct);
    }
}
