using DevNotes.Application.Abstractions;
using DevNotes.Application.Indexing;
using DevNotes.Application.Notes;
using DevNotes.Application.Tests.Fakes;
using DevNotes.Domain.Notes;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;

namespace DevNotes.Application.Tests.Notes;

public sealed class NoteServiceTests
{
    private readonly InMemoryNoteFileStore _files = new();
    private readonly InMemoryNoteIndex _index = new();
    private readonly VaultEventHub _events = new();
    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 9, 30, 12, 0, 0, TimeSpan.Zero));
    private readonly List<NotesChangedEventArgs> _changes = [];
    private readonly NoteService _service;

    public NoteServiceTests()
    {
        _time.SetLocalTimeZone(TimeZoneInfo.Utc);
        var indexer = new VaultIndexer(_files, _index, new IndexingOptions(), NullLogger<VaultIndexer>.Instance);
        _service = new NoteService(_files, indexer, _events, new SequentialNoteIdGenerator(), _time, NullLogger<NoteService>.Instance);
        _events.NotesChanged += (_, e) => _changes.Add(e);
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task OpenAsync_ExistingNote_ReturnsTextHashAndParsedDocument()
    {
        var text = NoteText.Simple("A", "Title");
        _files.SetExternal("a.md", text);

        var opened = await _service.OpenAsync(NotePath.Create("a.md"), Ct);

        opened.Should().NotBeNull();
        opened!.Text.Should().Be(text);
        opened.Hash.Should().Be(ContentHash.Compute(text));
        opened.Document.Metadata.Title.Should().Be("Title");
    }

    [Fact]
    public async Task OpenAsync_MissingNote_ReturnsNull()
    {
        (await _service.OpenAsync(NotePath.Create("nope.md"), Ct)).Should().BeNull();
    }

    [Fact]
    public async Task CreateAsync_WritesTemplateIndexesAndNotifies()
    {
        var created = await _service.CreateAsync(new NewNoteRequest("Deadlock en inventario", "bugs/2026", NoteType.Bug, "cslinq"), Ct);

        created.Path.Value.Should().Be("bugs/2026/deadlock-en-inventario.md");
        created.Document.Metadata.Should().BeEquivalentTo(new NoteMetadata
        {
            Id = NoteId.Parse("01TEST00000000000000000001"),
            Title = "Deadlock en inventario",
            Project = "cslinq",
            Type = NoteType.Bug,
            Created = new DateOnly(2026, 9, 30),
            Updated = new DateOnly(2026, 9, 30),
        });
        _files.TextOf(created.Path.Value).Should().Be(created.Text);
        _index.Find(created.Path.Value)!.Id.Should().Be(created.Document.Metadata.Id!.Value);
        _changes.Should().ContainSingle().Which.Should().Match<NotesChangedEventArgs>(e =>
            e.Source == NotesChangeSource.Local && e.Paths.Single() == created.Path);
    }

    [Fact]
    public async Task CreateAsync_NameTaken_AppendsNumericSuffixWithoutOverwriting()
    {
        _files.SetExternal("same-title.md", "existing content");

        var second = await _service.CreateAsync(new NewNoteRequest("Same title"), Ct);
        var third = await _service.CreateAsync(new NewNoteRequest("Same title"), Ct);

        second.Path.Value.Should().Be("same-title-2.md");
        third.Path.Value.Should().Be("same-title-3.md");
        _files.TextOf("same-title.md").Should().Be("existing content");
        second.Document.Metadata.Id.Should().NotBe(third.Document.Metadata.Id);
    }

    [Theory]
    [InlineData(null, "notes")]
    [InlineData("   ", "notes")]
    [InlineData("Valid", "../outside")]
    [InlineData("Valid", "a/../../b")]
    [InlineData("Valid", "con")]
    public async Task CreateAsync_InvalidTitleOrFolder_ThrowsAndWritesNothing(string? title, string folder)
    {
        var act = () => _service.CreateAsync(new NewNoteRequest(title!, folder), Ct);

        await act.Should().ThrowAsync<ArgumentException>();
        _files.Paths.Should().BeEmpty();
    }

    [Fact]
    public async Task SaveAsync_UnchangedOnDisk_WritesStampsAndReindexes()
    {
        var original = NoteText.WithFrontmatter("id: A\ntitle: Old", "Body\n");
        var info = _files.SetExternal("a.md", original);
        var edited = original.Replace("Body", "Edited body", StringComparison.Ordinal);

        var result = await _service.SaveAsync(info.Path, edited, ContentHash.Compute(original), SaveMode.DetectConflicts, Ct);

        var saved = result.Should().BeOfType<SaveResult.Saved>().Subject.Note;
        saved.Text.Should().Contain("Edited body").And.Contain("updated: 2026-09-30");
        saved.Hash.Should().Be(ContentHash.Compute(saved.Text));
        _files.TextOf("a.md").Should().Be(saved.Text);
        _index.Find("a.md")!.Body.Should().Contain("Edited body");
        _index.Find("a.md")!.Metadata.Updated.Should().Be(new DateOnly(2026, 9, 30));
        _changes.Should().ContainSingle();
    }

    [Fact]
    public async Task SaveAsync_FileChangedOnDisk_ReturnsConflictAndNeverOverwrites()
    {
        var original = NoteText.Simple("A", "Title", "v1");
        var info = _files.SetExternal("a.md", original);
        var external = NoteText.Simple("A", "Title", "changed in VS Code");
        _files.SetExternal("a.md", external);

        var result = await _service.SaveAsync(info.Path, NoteText.Simple("A", "Title", "my edit"), ContentHash.Compute(original), SaveMode.DetectConflicts, Ct);

        var conflict = result.Should().BeOfType<SaveResult.Conflict>().Subject;
        conflict.Kind.Should().Be(SaveConflictKind.ModifiedOnDisk);
        conflict.Disk!.Text.Should().Be(external);
        _files.TextOf("a.md").Should().Be(external, "the external version must survive");
        _changes.Should().BeEmpty();
    }

    [Fact]
    public async Task SaveAsync_FileDeletedOnDisk_ReturnsConflict()
    {
        var original = NoteText.Simple("A", "Title");
        var info = _files.SetExternal("a.md", original);
        _files.DeleteExternal("a.md");

        var result = await _service.SaveAsync(info.Path, original + "more", ContentHash.Compute(original), SaveMode.DetectConflicts, Ct);

        result.Should().BeOfType<SaveResult.Conflict>().Which.Kind.Should().Be(SaveConflictKind.DeletedOnDisk);
        _files.Exists("a.md").Should().BeFalse();
    }

    [Fact]
    public async Task SaveAsync_OverwriteMode_KeepsTheUserVersion()
    {
        var original = NoteText.Simple("A", "Title", "v1");
        var info = _files.SetExternal("a.md", original);
        _files.SetExternal("a.md", NoteText.Simple("A", "Title", "external"));
        var mine = NoteText.Simple("A", "Title", "mine");

        var result = await _service.SaveAsync(info.Path, mine, ContentHash.Compute(original), SaveMode.Overwrite, Ct);

        result.Should().BeOfType<SaveResult.Saved>();
        _files.TextOf("a.md").Should().Contain("mine").And.NotContain("external");
    }

    [Fact]
    public async Task SaveAsync_OverwriteMode_RecreatesADeletedFile()
    {
        var path = NotePath.Create("a.md");
        var mine = NoteText.Simple("A", "Title", "mine");

        var result = await _service.SaveAsync(path, mine, ContentHash.Compute("whatever"), SaveMode.Overwrite, Ct);

        result.Should().BeOfType<SaveResult.Saved>();
        _files.Exists("a.md").Should().BeTrue();
    }

    [Fact]
    public async Task SaveAsync_IdenticalContent_DoesNotTouchTheFile()
    {
        var text = "---\nid: A\nupdated: 2020-01-01\n---\nBody\n";
        var info = _files.SetExternal("a.md", text);
        var writes = _files.WriteCount;

        var result = await _service.SaveAsync(info.Path, text, ContentHash.Compute(text), SaveMode.DetectConflicts, Ct);

        result.Should().BeOfType<SaveResult.Saved>().Which.Note.Text.Should().Be(text);
        _files.WriteCount.Should().Be(writes);
        _files.TextOf("a.md").Should().Contain("updated: 2020-01-01", "an unchanged note keeps its date");
        _changes.Should().BeEmpty();
    }

    [Fact]
    public async Task SaveAsync_NoteWithoutFrontmatter_GetsAnIdOnFirstSave()
    {
        var info = _files.SetExternal("plain.md", "# Plain\n");

        var result = await _service.SaveAsync(info.Path, "# Plain\n\nmore\n", ContentHash.Compute("# Plain\n"), SaveMode.DetectConflicts, Ct);

        var saved = result.Should().BeOfType<SaveResult.Saved>().Subject.Note;
        saved.Text.Should().StartWith("---\nid: 01TEST00000000000000000001\nupdated: 2026-09-30\n---\n");
        saved.Document.Metadata.Id!.Value.IsPathDerived.Should().BeFalse();
        _index.Notes.Should().ContainSingle().Which.Id.Should().Be(saved.Document.Metadata.Id.Value);
    }

    [Fact]
    public async Task SaveAsync_IndexFailure_StillReportsTheNoteAsSaved()
    {
        var original = NoteText.Simple("A", "Title");
        var info = _files.SetExternal("a.md", original);
        _index.FailNextMutation = new InvalidOperationException("index is broken");
        IndexStatus? status = null;
        _events.IndexStatusChanged += (_, s) => status = s;

        var result = await _service.SaveAsync(info.Path, original + "edit", ContentHash.Compute(original), SaveMode.DetectConflicts, Ct);

        result.Should().BeOfType<SaveResult.Saved>();
        _files.TextOf("a.md").Should().Contain("edit");
        status.Should().NotBeNull();
        status!.State.Should().Be(IndexState.Failed);
        status.Error.Should().Be("index is broken");
        _events.Status.Should().BeSameAs(status);
    }

    [Fact]
    public async Task RenameAsync_UpdatesTitleAndFileNameAndKeepsIdentity()
    {
        _files.SetExternal("bugs/old-name.md", NoteText.WithFrontmatter("id: A\ntitle: Old name\ncustom: keep"));
        await _service.SaveAsync(NotePath.Create("bugs/old-name.md"), _files.TextOf("bugs/old-name.md")! + "x", ContentHash.Compute(_files.TextOf("bugs/old-name.md")!), SaveMode.DetectConflicts, Ct);
        _changes.Clear();

        var renamed = await _service.RenameAsync(NotePath.Create("bugs/old-name.md"), "  Nuevo   título: v2 ", Ct);

        renamed.Value.Should().Be("bugs/nuevo-titulo-v2.md");
        _files.Exists("bugs/old-name.md").Should().BeFalse();
        _files.TextOf(renamed.Value).Should().Contain("title: \"Nuevo título: v2\"").And.Contain("custom: keep");
        _index.Notes.Should().ContainSingle().Which.Should().Match<IndexedNote>(note =>
            note.Path == renamed && note.Id.Value == "A" && note.Metadata.Title == "Nuevo título: v2");
        _changes.Should().ContainSingle().Which.Paths.Should().BeEquivalentTo([NotePath.Create("bugs/old-name.md"), renamed]);
    }

    [Fact]
    public async Task RenameAsync_TargetNameTaken_PicksAFreeName()
    {
        _files.SetExternal("a.md", NoteText.Simple("A", "A"));
        _files.SetExternal("taken.md", NoteText.Simple("T", "Taken"));

        var renamed = await _service.RenameAsync(NotePath.Create("a.md"), "Taken", Ct);

        renamed.Value.Should().Be("taken-2.md");
        _files.TextOf("taken.md").Should().Contain("id: T");
    }

    [Fact]
    public async Task RenameAsync_SameSlug_OnlyRewritesTheTitle()
    {
        _files.SetExternal("my-note.md", NoteText.Simple("A", "my note"));

        var renamed = await _service.RenameAsync(NotePath.Create("my-note.md"), "My Note!", Ct);

        renamed.Value.Should().Be("my-note.md");
        _files.TextOf("my-note.md").Should().Contain("title: \"My Note!\"");
    }

    [Fact]
    public async Task RenameAsync_MissingNoteOrInvalidTitle_Throws()
    {
        _files.SetExternal("a.md", NoteText.Simple("A", "A"));

        await FluentActions.Invoking(() => _service.RenameAsync(NotePath.Create("missing.md"), "X", Ct))
            .Should().ThrowAsync<NoteNotFoundException>();
        await FluentActions.Invoking(() => _service.RenameAsync(NotePath.Create("a.md"), " \n ", Ct))
            .Should().ThrowAsync<ArgumentException>();
    }

    [Fact]
    public async Task MoveAsync_MovesFileAndIndexRow()
    {
        _files.SetExternal("a.md", NoteText.Simple("A", "A"));
        await _service.SaveAsync(NotePath.Create("a.md"), _files.TextOf("a.md")! + "x", ContentHash.Compute(_files.TextOf("a.md")!), SaveMode.DetectConflicts, Ct);

        var moved = await _service.MoveAsync(NotePath.Create("a.md"), @"\archive\2026\", Ct);

        moved.Value.Should().Be("archive/2026/a.md");
        _files.Exists("a.md").Should().BeFalse();
        _index.Notes.Should().ContainSingle().Which.Path.Should().Be(moved);
    }

    [Fact]
    public async Task MoveAsync_SameFolder_IsANoOp_AndExistingDestinationIsNeverOverwritten()
    {
        _files.SetExternal("a.md", NoteText.Simple("A", "A"));
        _files.SetExternal("dest/a.md", "other note");

        (await _service.MoveAsync(NotePath.Create("a.md"), string.Empty, Ct)).Value.Should().Be("a.md");
        await FluentActions.Invoking(() => _service.MoveAsync(NotePath.Create("a.md"), "dest", Ct))
            .Should().ThrowAsync<NoteAlreadyExistsException>();
        await FluentActions.Invoking(() => _service.MoveAsync(NotePath.Create("a.md"), "../escape", Ct))
            .Should().ThrowAsync<ArgumentException>();

        _files.TextOf("dest/a.md").Should().Be("other note");
        _files.Exists("a.md").Should().BeTrue();
    }

    [Fact]
    public async Task DeleteAsync_ThenRestoreAsync_RoundTripsThroughTheTrash()
    {
        var text = NoteText.Simple("A", "To delete");
        _files.SetExternal("a.md", text);
        await _service.SaveAsync(NotePath.Create("a.md"), text + "x", ContentHash.Compute(text), SaveMode.DetectConflicts, Ct);

        var entry = await _service.DeleteAsync(NotePath.Create("a.md"), Ct);

        _files.Exists("a.md").Should().BeFalse();
        _index.Notes.Should().BeEmpty();
        (await _service.ListTrashAsync(Ct)).Should().ContainSingle().Which.OriginalPath.Value.Should().Be("a.md");

        var restored = await _service.RestoreAsync(entry.Id, Ct);

        restored.Value.Should().Be("a.md");
        _files.TextOf("a.md").Should().Contain("To delete");
        _index.Notes.Should().ContainSingle().Which.Id.Value.Should().Be("A");
        (await _service.ListTrashAsync(Ct)).Should().BeEmpty();
    }

    [Fact]
    public async Task Trash_CanBePurgedEntryByEntryOrCompletely()
    {
        _files.SetExternal("a.md", "a");
        _files.SetExternal("b.md", "b");
        _files.SetExternal("c.md", "c");
        var a = await _service.DeleteAsync(NotePath.Create("a.md"), Ct);
        await _service.DeleteAsync(NotePath.Create("b.md"), Ct);
        await _service.DeleteAsync(NotePath.Create("c.md"), Ct);

        await _service.DeleteFromTrashAsync(a.Id, Ct);
        (await _service.ListTrashAsync(Ct)).Should().HaveCount(2);

        await _service.EmptyTrashAsync(Ct);
        (await _service.ListTrashAsync(Ct)).Should().BeEmpty();
        await FluentActions.Invoking(() => _service.RestoreAsync(a.Id, Ct)).Should().ThrowAsync<TrashEntryNotFoundException>();
    }

    [Fact]
    public async Task ListFoldersAsync_ReturnsDistinctFolders()
    {
        _files.SetExternal("root.md", "x");
        _files.SetExternal("b/two.md", "x");
        _files.SetExternal("a/one.md", "x");
        _files.SetExternal("a/three.md", "x");

        (await _service.ListFoldersAsync(Ct)).Should().Equal("a", "b");
    }
}
