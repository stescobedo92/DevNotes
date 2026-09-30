using DevNotes.Application.Abstractions;
using DevNotes.Application.Indexing;
using DevNotes.Application.Tests.Fakes;
using DevNotes.Domain.Notes;
using Microsoft.Extensions.Logging.Abstractions;

namespace DevNotes.Application.Tests.Indexing;

public sealed class VaultIndexerTests
{
    private readonly InMemoryNoteFileStore _files = new();
    private readonly InMemoryNoteIndex _index = new();
    private readonly VaultIndexer _indexer;

    public VaultIndexerTests()
    {
        _indexer = new VaultIndexer(_files, _index, new IndexingOptions { BatchSize = 3, MaxDegreeOfParallelism = 2 }, NullLogger<VaultIndexer>.Instance);
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task SynchronizeAsync_EmptyIndex_IndexesEveryNote()
    {
        for (var i = 1; i <= 8; i++)
        {
            _files.SetExternal($"notes/n{i}.md", NoteText.Simple($"ID-{i}", $"Title {i}", $"Body {i}"));
        }

        _files.SetExternal(".git/ignored.md", "hidden");
        var progress = new List<IndexProgress>();

        var summary = await _indexer.SynchronizeAsync(new CollectingProgress(progress), Ct);

        summary.Should().BeEquivalentTo(new { Scanned = 8, Indexed = 8, Unchanged = 0, Removed = 0, Failed = 0 });
        _index.Notes.Should().HaveCount(8);
        _index.UpsertCalls.Should().Be(3, "8 notes with a batch size of 3 are written in 3 transactions");
        progress.Should().Equal(new IndexProgress(0, 8), new IndexProgress(3, 8), new IndexProgress(6, 8), new IndexProgress(8, 8));

        var note = _index.Find("notes/n3.md")!;
        note.Id.Value.Should().Be("ID-3");
        note.Metadata.Title.Should().Be("Title 3");
        note.Body.Should().Be("\nBody 3");
        note.Hash.Should().Be(ContentHash.Compute(_files.TextOf("notes/n3.md")!));
    }

    [Fact]
    public async Task SynchronizeAsync_NothingChanged_ReadsNoFiles()
    {
        _files.SetExternal("a.md", NoteText.Simple("A", "A"));
        _files.SetExternal("b.md", NoteText.Simple("B", "B"));
        await _indexer.SynchronizeAsync(null, Ct);
        var readsAfterFirstScan = _files.ReadCount;

        var summary = await _indexer.SynchronizeAsync(null, Ct);

        summary.Should().BeEquivalentTo(new { Scanned = 2, Indexed = 0, Unchanged = 2, Removed = 0, Failed = 0 });
        _files.ReadCount.Should().Be(readsAfterFirstScan, "size and modification time are enough to skip unchanged files");
    }

    [Fact]
    public async Task SynchronizeAsync_TouchedFileWithSameContent_IsNotReindexed()
    {
        _files.SetExternal("a.md", NoteText.Simple("A", "A"));
        await _indexer.SynchronizeAsync(null, Ct);
        _files.Touch("a.md");

        var summary = await _indexer.SynchronizeAsync(null, Ct);

        summary.Indexed.Should().Be(0);
        summary.Unchanged.Should().Be(1);
        _index.UpsertedNotes.Should().Be(1, "the content hash did not change");
        _index.TouchedFiles.Should().Be(1);

        // The refreshed timestamp makes the next scan skip the file without reading it.
        var reads = _files.ReadCount;
        await _indexer.SynchronizeAsync(null, Ct);
        _files.ReadCount.Should().Be(reads);
    }

    [Fact]
    public async Task SynchronizeAsync_ModifiedAndDeletedFiles_AreReflected()
    {
        _files.SetExternal("keep.md", NoteText.Simple("K", "Keep"));
        _files.SetExternal("edit.md", NoteText.Simple("E", "Before"));
        _files.SetExternal("gone.md", NoteText.Simple("G", "Gone"));
        await _indexer.SynchronizeAsync(null, Ct);

        _files.SetExternal("edit.md", NoteText.Simple("E", "After"));
        _files.DeleteExternal("gone.md");
        _files.SetExternal("new.md", NoteText.Simple("N", "New"));

        var summary = await _indexer.SynchronizeAsync(null, Ct);

        summary.Should().BeEquivalentTo(new { Scanned = 3, Indexed = 2, Unchanged = 1, Removed = 1, Failed = 0 });
        _index.Notes.Select(note => note.Path.Value).Should().BeEquivalentTo("keep.md", "edit.md", "new.md");
        _index.Find("edit.md")!.Metadata.Title.Should().Be("After");
    }

    [Fact]
    public async Task SynchronizeAsync_NoteWithoutId_GetsStablePathDerivedId()
    {
        _files.SetExternal("plain.md", "# Just markdown\n");

        await _indexer.SynchronizeAsync(null, Ct);

        var note = _index.Find("plain.md")!;
        note.Id.Should().Be(NoteId.FromPath(NotePath.Create("plain.md")));
        note.Id.IsPathDerived.Should().BeTrue();
        note.Metadata.Title.Should().Be("Just markdown");
        _files.TextOf("plain.md").Should().Be("# Just markdown\n", "indexing never modifies the user's files");
    }

    [Fact]
    public async Task SynchronizeAsync_CopiedNoteWithSameId_KeepsBothAndOnlyTheFirstOwnsTheId()
    {
        _files.SetExternal("a-original.md", NoteText.Simple("SHARED", "Original"));
        _files.SetExternal("b-copy.md", NoteText.Simple("SHARED", "Copy"));

        await _indexer.SynchronizeAsync(null, Ct);

        _index.Notes.Should().HaveCount(2);
        _index.Find("a-original.md")!.Id.Value.Should().Be("SHARED");
        _index.Find("b-copy.md")!.Id.Should().Be(NoteId.FromPath(NotePath.Create("b-copy.md")));
    }

    [Fact]
    public async Task SynchronizeAsync_CopyAddedLater_DoesNotStealTheIdFromTheIndexedNote()
    {
        _files.SetExternal("z-original.md", NoteText.Simple("SHARED", "Original"));
        await _indexer.SynchronizeAsync(null, Ct);
        _files.SetExternal("a-copy.md", NoteText.Simple("SHARED", "Copy"));

        await _indexer.SynchronizeAsync(null, Ct);

        _index.Find("z-original.md")!.Id.Value.Should().Be("SHARED");
        _index.Find("a-copy.md")!.Id.IsPathDerived.Should().BeTrue();
    }

    [Fact]
    public async Task SynchronizeAsync_RenamedFile_KeepsItsIdentity()
    {
        var text = NoteText.Simple("STABLE", "Moved note");
        _files.SetExternal("old/name.md", text);
        await _indexer.SynchronizeAsync(null, Ct);

        _files.DeleteExternal("old/name.md");
        _files.SetExternal("new/name.md", text);
        var summary = await _indexer.SynchronizeAsync(null, Ct);

        _index.Notes.Should().ContainSingle().Which.Path.Value.Should().Be("new/name.md");
        _index.Notes[0].Id.Value.Should().Be("STABLE");
        summary.Removed.Should().Be(1);
    }

    [Fact]
    public async Task SynchronizeAsync_UnreadableFile_IsCountedAndDoesNotAbortTheScan()
    {
        _files.SetExternal("good.md", NoteText.Simple("G", "Good"));
        _files.SetExternal("locked.md", NoteText.Simple("L", "Locked"));
        _files.MakeUnreadable("locked.md");

        var summary = await _indexer.SynchronizeAsync(null, Ct);

        summary.Failed.Should().Be(1);
        summary.Indexed.Should().Be(1);
        _index.Notes.Should().ContainSingle().Which.Path.Value.Should().Be("good.md");
    }

    [Fact]
    public async Task SynchronizeAsync_InvalidFrontmatter_StillIndexesTheNote()
    {
        _files.SetExternal("broken.md", "---\ntitle: [oops\n---\n# Heading\nsearchable text");

        await _indexer.SynchronizeAsync(null, Ct);

        var note = _index.Find("broken.md")!;
        note.Metadata.Title.Should().Be("Heading");
        note.Body.Should().Contain("searchable text");
    }

    [Fact]
    public async Task SynchronizeAsync_Cancelled_ThrowsAndStops()
    {
        _files.SetExternal("a.md", NoteText.Simple("A", "A"));
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        var act = () => _indexer.SynchronizeAsync(null, cts.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
        _index.Notes.Should().BeEmpty();
    }

    [Fact]
    public async Task RebuildAsync_ClearsBeforeScanning()
    {
        _files.SetExternal("a.md", NoteText.Simple("A", "A"));
        await _indexer.SynchronizeAsync(null, Ct);

        var summary = await _indexer.RebuildAsync(null, Ct);

        _index.ClearCalls.Should().Be(1);
        summary.Indexed.Should().Be(1, "after a rebuild every file is indexed again");
        _index.Notes.Should().ContainSingle();
    }

    [Fact]
    public async Task IndexFileAsync_ExistingFile_IsUpserted_AndMissingFileIsRemoved()
    {
        _files.SetExternal("a.md", NoteText.Simple("A", "First"));
        await _indexer.IndexFileAsync(NotePath.Create("a.md"), Ct);
        _index.Find("a.md")!.Metadata.Title.Should().Be("First");

        _files.SetExternal("a.md", NoteText.Simple("A", "Second"));
        await _indexer.IndexFileAsync(NotePath.Create("a.md"), Ct);
        _index.Notes.Should().ContainSingle().Which.Metadata.Title.Should().Be("Second");

        _files.DeleteExternal("a.md");
        await _indexer.IndexFileAsync(NotePath.Create("a.md"), Ct);
        _index.Notes.Should().BeEmpty();
    }

    [Fact]
    public async Task IndexFileAsync_DuplicateIdWhileOwnerExists_UsesPathDerivedId()
    {
        _files.SetExternal("owner.md", NoteText.Simple("SHARED", "Owner"));
        _files.SetExternal("copy.md", NoteText.Simple("SHARED", "Copy"));
        await _indexer.IndexFileAsync(NotePath.Create("owner.md"), Ct);

        await _indexer.IndexFileAsync(NotePath.Create("copy.md"), Ct);

        _index.Find("owner.md")!.Id.Value.Should().Be("SHARED");
        _index.Find("copy.md")!.Id.IsPathDerived.Should().BeTrue();
    }

    [Fact]
    public async Task IndexFileAsync_SameIdAfterOwnerWasMoved_FollowsTheNote()
    {
        var text = NoteText.Simple("SHARED", "Note");
        _files.SetExternal("old.md", text);
        await _indexer.IndexFileAsync(NotePath.Create("old.md"), Ct);
        _files.DeleteExternal("old.md");
        _files.SetExternal("new.md", text);

        await _indexer.IndexFileAsync(NotePath.Create("new.md"), Ct);

        _index.Notes.Should().ContainSingle().Which.Path.Value.Should().Be("new.md");
        _index.Notes[0].Id.Value.Should().Be("SHARED");
    }

    [Fact]
    public async Task RemoveAsync_EmptyList_DoesNothing()
    {
        _index.FailNextMutation = new InvalidOperationException("must not be called");

        await _indexer.RemoveAsync([], Ct);

        _index.FailNextMutation.Should().NotBeNull();
    }

    [Fact]
    public void Batch_IsLimitedByNumberOfNotesAndByBytes()
    {
        const long megabyte = 1024 * 1024;
        NoteFileInfo File(string name, long megabytes) => new(NotePath.Create(name), megabytes * megabyte, DateTimeOffset.UnixEpoch);
        NoteFileInfo[] files =
        [
            File("a.md", 1), File("b.md", 1), File("c.md", 1), // limited by count (2)
            File("d.md", 20), File("e.md", 20),                // 40 MB together: more than one batch may hold
            File("f.md", 100),                                 // larger than the byte limit on its own: a batch of one
            File("g.md", 1),
        ];

        var batches = VaultIndexer.Batch(files, maxCount: 2).Select(batch => batch.Select(file => file.Path.Value).ToArray()).ToList();

        batches.Should().BeEquivalentTo(
            new[] { new[] { "a.md", "b.md" }, ["c.md", "d.md"], ["e.md"], ["f.md"], ["g.md"] },
            options => options.WithStrictOrdering());
        VaultIndexer.Batch([], maxCount: 5).Should().BeEmpty();
    }

    [Fact]
    public void IndexingOptions_ClampUnreasonableValues()
    {
        var options = new IndexingOptions { BatchSize = 0, MaxDegreeOfParallelism = 999, WatcherDebounceMilliseconds = -5, MaxBufferedChanges = 0 };

        options.EffectiveBatchSize.Should().Be(1);
        options.EffectiveParallelism.Should().Be(32);
        options.WatcherDebounce.Should().Be(TimeSpan.Zero);
        options.EffectiveMaxBufferedChanges.Should().Be(1);
    }

    private sealed class CollectingProgress(List<IndexProgress> reports) : IProgress<IndexProgress>
    {
        public void Report(IndexProgress value) => reports.Add(value);
    }
}
