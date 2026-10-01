using DevNotes.Application.Indexing;
using DevNotes.Application.Notes;
using DevNotes.Application.Settings;
using DevNotes.Application.Vaults;
using DevNotes.Desktop.Resources;
using DevNotes.Desktop.Services;
using DevNotes.Desktop.Tests.Support;
using DevNotes.Desktop.ViewModels;
using DevNotes.Domain.Notes;
using Microsoft.Extensions.Time.Testing;
using NSubstitute;
using NSubstitute.ExceptionExtensions;

namespace DevNotes.Desktop.Tests.ViewModels;

public sealed class NoteEditorViewModelTests : IDisposable
{
    private const string NoteText = "---\nid: ABC\ntitle: Nota de prueba\nupdated: 2026-09-30\n---\n\n# Uno\n\ntexto\n\n## Dos\n";

    private static readonly TimeSpan _autosaveDelay = TimeSpan.FromMilliseconds(1500);
    private static readonly NotePath _path = NotePath.Create("notes/prueba.md");

    private readonly INoteService _notes = Substitute.For<INoteService>();
    private readonly IVaultSession _session = Substitute.For<IVaultSession>();
    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 9, 30, 12, 0, 0, TimeSpan.Zero));
    private readonly RecordingNotifications _notifications = new();
    private readonly FakeClipboard _clipboard = new();
    private readonly FakeLinkOpener _links = new();
    private readonly FakeThemeService _theme = new();
    private readonly ImmediateUiDispatcher _dispatcher = new();
    private readonly NoteEditorViewModel _editor;

    public NoteEditorViewModelTests()
    {
        _time.SetLocalTimeZone(TimeZoneInfo.Utc);
        _session.Notes.Returns(_notes);
        _editor = new NoteEditorViewModel(
            _dispatcher,
            _notifications,
            _clipboard,
            _links,
            new TextMateCodeHighlighter(),
            _theme,
            _time,
            new EditorOptions { AutosaveDelayMilliseconds = 1500, PreviewDelayMilliseconds = 150 });

        // By default a save succeeds and writes exactly what it was given.
        _notes.SaveAsync(default, default!, default, default, default)
            .ReturnsForAnyArgs(call => Echo(call.ArgAt<NotePath>(0), call.ArgAt<string?>(1)));
    }

    public void Dispose() => _editor.Dispose();

    [Fact]
    public async Task OpenAsync_LoadsTextAndEverythingDerivedFromIt()
    {
        await OpenNoteAsync();

        _editor.HasNote.Should().BeTrue();
        _editor.Path.Should().Be(_path);
        _editor.Text.Should().Be(NoteText);
        _editor.Title.Should().Be("Nota de prueba");
        _editor.State.Should().Be(SaveState.Saved);
        _editor.IsDirty.Should().BeFalse();
        _editor.PreviewMarkdown.Should().Be("\n# Uno\n\ntexto\n\n## Dos\n", "the preview never shows the frontmatter");
        _editor.Outline.Should().Equal(new OutlineItemViewModel(1, "Uno", 6), new OutlineItemViewModel(2, "Dos", 10));
        _editor.Details!.Id.Should().Be("ABC");
        _editor.Details.Path.Should().Be("notes/prueba.md");
        _editor.DocumentVersion.Should().BePositive("loading a document tells the view to reset caret and undo history");
        _editor.FrontmatterWarning.Should().BeNull();
    }

    [Fact]
    public async Task OpenAsync_WithoutSession_OrMissingNote_ReturnsFalse()
    {
        (await _editor.OpenAsync(_path)).Should().BeFalse("no vault is open");

        await _editor.SetSessionAsync(_session);
        _notes.OpenAsync(_path, Arg.Any<CancellationToken>()).Returns((OpenedNote?)null);

        (await _editor.OpenAsync(_path)).Should().BeFalse();
        _notifications.Errors.Should().Equal(Strings.Error_NoteNotFound);
        _editor.HasNote.Should().BeFalse();
    }

    [Fact]
    public async Task OpenAsync_ReadFailure_IsReportedAndKeepsTheCurrentNote()
    {
        await OpenNoteAsync();
        var other = NotePath.Create("other.md");
        _notes.OpenAsync(other, Arg.Any<CancellationToken>()).ThrowsAsync(new IOException("sharing violation"));

        (await _editor.OpenAsync(other)).Should().BeFalse();

        _editor.Path.Should().Be(_path);
        _notifications.Errors.Should().ContainSingle().Which.Should().Contain("sharing violation");
    }

    [Fact]
    public async Task Typing_MarksTheNoteModified_AndAutosavesAfterTheQuietPeriod()
    {
        await OpenNoteAsync();

        _editor.Text = NoteText + "más texto\n";

        _editor.State.Should().Be(SaveState.Modified);
        _editor.IsDirty.Should().BeTrue();

        _time.Advance(_autosaveDelay - TimeSpan.FromMilliseconds(1));
        await _notes.DidNotReceiveWithAnyArgs().SaveAsync(default, default!, default, default, default);

        _time.Advance(TimeSpan.FromMilliseconds(1));

        await _notes.Received(1).SaveAsync(_path, NoteText + "más texto\n", ContentHash.Compute(NoteText), SaveMode.DetectConflicts, Arg.Any<CancellationToken>());
        _editor.State.Should().Be(SaveState.Saved);
        _editor.IsDirty.Should().BeFalse();
    }

    [Fact]
    public async Task Typing_KeepsPostponingTheAutosaveWhileTheUserIsStillTyping()
    {
        await OpenNoteAsync();

        for (var i = 0; i < 5; i++)
        {
            _editor.Text += "x";
            _time.Advance(TimeSpan.FromMilliseconds(1000));
        }

        await _notes.DidNotReceiveWithAnyArgs().SaveAsync(default, default!, default, default, default);

        _time.Advance(_autosaveDelay);

        await _notes.ReceivedWithAnyArgs(1).SaveAsync(default, default!, default, default, default);
    }

    [Fact]
    public async Task TypingBackToTheSavedText_IsNotDirty_AndDoesNotSave()
    {
        await OpenNoteAsync();

        _editor.Text = NoteText + "x";
        _editor.Text = NoteText;
        _time.Advance(_autosaveDelay);

        _editor.State.Should().Be(SaveState.Saved);
        await _notes.DidNotReceiveWithAnyArgs().SaveAsync(default, default!, default, default, default);
    }

    [Fact]
    public async Task Save_AdoptsTheTextThatWasActuallyWritten()
    {
        const string typed = "# Sin frontmatter\n";
        const string stamped = "---\nid: NEW-ID\nupdated: 2026-09-30\n---\n\n# Sin frontmatter\n";
        await OpenNoteAsync("plain.md", "# Antes\n");
        _notes.SaveAsync(default, default!, default, default, default)
            .ReturnsForAnyArgs(call => Echo(call.ArgAt<NotePath>(0), stamped));

        _editor.Text = typed;
        (await _editor.FlushAsync()).Should().BeTrue();

        _editor.Text.Should().Be(stamped, "the editor shows what is on disk, including the id the app added");
        _editor.IsDirty.Should().BeFalse();
        _editor.Details!.Id.Should().Be("NEW-ID");
    }

    [Fact]
    public async Task Save_WhileTheUserKeepsTyping_KeepsTheNewTextAndTheSameId()
    {
        const string stamped = "---\nid: NEW-ID\nupdated: 2026-09-30\n---\n\n# Título\n";
        await OpenNoteAsync("plain.md", "# Antes\n");
        var saving = new TaskCompletionSource<SaveResult>();
        _notes.SaveAsync(default, default!, default, default, default).ReturnsForAnyArgs(saving.Task);

        _editor.Text = "# Título\n";
        var flush = _editor.FlushAsync();
        _editor.State.Should().Be(SaveState.Saving);

        _editor.Text = "# Título\n\ntecleado durante el guardado\n";
        saving.SetResult(new SaveResult.Saved(Opened(NotePath.Create("plain.md"), stamped)));
        (await flush).Should().BeFalse("newer text is still unsaved");

        _editor.Text.Should().Be("---\nid: NEW-ID\nupdated: 2026-09-30\n---\n\n# Título\n\ntecleado durante el guardado\n");
        _editor.State.Should().Be(SaveState.Modified);
    }

    [Fact]
    public async Task Save_Conflict_NeverOverwrites_AndStopsAutosaving()
    {
        await OpenNoteAsync();
        _notes.SaveAsync(default, default!, default, SaveMode.DetectConflicts, default)
            .ReturnsForAnyArgs(new SaveResult.Conflict(SaveConflictKind.ModifiedOnDisk, null));

        _editor.Text = NoteText + "mío";
        (await _editor.FlushAsync()).Should().BeFalse();

        _editor.State.Should().Be(SaveState.Conflict);
        _editor.IsConflict.Should().BeTrue();
        _editor.ConflictMessage.Should().Be(Strings.Conflict_Modified);
        _editor.CanLoadDiskVersion.Should().BeTrue();
        _editor.Text.Should().Be(NoteText + "mío", "the user's text stays in the editor");

        _notes.ClearReceivedCalls();
        _editor.Text += " más";
        _time.Advance(_autosaveDelay * 2);

        _editor.State.Should().Be(SaveState.Conflict);
        await _notes.DidNotReceiveWithAnyArgs().SaveAsync(default, default!, default, default, default);
    }

    [Fact]
    public async Task Conflict_KeepMine_OverwritesExplicitly()
    {
        await EnterConflictAsync();
        _notes.SaveAsync(default, default!, default, default, default)
            .ReturnsForAnyArgs(call => Echo(call.ArgAt<NotePath>(0), call.ArgAt<string?>(1)));

        await _editor.KeepMineCommand.ExecuteAsync(null);

        await _notes.Received(1).SaveAsync(_path, NoteText + "mío", Arg.Any<ContentHash>(), SaveMode.Overwrite, Arg.Any<CancellationToken>());
        _editor.State.Should().Be(SaveState.Saved);
        _editor.Conflict.Should().BeNull();
    }

    [Fact]
    public async Task Conflict_LoadDisk_ReplacesTheBufferWithTheDiskVersion()
    {
        await EnterConflictAsync();
        var disk = NoteText + "versión externa";
        _notes.OpenAsync(_path, Arg.Any<CancellationToken>()).Returns(Opened(_path, disk));

        await _editor.LoadDiskCommand.ExecuteAsync(null);

        _editor.Text.Should().Be(disk);
        _editor.State.Should().Be(SaveState.Saved);
        _editor.IsDirty.Should().BeFalse();
    }

    [Fact]
    public async Task Conflict_SaveCopy_KeepsBothVersions()
    {
        await EnterConflictAsync();
        var disk = NoteText + "versión externa";
        _notes.OpenAsync(_path, Arg.Any<CancellationToken>()).Returns(Opened(_path, disk));
        _notes.CreateCopyAsync(_path, NoteText + "mío", Arg.Any<CancellationToken>())
            .Returns(Opened(NotePath.Create("notes/prueba-copy.md"), NoteText + "mío"));

        await _editor.SaveCopyCommand.ExecuteAsync(null);

        await _notes.Received(1).CreateCopyAsync(_path, NoteText + "mío", Arg.Any<CancellationToken>());
        _editor.Text.Should().Be(disk);
        _notifications.Shown.Should().ContainSingle().Which.Message.Should().Contain("notes/prueba-copy.md");
    }

    [Fact]
    public async Task Save_IoFailure_KeepsTheTextReportsItAndRetriesLater()
    {
        await OpenNoteAsync();
        _notes.SaveAsync(default, default!, default, default, default).ThrowsAsyncForAnyArgs(new IOException("disk full"));

        _editor.Text = NoteText + "importante";
        (await _editor.FlushAsync()).Should().BeFalse();

        _editor.State.Should().Be(SaveState.Failed);
        _editor.HasProblem.Should().BeTrue();
        _editor.Text.Should().Be(NoteText + "importante");
        _notifications.Errors.Should().ContainSingle().Which.Should().Contain("disk full");

        _notes.SaveAsync(default, default!, default, default, default)
            .ReturnsForAnyArgs(call => Echo(call.ArgAt<NotePath>(0), call.ArgAt<string?>(1)));
        (await _editor.FlushAsync()).Should().BeTrue("an explicit save retries");
        _editor.State.Should().Be(SaveState.Saved);
    }

    [Fact]
    public async Task FlushAsync_NothingToSave_DoesNotTouchTheDisk()
    {
        (await _editor.FlushAsync()).Should().BeTrue("no note is open");
        await OpenNoteAsync();

        (await _editor.FlushAsync()).Should().BeTrue();

        await _notes.DidNotReceiveWithAnyArgs().SaveAsync(default, default!, default, default, default);
    }

    [Fact]
    public async Task OpenAsync_AnotherNote_SavesTheCurrentOneFirst()
    {
        await OpenNoteAsync();
        var other = NotePath.Create("other.md");
        _notes.OpenAsync(other, Arg.Any<CancellationToken>()).Returns(Opened(other, "# Otra\n"));
        var noteChanges = 0;
        _editor.NoteChanged += (_, _) => noteChanges++;
        var version = _editor.DocumentVersion;

        _editor.Text = NoteText + "pendiente";
        (await _editor.OpenAsync(other)).Should().BeTrue();

        await _notes.Received(1).SaveAsync(_path, NoteText + "pendiente", Arg.Any<ContentHash>(), SaveMode.DetectConflicts, Arg.Any<CancellationToken>());
        _editor.Path.Should().Be(other);
        _editor.Text.Should().Be("# Otra\n");
        _editor.DocumentVersion.Should().Be(version + 1);
        noteChanges.Should().Be(1);
    }

    [Fact]
    public async Task OpenAsync_AnotherNote_IsRefusedWhileTheCurrentOneCannotBeSaved()
    {
        await EnterConflictAsync();

        (await _editor.OpenAsync(NotePath.Create("other.md"))).Should().BeFalse();

        _editor.Path.Should().Be(_path);
        _editor.Text.Should().Be(NoteText + "mío");
        _notifications.Errors.Should().Contain(Strings.Error_UnsavedChanges);
    }

    [Fact]
    public async Task OpenAsync_SameNote_IsANoOp_UnlessReloadIsRequested()
    {
        await OpenNoteAsync();
        _notes.ClearReceivedCalls();

        (await _editor.OpenAsync(_path)).Should().BeTrue();
        await _notes.DidNotReceiveWithAnyArgs().OpenAsync(default, default);

        (await _editor.OpenAsync(_path, reload: true)).Should().BeTrue();
        await _notes.Received(1).OpenAsync(_path, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ExternalChange_CleanBuffer_ReloadsSilently()
    {
        await OpenNoteAsync();
        var external = NoteText + "editado en VS Code\n";
        _notes.OpenAsync(_path, Arg.Any<CancellationToken>()).Returns(Opened(_path, external));
        var version = _editor.DocumentVersion;

        await _editor.HandleNotesChangedAsync(new NotesChangedEventArgs(NotesChangeSource.External, [_path]));

        _editor.Text.Should().Be(external);
        _editor.State.Should().Be(SaveState.Saved);
        _editor.DocumentVersion.Should().Be(version, "a reload is not a new document: the caret stays where it was");
        _editor.ReloadVersion.Should().Be(1, "the view is told to drop the undo history of the replaced version");
        _notifications.Shown.Should().ContainSingle().Which.Message.Should().Be(Strings.Notify_Reloaded);
    }

    [Fact]
    public async Task OpenAsync_Reload_KeepsTheDocumentAndFollowsTheNewPath()
    {
        // What rename and move do: the same note comes back under another path with slightly different text.
        await OpenNoteAsync();
        var renamed = NotePath.Create("notes/renombrada.md");
        _notes.OpenAsync(renamed, Arg.Any<CancellationToken>()).Returns(Opened(renamed, NoteText.Replace("Nota de prueba", "Renombrada", StringComparison.Ordinal)));
        var version = _editor.DocumentVersion;
        var noteChanges = 0;
        _editor.NoteChanged += (_, _) => noteChanges++;
        var detailsNotified = 0;
        _editor.PropertyChanged += (_, e) => detailsNotified += e.PropertyName == nameof(NoteEditorViewModel.Details) ? 1 : 0;

        (await _editor.OpenAsync(renamed, reload: true)).Should().BeTrue();

        _editor.Path.Should().Be(renamed);
        _editor.Title.Should().Be("Renombrada");
        _editor.DocumentVersion.Should().Be(version, "the caret must not jump to the top after renaming a note");
        _editor.ReloadVersion.Should().Be(1);
        _editor.Details!.Path.Should().Be("notes/renombrada.md");
        detailsNotified.Should().BePositive("the details panel shows the path");
        noteChanges.Should().Be(1);
    }

    [Fact]
    public async Task OpenAsync_WhileAnotherOpenIsInFlight_OnlyTheLatestRequestLoads()
    {
        // Holding an arrow key in the list asks for one note per row; reads can finish in any order.
        await OpenNoteAsync();
        var first = NotePath.Create("first.md");
        var second = NotePath.Create("second.md");
        var firstRead = new TaskCompletionSource<OpenedNote?>();
        var secondRead = new TaskCompletionSource<OpenedNote?>();
        _notes.OpenAsync(first, Arg.Any<CancellationToken>()).Returns(firstRead.Task);
        _notes.OpenAsync(second, Arg.Any<CancellationToken>()).Returns(secondRead.Task);

        var openFirst = _editor.OpenAsync(first);
        var openSecond = _editor.OpenAsync(second);
        secondRead.SetResult(Opened(second, "# Segunda\n"));
        firstRead.SetResult(Opened(first, "# Primera\n"));

        (await openSecond).Should().BeTrue();
        (await openFirst).Should().BeTrue("an overtaken request is not a failure: the note the user asked for last is open");
        _editor.Path.Should().Be(second);
        _editor.Text.Should().Be("# Segunda\n");
        _editor.IsReadOnly.Should().BeFalse();
    }

    [Fact]
    public async Task OpenAsync_IsReadOnlyWhileTheNextNoteIsBeingRead()
    {
        // Text typed in that gap would belong to the note that is being replaced and would be lost.
        await OpenNoteAsync();
        var other = NotePath.Create("other.md");
        var read = new TaskCompletionSource<OpenedNote?>();
        _notes.OpenAsync(other, Arg.Any<CancellationToken>()).Returns(read.Task);

        var opening = _editor.OpenAsync(other);

        _editor.IsReadOnly.Should().BeTrue();
        read.SetResult(Opened(other, "# Otra\n"));
        (await opening).Should().BeTrue();
        _editor.IsReadOnly.Should().BeFalse();
    }

    [Fact]
    public async Task SuspendEditing_NestsAndResumesWhenTheLastScopeEnds()
    {
        await OpenNoteAsync();

        var outer = _editor.SuspendEditing();
        var inner = _editor.SuspendEditing();
        _editor.IsReadOnly.Should().BeTrue();

        inner.Dispose();
        inner.Dispose();
        _editor.IsReadOnly.Should().BeTrue("the outer scope is still active; disposing twice counts once");

        outer.Dispose();
        _editor.IsReadOnly.Should().BeFalse();
    }

    [Fact]
    public async Task SetSessionAsync_WhenTheNoteCannotBeSaved_KeepsTheNoteAndReportsIt()
    {
        await EnterConflictAsync();

        (await _editor.SetSessionAsync(null)).Should().BeFalse();

        _editor.Path.Should().Be(_path);
        _editor.Text.Should().Be(NoteText + "mío", "closing or switching the vault must never drop text the user has not decided about");
        _editor.IsConflict.Should().BeTrue();
    }

    [Fact]
    public async Task Save_UnexpectedFailure_PropagatesButDoesNotLeaveTheNoteSavingForever()
    {
        await OpenNoteAsync();
        _notes.SaveAsync(default, default!, default, default, default).ThrowsAsyncForAnyArgs(new InvalidOperationException("a defect"));

        _editor.Text = NoteText + "texto";
        await _editor.FlushAsync();

        _editor.State.Should().Be(SaveState.Failed, "\"Saving…\" forever would hide external changes and block leaving the note");
        _editor.Text.Should().Be(NoteText + "texto");
        _notifications.Errors.Should().ContainSingle().Which.Should().Contain("a defect", "the failure is reported, never swallowed");
    }

    [Fact]
    public async Task OpenLink_RefusedByTheLinkPolicy_TellsTheUserWhy()
    {
        await _editor.OpenLinkCommand.ExecuteAsync("file:///C:/Windows/System32/calc.exe");

        _links.Opened.Should().ContainSingle();
        _notifications.Shown.Should().ContainSingle().Which.Message.Should().Be(Strings.Notify_LinkBlocked);
    }

    [Fact]
    public async Task ExternalChange_DirtyBuffer_ShowsConflictInsteadOfOverwritingEitherSide()
    {
        await OpenNoteAsync();
        _editor.Text = NoteText + "mío";
        _notes.OpenAsync(_path, Arg.Any<CancellationToken>()).Returns(Opened(_path, NoteText + "externo"));

        await _editor.HandleNotesChangedAsync(new NotesChangedEventArgs(NotesChangeSource.External, [_path]));

        _editor.State.Should().Be(SaveState.Conflict);
        _editor.Conflict.Should().Be(SaveConflictKind.ModifiedOnDisk);
        _editor.Text.Should().Be(NoteText + "mío");
        await _notes.DidNotReceiveWithAnyArgs().SaveAsync(default, default!, default, default, default);
    }

    [Fact]
    public async Task ExternalDelete_CleanBuffer_ClosesTheNote()
    {
        await OpenNoteAsync();
        _notes.OpenAsync(_path, Arg.Any<CancellationToken>()).Returns((OpenedNote?)null);

        await _editor.HandleNotesChangedAsync(new NotesChangedEventArgs(NotesChangeSource.External, [_path]));

        _editor.HasNote.Should().BeFalse();
        _editor.Text.Should().BeEmpty();
        _notifications.Shown.Should().ContainSingle().Which.Message.Should().Be(Strings.Notify_DeletedExternally);
    }

    [Fact]
    public async Task ExternalDelete_DirtyBuffer_KeepsTheTextAndOffersToRecreate()
    {
        await OpenNoteAsync();
        _editor.Text = NoteText + "mío";
        _notes.OpenAsync(_path, Arg.Any<CancellationToken>()).Returns((OpenedNote?)null);

        await _editor.HandleNotesChangedAsync(new NotesChangedEventArgs(NotesChangeSource.Scan, []));

        _editor.Conflict.Should().Be(SaveConflictKind.DeletedOnDisk);
        _editor.ConflictMessage.Should().Be(Strings.Conflict_Deleted);
        _editor.CanLoadDiskVersion.Should().BeFalse();
        _editor.Text.Should().Be(NoteText + "mío");
    }

    [Fact]
    public async Task NotesChanged_ForOtherNotesOrOwnSaves_DoesNothing()
    {
        await OpenNoteAsync();
        _notes.ClearReceivedCalls();

        await _editor.HandleNotesChangedAsync(new NotesChangedEventArgs(NotesChangeSource.External, [NotePath.Create("unrelated.md")]));
        await _notes.DidNotReceiveWithAnyArgs().OpenAsync(default, default);

        // Own save: the disk hash equals the hash the editor already has.
        await _editor.HandleNotesChangedAsync(new NotesChangedEventArgs(NotesChangeSource.Local, [_path]));
        _editor.State.Should().Be(SaveState.Saved);
        _notifications.Shown.Should().BeEmpty();
    }

    [Fact]
    public async Task CheckDiskAsync_TransientReadFailure_IsIgnoredUntilTheNextNotification()
    {
        await OpenNoteAsync();
        _notes.OpenAsync(_path, Arg.Any<CancellationToken>()).ThrowsAsync(new IOException("locked by another process"));

        await _editor.CheckDiskAsync();

        _editor.State.Should().Be(SaveState.Saved);
        _editor.Text.Should().Be(NoteText);
        _notifications.Shown.Should().BeEmpty();
    }

    [Fact]
    public async Task DerivedState_IsRefreshedAfterTheUserStopsTyping()
    {
        await OpenNoteAsync();

        _editor.Text = NoteText + "\n### Tres\n";
        _editor.Outline.Should().HaveCount(2, "the outline waits for the quiet period");

        _time.Advance(TimeSpan.FromMilliseconds(150));
        await WaitForAsync(() => _editor.Outline.Count == 3);

        _editor.Outline[2].Should().Be(new OutlineItemViewModel(3, "Tres", 12));
        _editor.PreviewMarkdown.Should().EndWith("### Tres\n");
    }

    [Fact]
    public async Task DerivedState_OfTextThatWasReplaced_NeverOverwritesTheLoadedNote()
    {
        await OpenNoteAsync();

        // Long enough for its parse to be still running in the background when the disk version is loaded.
        _editor.Text = NoteText + string.Concat(Enumerable.Repeat("\n### Más\n\ntexto\n", 50_000));
        _time.Advance(TimeSpan.FromMilliseconds(150));
        await _editor.LoadDiskCommand.ExecuteAsync(null);
        await _dispatcher.WhenIdleAsync();

        _editor.Text.Should().Be(NoteText);
        _editor.Outline.Should().HaveCount(2, "the parse of the dropped text finished after the note was loaded");
        _editor.PreviewMarkdown.Should().Be("\n# Uno\n\ntexto\n\n## Dos\n");
    }

    [Fact]
    public async Task InvalidFrontmatter_ShowsAWarning()
    {
        await OpenNoteAsync("broken.md", "---\ntitle: [oops\n---\n# Texto\n");

        _editor.HasFrontmatterWarning.Should().BeTrue();
        _editor.FrontmatterWarning.Should().Contain("YAML");
        _editor.Title.Should().Be("Texto");
    }

    [Fact]
    public async Task ViewMode_CyclesAndExposesPaneVisibility()
    {
        await OpenNoteAsync();
        _editor.ViewMode.Should().Be(EditorViewMode.Split);
        (_editor.IsEditorVisible, _editor.IsPreviewVisible, _editor.IsSplit).Should().Be((true, true, true));

        _editor.CycleViewModeCommand.Execute(null);
        _editor.ViewMode.Should().Be(EditorViewMode.Preview);
        (_editor.IsEditorVisible, _editor.IsPreviewVisible, _editor.IsPreviewOnly).Should().Be((false, true, true));

        _editor.CycleViewModeCommand.Execute(null);
        _editor.ViewMode.Should().Be(EditorViewMode.Editor);
        (_editor.IsEditorVisible, _editor.IsPreviewVisible, _editor.IsEditorOnly).Should().Be((true, false, true));

        _editor.SetViewModeCommand.Execute(EditorViewMode.Split);
        _editor.ViewMode.Should().Be(EditorViewMode.Split);
    }

    [Fact]
    public async Task GoToHeading_RaisesNavigation_AndMakesTheEditorVisible()
    {
        await OpenNoteAsync();
        _editor.ViewMode = EditorViewMode.Preview;
        int? line = null;
        _editor.NavigateRequested += (_, requested) => line = requested;

        _editor.GoToHeadingCommand.Execute(_editor.Outline[1]);

        line.Should().Be(10);
        _editor.ViewMode.Should().Be(EditorViewMode.Split);
    }

    [Fact]
    public async Task CopyCodeAndOpenLink_GoThroughThePlatformServices()
    {
        await _editor.CopyCodeCommand.ExecuteAsync("SELECT 1;");
        await _editor.OpenLinkCommand.ExecuteAsync("https://example.com");
        await _editor.CopyCodeCommand.ExecuteAsync(string.Empty);

        _clipboard.Text.Should().Be("SELECT 1;");
        _links.Opened.Should().Equal("https://example.com");
        _notifications.Shown.Should().ContainSingle().Which.Message.Should().Be(Strings.Notify_CodeCopied);
    }

    [Fact]
    public async Task CloseAndSessionChanges_SaveFirst()
    {
        await OpenNoteAsync();
        _editor.Text = NoteText + "pendiente";

        (await _editor.CloseAsync()).Should().BeTrue();

        await _notes.ReceivedWithAnyArgs(1).SaveAsync(default, default!, default, default, default);
        _editor.HasNote.Should().BeFalse();

        await OpenNoteAsync();
        _editor.Text = NoteText + "otra vez";
        await _editor.SetSessionAsync(null);

        await _notes.ReceivedWithAnyArgs(2).SaveAsync(default, default!, default, default, default);
        _editor.HasNote.Should().BeFalse();
        (await _editor.OpenAsync(_path)).Should().BeFalse("the vault was closed");
    }

    [Fact]
    public async Task CloseWithoutSaving_DropsTheBuffer()
    {
        await OpenNoteAsync();
        _editor.Text = NoteText + "descartado";

        _editor.CloseWithoutSaving();
        _time.Advance(_autosaveDelay * 2);

        _editor.HasNote.Should().BeFalse();
        await _notes.DidNotReceiveWithAnyArgs().SaveAsync(default, default!, default, default, default);
    }

    [Fact]
    public void ThemeChange_IsReflectedForTheCodeHighlighter()
    {
        _editor.IsDarkTheme.Should().BeTrue();

        _theme.Apply(new AppSettings { Theme = AppTheme.Light });

        _editor.IsDarkTheme.Should().BeFalse();
    }

    [Theory]
    [InlineData(SaveState.Saved)]
    [InlineData(SaveState.Modified)]
    public async Task StateText_IsLocalized(SaveState state)
    {
        await OpenNoteAsync();
        if (state == SaveState.Modified)
        {
            _editor.Text += "x";
        }

        _editor.StateText.Should().Be(state == SaveState.Saved ? Strings.Save_Saved : Strings.Save_Modified);
    }

    private async Task OpenNoteAsync(string path = "notes/prueba.md", string text = NoteText)
    {
        var notePath = NotePath.Create(path);
        await _editor.SetSessionAsync(_session);
        _notes.OpenAsync(notePath, Arg.Any<CancellationToken>()).Returns(Opened(notePath, text));
        (await _editor.OpenAsync(notePath)).Should().BeTrue();
        _notifications.Shown.Clear();
    }

    private async Task EnterConflictAsync()
    {
        await OpenNoteAsync();
        _notes.SaveAsync(default, default!, default, default, default)
            .ReturnsForAnyArgs(new SaveResult.Conflict(SaveConflictKind.ModifiedOnDisk, null));
        _editor.Text = NoteText + "mío";
        await _editor.FlushAsync();
        _editor.State.Should().Be(SaveState.Conflict);
        _notes.ClearReceivedCalls();
        _notifications.Shown.Clear();
    }

    /// <summary>A successful save that wrote exactly the given text.</summary>
    private static SaveResult Echo(NotePath path, string? text) =>
        new SaveResult.Saved(Opened(path.IsEmpty ? _path : path, text ?? string.Empty));

    private static OpenedNote Opened(NotePath path, string text) =>
        new(path, text, ContentHash.Compute(text), NoteDocumentParser.Parse(text, path.FileNameWithoutExtension));

    private static async Task WaitForAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
        while (!condition())
        {
            if (DateTime.UtcNow > deadline)
            {
                throw new Xunit.Sdk.XunitException("The expected state was not reached in time.");
            }

            await Task.Delay(10, TestContext.Current.CancellationToken);
        }
    }
}
