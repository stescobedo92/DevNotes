using DevNotes.Application.Abstractions;
using DevNotes.Application.Indexing;
using DevNotes.Application.Search;
using DevNotes.Application.Tests.Fakes;
using DevNotes.Application.Vaults;
using DevNotes.Domain.Notes;
using DevNotes.Domain.Vaults;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using NSubstitute;

namespace DevNotes.Application.Tests.Vaults;

public sealed class VaultSessionTests : IAsyncDisposable
{
    private static readonly TimeSpan _debounce = TimeSpan.FromMilliseconds(300);

    private readonly InMemoryNoteFileStore _files = new();
    private readonly InMemoryNoteIndex _index = new();
    private readonly ManualVaultWatcher _watcher = new();
    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 9, 30, 12, 0, 0, TimeSpan.Zero));
    private readonly List<NotesChangedEventArgs> _changes = [];
    private readonly List<IndexStatus> _statuses = [];
    private readonly VaultSession _session;

    public VaultSessionTests()
    {
        _session = CreateSession(new IndexingOptions { WatcherDebounceMilliseconds = 300, MaxBufferedChanges = 5 });
        _session.Events.NotesChanged += (_, e) =>
        {
            lock (_changes)
            {
                _changes.Add(e);
            }
        };
        _session.Events.IndexStatusChanged += (_, status) =>
        {
            lock (_statuses)
            {
                _statuses.Add(status);
            }
        };
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public ValueTask DisposeAsync() => _session.DisposeAsync();

    [Fact]
    public async Task StartAsync_InitializesIndexStartsWatcherAndRunsInitialScan()
    {
        _files.SetExternal("a.md", NoteText.Simple("A", "Alpha"));
        _files.SetExternal("b.md", NoteText.Simple("B", "Beta"));

        await _session.StartAsync(Ct);
        await _session.WhenIdleAsync(Ct);

        _index.IsInitialized.Should().BeTrue();
        _watcher.IsStarted.Should().BeTrue();
        _index.Notes.Should().HaveCount(2);
        Changes().Should().ContainSingle().Which.Source.Should().Be(NotesChangeSource.Scan);
        Statuses().First().State.Should().Be(IndexState.Indexing);
        Statuses().Last().Should().Be(IndexStatus.Idle);
        (await _session.Queries.CountAsync(Ct)).Should().Be(2);
    }

    [Fact]
    public async Task StartAsync_Twice_Throws()
    {
        await _session.StartAsync(Ct);

        await FluentActions.Invoking(() => _session.StartAsync(Ct)).Should().ThrowAsync<InvalidOperationException>();
    }

    [Fact]
    public async Task StartAsync_WatcherUnavailable_StillIndexes()
    {
        _watcher.FailOnStart = new IOException("inotify limit reached");
        _files.SetExternal("a.md", NoteText.Simple("A", "Alpha"));

        await _session.StartAsync(Ct);
        await _session.WhenIdleAsync(Ct);

        _index.Notes.Should().ContainSingle();
    }

    [Fact]
    public async Task ExternalChanges_AreDebouncedAndIndexedAsOneBatch()
    {
        await StartAndSettleAsync();
        _files.SetExternal("new.md", NoteText.Simple("N", "New"));
        _files.SetExternal("other.md", NoteText.Simple("O", "Other"));

        _watcher.Raise(VaultFileEventKind.Created, "new.md");
        _watcher.Raise(VaultFileEventKind.Changed, "new.md");
        _watcher.Raise(VaultFileEventKind.Changed, @"other.md");
        _time.Advance(_debounce - TimeSpan.FromMilliseconds(1));
        _index.Notes.Should().BeEmpty("the quiet period has not elapsed yet");

        _time.Advance(TimeSpan.FromMilliseconds(1));
        await _session.WhenIdleAsync(Ct);

        _index.Notes.Select(note => note.Path.Value).Should().BeEquivalentTo("new.md", "other.md");
        var change = Changes().Should().ContainSingle().Subject;
        change.Source.Should().Be(NotesChangeSource.External);
        change.Paths.Select(path => path.Value).Should().BeEquivalentTo("new.md", "other.md");
        change.Affects(NotePath.Create("new.md")).Should().BeTrue();
        change.Affects(NotePath.Create("unrelated.md")).Should().BeFalse();
    }

    [Fact]
    public async Task ExternalDeleteAndRename_UpdateTheIndex()
    {
        var text = NoteText.Simple("A", "Alpha");
        _files.SetExternal("a.md", text);
        _files.SetExternal("b.md", NoteText.Simple("B", "Beta"));
        await StartAndSettleAsync();

        _files.DeleteExternal("a.md");
        _files.SetExternal("renamed.md", text);
        _files.DeleteExternal("b.md");
        _watcher.Raise(VaultFileEventKind.Renamed, "renamed.md", "a.md");
        _watcher.Raise(VaultFileEventKind.Deleted, "b.md");
        await _session.WhenIdleAsync(Ct);

        _index.Notes.Should().ContainSingle().Which.Should().Match<IndexedNote>(note => note.Path.Value == "renamed.md" && note.Id.Value == "A");
    }

    [Fact]
    public async Task ExternalChanges_OneUnreadableFile_DoesNotHideTheOthers_AndIsRetried()
    {
        _files.SetExternal("a.md", NoteText.Simple("A", "Alpha"));
        _files.SetExternal("b.md", NoteText.Simple("B", "Beta"));
        await StartAndSettleAsync();

        // Both notes change; a.md is still held open by the program that is writing it.
        _files.SetExternal("a.md", NoteText.Simple("A", "Alpha v2"));
        _files.SetExternal("b.md", NoteText.Simple("B", "Beta v2"));
        _files.MakeUnreadable("a.md");
        _watcher.Raise(VaultFileEventKind.Changed, "a.md");
        _watcher.Raise(VaultFileEventKind.Changed, "b.md");
        await _session.WhenIdleAsync(Ct);

        _index.Find("b.md")!.Metadata.Title.Should().Be("Beta v2", "the readable change of the batch is indexed");
        _index.Find("a.md")!.Metadata.Title.Should().Be("Alpha");
        Changes().Should().ContainSingle().Which.Paths.Select(path => path.Value).Should().BeEquivalentTo("a.md", "b.md");
        Statuses().Last().Should().Be(IndexStatus.Idle, "a busy file is not an index failure");

        _files.MakeReadable("a.md");
        await _session.WhenIdleAsync(Ct);

        _index.Find("a.md")!.Metadata.Title.Should().Be("Alpha v2", "the file is looked at again once the writer lets go");
    }

    [Fact]
    public async Task ExternalChange_OfAFileThatStaysUnreadable_IsRetriedAFewTimesOnly()
    {
        _files.SetExternal("a.md", NoteText.Simple("A", "Alpha"));
        await StartAndSettleAsync();
        _files.MakeUnreadable("a.md");
        var readsBefore = _files.ReadCount;

        _watcher.Raise(VaultFileEventKind.Changed, "a.md");
        for (var i = 0; i < VaultSession.MaxChangeAttempts + 3; i++)
        {
            await _session.WhenIdleAsync(Ct);
        }

        (_files.ReadCount - readsBefore).Should().Be(VaultSession.MaxChangeAttempts, "the next scan takes over; retrying forever would spin");
        _index.Find("a.md")!.Metadata.Title.Should().Be("Alpha");
    }

    [Fact]
    public async Task FailingSubscriber_DoesNotStopTheWorkerNorTheOtherSubscribers()
    {
        _session.Events.NotesChanged += (_, _) => throw new InvalidOperationException("subscriber bug");
        var laterSubscriberCalls = 0;
        _session.Events.NotesChanged += (_, _) => Interlocked.Increment(ref laterSubscriberCalls);
        _files.SetExternal("a.md", NoteText.Simple("A", "Alpha"));

        await _session.StartAsync(Ct);
        await _session.WhenIdleAsync(Ct);
        _files.SetExternal("b.md", NoteText.Simple("B", "Beta"));
        _watcher.Raise(VaultFileEventKind.Created, "b.md");
        await _session.WhenIdleAsync(Ct);

        _index.Notes.Should().HaveCount(2, "the indexing worker survived the failing subscriber");
        laterSubscriberCalls.Should().Be(2);
        Statuses().Last().Should().Be(IndexStatus.Idle);
    }

    [Theory]
    [InlineData(".git/index.md")]
    [InlineData("notes/.hidden/x.md")]
    [InlineData(@"notes\.a.md.1b2c.tmp")]
    [InlineData(".devnotes/trash/1/a.md")]
    public async Task HiddenPaths_AreIgnoredCompletely(string path)
    {
        await StartAndSettleAsync();

        _watcher.Raise(VaultFileEventKind.Created, path);
        _watcher.Raise(VaultFileEventKind.Deleted, path);
        await _session.WhenIdleAsync(Ct);

        Changes().Should().BeEmpty();
    }

    [Fact]
    public async Task ContentChangeOfNonNoteFile_IsIgnored_ButStructuralChangeTriggersRescan()
    {
        await StartAndSettleAsync();

        _watcher.Raise(VaultFileEventKind.Changed, "images/diagram.png");
        await _session.WhenIdleAsync(Ct);
        Changes().Should().BeEmpty();

        // A folder moved into the vault is reported as a single "created" event for the folder.
        _files.SetExternal("imported/one.md", NoteText.Simple("1", "One"));
        _files.SetExternal("imported/two.md", NoteText.Simple("2", "Two"));
        _watcher.Raise(VaultFileEventKind.Created, "imported");
        await _session.WhenIdleAsync(Ct);

        _index.Notes.Should().HaveCount(2);
        Changes().Should().ContainSingle().Which.AffectsEverything.Should().BeTrue();
    }

    [Fact]
    public async Task FolderRename_TriggersRescan()
    {
        _files.SetExternal("old/a.md", NoteText.Simple("A", "Alpha"));
        await StartAndSettleAsync();

        _files.DeleteExternal("old/a.md");
        _files.SetExternal("new/a.md", NoteText.Simple("A", "Alpha"));
        _watcher.Raise(VaultFileEventKind.Renamed, "new", "old");
        await _session.WhenIdleAsync(Ct);

        _index.Notes.Should().ContainSingle().Which.Path.Value.Should().Be("new/a.md");
    }

    [Fact]
    public async Task Overflow_OrTooManyBufferedChanges_FallBackToAScan()
    {
        await StartAndSettleAsync();
        for (var i = 0; i < 8; i++)
        {
            _files.SetExternal($"n{i}.md", NoteText.Simple($"ID-{i}", $"Note {i}"));
            _watcher.Raise(VaultFileEventKind.Created, $"n{i}.md"); // more than MaxBufferedChanges (5)
        }

        await _session.WhenIdleAsync(Ct);

        _index.Notes.Should().HaveCount(8);
        Changes().Should().ContainSingle().Which.Source.Should().Be(NotesChangeSource.Scan);

        _changes.Clear();
        _files.SetExternal("late.md", NoteText.Simple("LATE", "Late"));
        _watcher.Raise(VaultFileEventKind.Overflow, string.Empty);
        await _session.WhenIdleAsync(Ct);

        _index.Find("late.md").Should().NotBeNull();
        Changes().Should().ContainSingle().Which.Source.Should().Be(NotesChangeSource.Scan);
    }

    [Fact]
    public async Task RequestRebuild_ClearsAndReindexes()
    {
        _files.SetExternal("a.md", NoteText.Simple("A", "Alpha"));
        await StartAndSettleAsync();

        _session.RequestRebuild();
        await _session.WhenIdleAsync(Ct);

        _index.ClearCalls.Should().Be(1);
        _index.Notes.Should().ContainSingle();
    }

    [Fact]
    public async Task RequestSynchronize_WhileOneIsQueued_IsCoalesced()
    {
        // Nothing consumes the queue before the session starts, so these requests pile up deterministically.
        _session.RequestSynchronize();
        _session.RequestSynchronize();
        _session.RequestSynchronize();

        await _session.StartAsync(Ct); // also requests the initial scan
        await _session.WhenIdleAsync(Ct);

        Changes().Should().ContainSingle("queued scan requests collapse into one").Which.Source.Should().Be(NotesChangeSource.Scan);
    }

    [Fact]
    public async Task JobFailure_IsPublishedAndWorkerKeepsRunning()
    {
        await StartAndSettleAsync();
        _files.SetExternal("a.md", NoteText.Simple("A", "Alpha"));
        _index.FailNextMutation = new InvalidOperationException("database is locked");

        _watcher.Raise(VaultFileEventKind.Created, "a.md");
        await _session.WhenIdleAsync(Ct);

        Statuses().Should().Contain(status => status.State == IndexState.Failed && status.Error == "database is locked");
        _session.Events.Status.State.Should().Be(IndexState.Failed);

        _watcher.Raise(VaultFileEventKind.Changed, "a.md");
        await _session.WhenIdleAsync(Ct);

        _index.Notes.Should().ContainSingle("the worker must survive a failed job");
        _session.Events.Status.Should().Be(IndexStatus.Idle);
    }

    [Fact]
    public async Task NotesService_IsWiredToTheSameIndexAndEvents()
    {
        await StartAndSettleAsync();

        var created = await _session.Notes.CreateAsync(new("Hello world"), Ct);

        _index.Find(created.Path.Value).Should().NotBeNull();
        Changes().Should().ContainSingle().Which.Source.Should().Be(NotesChangeSource.Local);
        var result = await _session.Queries.QueryAsync("hello", NoteFilter.Empty, default, 10, Ct);
        result.Entries.Should().ContainSingle();
    }

    [Fact]
    public async Task DisposeAsync_StopsWatcherWorkerAndIndex_AndIsIdempotent()
    {
        await StartAndSettleAsync();

        await _session.DisposeAsync();
        await _session.DisposeAsync();

        _watcher.IsDisposed.Should().BeTrue();
        _watcher.SubscriberCount.Should().Be(0);
        _index.IsDisposed.Should().BeTrue();

        _watcher.Raise(VaultFileEventKind.Created, "after.md");
        _time.Advance(_debounce * 2);
        await _session.WhenIdleAsync(Ct); // must not hang
        await FluentActions.Invoking(() => _session.StartAsync(Ct)).Should().ThrowAsync<ObjectDisposedException>();
    }

    [Fact]
    public async Task DisposeAsync_BeforeStart_ReleasesResources()
    {
        await _session.DisposeAsync();

        _index.IsDisposed.Should().BeTrue();
        _watcher.IsDisposed.Should().BeTrue();
    }

    [Fact]
    public async Task Factory_ComposesASessionFromTheInfrastructurePorts()
    {
        var vault = new Vault(VaultId.Parse("vault-1"), "Work", Path.GetTempPath());
        var fileStores = Substitute.For<INoteFileStoreFactory>();
        fileStores.Create(vault.RootPath).Returns(_files);
        var indexes = new RecordingIndexFactory();
        var watchers = Substitute.For<IVaultWatcherFactory>();
        watchers.Create(vault.RootPath).Returns(new ManualVaultWatcher());
        var templates = Substitute.For<ITemplateStoreFactory>();
        templates.Create(vault.RootPath).Returns(new InMemoryTemplateStore());
        var factory = new VaultSessionFactory(
            fileStores, indexes, watchers, templates, new SequentialNoteIdGenerator(), _time, Options.Create(new IndexingOptions()), NullLoggerFactory.Instance);

        await using var session = factory.Create(vault);

        session.Vault.Should().Be(vault);
        indexes.Opened.Should().ContainKey(vault.Id);
    }

    [Fact]
    public void Factory_WatcherCreationFails_DisposesTheIndex()
    {
        var vault = new Vault(VaultId.Parse("vault-1"), "Work", Path.GetTempPath());
        var indexes = new RecordingIndexFactory();
        var watchers = Substitute.For<IVaultWatcherFactory>();
        watchers.Create(Arg.Any<string>()).Returns(_ => throw new DirectoryNotFoundException("vault folder is gone"));
        var factory = new VaultSessionFactory(
            Substitute.For<INoteFileStoreFactory>(), indexes, watchers, Substitute.For<ITemplateStoreFactory>(), new SequentialNoteIdGenerator(), _time,
            Options.Create(new IndexingOptions()), NullLoggerFactory.Instance);

        FluentActions.Invoking(() => factory.Create(vault)).Should().Throw<DirectoryNotFoundException>();

        indexes.Opened[vault.Id].IsDisposed.Should().BeTrue();
    }

    private VaultSession CreateSession(IndexingOptions options) =>
        new(
            new Vault(VaultId.Parse("vault-1"), "Test vault", Path.GetTempPath()),
            _files,
            _index,
            _watcher,
            new InMemoryTemplateStore(),
            new SequentialNoteIdGenerator(),
            _time,
            options,
            NullLoggerFactory.Instance);

    private async Task StartAndSettleAsync()
    {
        await _session.StartAsync(Ct);
        await _session.WhenIdleAsync(Ct);
        lock (_changes)
        {
            _changes.Clear();
        }

        lock (_statuses)
        {
            _statuses.Clear();
        }
    }

    private List<NotesChangedEventArgs> Changes()
    {
        lock (_changes)
        {
            return [.. _changes];
        }
    }

    private List<IndexStatus> Statuses()
    {
        lock (_statuses)
        {
            return [.. _statuses];
        }
    }
}
