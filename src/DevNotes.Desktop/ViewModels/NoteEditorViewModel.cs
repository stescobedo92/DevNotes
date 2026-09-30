using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DevNotes.Application.Common;
using DevNotes.Application.Indexing;
using DevNotes.Application.Notes;
using DevNotes.Application.Settings;
using DevNotes.Application.Vaults;
using DevNotes.Desktop.Resources;
using DevNotes.Desktop.Services;
using DevNotes.Domain.Notes;

namespace DevNotes.Desktop.ViewModels;

public enum SaveState
{
    Saved,
    Modified,
    Saving,
    Failed,
    Conflict,
}

/// <summary>One entry of the table of contents.</summary>
/// <param name="Level">Heading level (1–6).</param>
/// <param name="Text">Heading text.</param>
/// <param name="Line">Zero-based line of the heading in the editor text.</param>
public sealed record OutlineItemViewModel(int Level, string Text, int Line);

/// <summary>
/// The note being edited: text buffer, autosave with conflict detection, and the state derived
/// from the text (preview, metadata, outline).
/// <para>All members must be used from the UI thread.</para>
/// </summary>
public sealed partial class NoteEditorViewModel : ObservableObject, IDisposable
{
    private readonly IUiDispatcher _dispatcher;
    private readonly INotificationService _notifications;
    private readonly IClipboardService _clipboard;
    private readonly ILinkOpener _links;
    private readonly IThemeService _theme;
    private readonly TimeProvider _timeProvider;
    private readonly Debouncer _autosave;
    private readonly Debouncer _derived;

    private IVaultSession? _session;
    private ContentHash _baseHash;
    private string _savedText = string.Empty;
    private bool _loading;
    private int _derivedVersion;
    private int _openRequest;
    private int _editingSuspensions;

    public NoteEditorViewModel(
        IUiDispatcher dispatcher,
        INotificationService notifications,
        IClipboardService clipboard,
        ILinkOpener links,
        ICodeHighlighter highlighter,
        IThemeService theme,
        TimeProvider timeProvider,
        EditorOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        _dispatcher = dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));
        _notifications = notifications ?? throw new ArgumentNullException(nameof(notifications));
        _clipboard = clipboard ?? throw new ArgumentNullException(nameof(clipboard));
        _links = links ?? throw new ArgumentNullException(nameof(links));
        _theme = theme ?? throw new ArgumentNullException(nameof(theme));
        _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
        Highlighter = highlighter ?? throw new ArgumentNullException(nameof(highlighter));

        Text = string.Empty;
        Title = string.Empty;
        PreviewMarkdown = string.Empty;
        Outline = [];
        IsDarkTheme = theme.IsDark;
        _theme.AppearanceChanged += OnAppearanceChanged;

        // Debouncers may fire on a timer thread; the work always hops to the UI thread.
        _autosave = new Debouncer(
            timeProvider,
            options.AutosaveDelay,
            _ => _dispatcher.InvokeAsync(() => SaveCoreAsync(SaveMode.DetectConflicts)),
            ReportBackgroundFailure);
        _derived = new Debouncer(timeProvider, options.PreviewDelay, _ => _dispatcher.InvokeAsync(RefreshDerivedAsync), ReportBackgroundFailure);
    }

    /// <summary>Raised when the view should scroll the editor to a line (table of contents).</summary>
    public event EventHandler<int>? NavigateRequested;

    /// <summary>Raised when the view should move keyboard focus into the editor.</summary>
    public event EventHandler? FocusRequested;

    /// <summary>Raised after a different note (or none) became the current one.</summary>
    public event EventHandler? NoteChanged;

    public ICodeHighlighter Highlighter { get; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasNote), nameof(Details))]
    public partial NotePath? Path { get; private set; }

    [ObservableProperty]
    public partial string Title { get; private set; }

    /// <summary>Full text of the note file (frontmatter included), bound to the editor.</summary>
    [ObservableProperty]
    public partial string Text { get; set; }

    /// <summary>Incremented when a different document is loaded, so the view resets caret and undo history.</summary>
    [ObservableProperty]
    public partial int DocumentVersion { get; private set; }

    /// <summary>
    /// Incremented when the text of the open note was replaced by the version on disk. The view drops
    /// its undo history: undoing past that point would bring back text based on a version that no
    /// longer exists and the next save would silently overwrite the change made outside the app.
    /// </summary>
    [ObservableProperty]
    public partial int ReloadVersion { get; private set; }

    /// <summary>
    /// True while the note is being switched, renamed, moved or the app is closing. Whatever was typed
    /// in those moments would be written to the wrong place or lost, so the editor does not accept input.
    /// </summary>
    [ObservableProperty]
    public partial bool IsReadOnly { get; private set; }

    /// <summary>
    /// True while <see cref="Text"/> is being replaced by another document; the view then waits for
    /// <see cref="DocumentVersion"/> and loads the document once instead of patching the old one first.
    /// </summary>
    public bool IsReplacingDocument { get; private set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StateText), nameof(IsConflict), nameof(HasProblem))]
    public partial SaveState State { get; private set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsEditorVisible), nameof(IsPreviewVisible), nameof(IsSplit), nameof(IsEditorOnly), nameof(IsPreviewOnly))]
    public partial EditorViewMode ViewMode { get; set; }

    /// <summary>Markdown body shown by the preview (frontmatter removed).</summary>
    [ObservableProperty]
    public partial string PreviewMarkdown { get; private set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Details))]
    public partial NoteMetadata? Metadata { get; private set; }

    /// <summary>Metadata formatted for the details panel; null when no note is open.</summary>
    public NoteDetailsViewModel? Details => Metadata is { } metadata && Path is { } path ? new NoteDetailsViewModel(metadata, path) : null;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasFrontmatterWarning))]
    public partial string? FrontmatterWarning { get; private set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasOutline))]
    public partial IReadOnlyList<OutlineItemViewModel> Outline { get; private set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ConflictMessage), nameof(CanLoadDiskVersion))]
    public partial SaveConflictKind? Conflict { get; private set; }

    [ObservableProperty]
    public partial bool IsDarkTheme { get; private set; }

    public bool HasNote => Path is not null;

    public bool HasOutline => Outline.Count > 0;

    public bool HasFrontmatterWarning => FrontmatterWarning is not null;

    public bool IsConflict => State == SaveState.Conflict;

    public bool HasProblem => State is SaveState.Failed or SaveState.Conflict;

    public bool IsEditorVisible => ViewMode != EditorViewMode.Preview;

    public bool IsPreviewVisible => ViewMode != EditorViewMode.Editor;

    public bool IsSplit => ViewMode == EditorViewMode.Split;

    public bool IsEditorOnly => ViewMode == EditorViewMode.Editor;

    public bool IsPreviewOnly => ViewMode == EditorViewMode.Preview;

    /// <summary>True while the buffer differs from what is known to be on disk.</summary>
    public bool IsDirty => Path is not null && !string.Equals(Text, _savedText, StringComparison.Ordinal);

    public bool CanLoadDiskVersion => Conflict == SaveConflictKind.ModifiedOnDisk;

    public string StateText => State switch
    {
        SaveState.Modified => Strings.Save_Modified,
        SaveState.Saving => Strings.Save_Saving,
        SaveState.Failed => Strings.Save_Failed,
        SaveState.Conflict => Strings.Save_Conflict,
        _ => Strings.Save_Saved,
    };

    public string? ConflictMessage => Conflict switch
    {
        SaveConflictKind.ModifiedOnDisk => Strings.Conflict_Modified,
        SaveConflictKind.DeletedOnDisk => Strings.Conflict_Deleted,
        _ => null,
    };

    private DateOnly Today => DateOnly.FromDateTime(_timeProvider.GetLocalNow().DateTime);

    /// <summary>
    /// Switches to another vault session. Pending changes of the current note are saved first; when
    /// they cannot be saved nothing changes and false is returned, so the text is never dropped.
    /// </summary>
    public async Task<bool> SetSessionAsync(IVaultSession? session)
    {
        if (_session is not null && !await FlushAsync())
        {
            return false;
        }

        _openRequest++;
        Clear();
        _session = session;
        return true;
    }

    /// <summary>Makes the editor read-only until the returned scope is disposed. Scopes can be nested.</summary>
    public IDisposable SuspendEditing()
    {
        _editingSuspensions++;
        IsReadOnly = true;
        return new EditingSuspension(this);
    }

    /// <summary>
    /// Opens a note. Returns false, leaving the current note in place, when the current note has
    /// changes that could not be saved or the requested note cannot be read.
    /// </summary>
    /// <remarks>
    /// Requests may overlap (holding an arrow key in the list issues one per row): only the latest one
    /// loads its note. A request that was overtaken also returns true, because the note that ends up
    /// open is the one the user asked for last.
    /// </remarks>
    public async Task<bool> OpenAsync(NotePath path, bool reload = false)
    {
        if (_session is not { } session)
        {
            return false;
        }

        var request = ++_openRequest;
        if (!reload && Path == path)
        {
            return true;
        }

        // No typing between the flush and the load: that text would belong to a note that is being replaced.
        using var editing = SuspendEditing();
        if (!await FlushAsync())
        {
            _notifications.Show(Strings.Error_UnsavedChanges, NotificationKind.Error);
            return false;
        }

        if (request != _openRequest)
        {
            return true;
        }

        OpenedNote? opened;
        try
        {
            opened = await session.Notes.OpenAsync(path, CancellationToken.None);
        }
        catch (Exception exception) when (ErrorMessages.IsExpected(exception))
        {
            _notifications.Show(ErrorMessages.Describe(exception), NotificationKind.Error);
            return false;
        }

        if (request != _openRequest || !ReferenceEquals(session, _session))
        {
            return true;
        }

        if (opened is null)
        {
            _notifications.Show(Strings.Error_NoteNotFound, NotificationKind.Error);
            return false;
        }

        // A reload is the same note after a rename or a move: the caret stays where the user left it.
        Load(opened, isNewDocument: !reload);
        return true;
    }

    /// <summary>Saves pending changes now. Returns true when nothing is left unsaved.</summary>
    public async Task<bool> FlushAsync()
    {
        if (Path is null)
        {
            return true;
        }

        if (IsDirty && State != SaveState.Conflict)
        {
            _autosave.Signal();
        }

        await _autosave.FlushAsync();
        return !IsDirty && State == SaveState.Saved;
    }

    /// <summary>Closes the current note after saving it. Returns false when it could not be saved.</summary>
    public async Task<bool> CloseAsync()
    {
        if (!await FlushAsync())
        {
            return false;
        }

        Clear();
        return true;
    }

    /// <summary>Reacts to changes reported by the vault (watcher, scans, other operations).</summary>
    public async Task HandleNotesChangedAsync(NotesChangedEventArgs change)
    {
        ArgumentNullException.ThrowIfNull(change);

        // While a save is in flight its own result is authoritative (it detects conflicts itself).
        if (Path is { } path && change.Affects(path) && State != SaveState.Saving)
        {
            await CheckDiskAsync();
        }
    }

    /// <summary>
    /// Compares the note with the file on disk. An unmodified buffer follows the disk silently; a
    /// modified one is never overwritten and the conflict is shown instead.
    /// </summary>
    public async Task CheckDiskAsync()
    {
        if (_session is not { } session || Path is not { } path)
        {
            return;
        }

        OpenedNote? disk;
        try
        {
            disk = await session.Notes.OpenAsync(path, CancellationToken.None);
        }
        catch (Exception exception) when (ErrorMessages.IsExpected(exception))
        {
            return; // Typically another program still holds the file; the next notification retries.
        }

        if (Path != path || State == SaveState.Saving)
        {
            return;
        }

        if (disk is null)
        {
            if (IsDirty)
            {
                EnterConflict(SaveConflictKind.DeletedOnDisk);
            }
            else
            {
                Clear();
                _notifications.Show(Strings.Notify_DeletedExternally);
            }

            return;
        }

        if (disk.Hash == _baseHash)
        {
            return;
        }

        if (IsDirty)
        {
            EnterConflict(SaveConflictKind.ModifiedOnDisk);
        }
        else
        {
            Load(disk, isNewDocument: false);
            _notifications.Show(Strings.Notify_Reloaded);
        }
    }

    /// <summary>Closes the current note dropping whatever is unsaved. Only for flows the user confirmed.</summary>
    public void CloseWithoutSaving() => Clear();

    public void RequestFocus() => FocusRequested?.Invoke(this, EventArgs.Empty);

    public void Dispose()
    {
        _theme.AppearanceChanged -= OnAppearanceChanged;
        _autosave.Dispose();
        _derived.Dispose();
    }

    partial void OnTextChanged(string value)
    {
        if (_loading || Path is null)
        {
            return;
        }

        if (State != SaveState.Conflict)
        {
            State = IsDirty ? SaveState.Modified : SaveState.Saved;
            if (IsDirty)
            {
                _autosave.Signal();
            }
        }

        _derived.Signal();
    }

    [RelayCommand]
    private async Task SaveAsync() => await FlushAsync();

    /// <summary>Conflict resolution: the user's text wins and replaces the file on disk.</summary>
    [RelayCommand]
    private Task KeepMineAsync() => SaveCoreAsync(SaveMode.Overwrite);

    /// <summary>Conflict resolution: the file on disk wins and the user's changes are dropped.</summary>
    [RelayCommand]
    private async Task LoadDiskAsync()
    {
        if (_session is not { } session || Path is not { } path)
        {
            return;
        }

        try
        {
            var disk = await session.Notes.OpenAsync(path, CancellationToken.None);
            if (disk is null)
            {
                Clear();
            }
            else
            {
                Load(disk, isNewDocument: false);
            }
        }
        catch (Exception exception) when (ErrorMessages.IsExpected(exception))
        {
            _notifications.Show(ErrorMessages.Describe(exception), NotificationKind.Error);
        }
    }

    /// <summary>Conflict resolution: keeps both by saving the user's text as a separate note.</summary>
    [RelayCommand]
    private async Task SaveCopyAsync()
    {
        if (_session is not { } session || Path is not { } path)
        {
            return;
        }

        try
        {
            var copy = await session.Notes.CreateCopyAsync(path, Text, CancellationToken.None);
            _notifications.Show(ErrorMessages.Format(Strings.Notify_CopySaved, copy.Path.Value));
        }
        catch (Exception exception) when (ErrorMessages.IsExpected(exception))
        {
            _notifications.Show(ErrorMessages.Describe(exception), NotificationKind.Error);
            return;
        }

        await LoadDiskAsync();
    }

    [RelayCommand]
    private void SetViewMode(EditorViewMode mode) => ViewMode = mode;

    /// <summary>Editor → split → preview → editor.</summary>
    [RelayCommand]
    private void CycleViewMode() => ViewMode = ViewMode switch
    {
        EditorViewMode.Editor => EditorViewMode.Split,
        EditorViewMode.Split => EditorViewMode.Preview,
        _ => EditorViewMode.Editor,
    };

    [RelayCommand]
    private void GoToHeading(OutlineItemViewModel? heading)
    {
        if (heading is null)
        {
            return;
        }

        if (ViewMode == EditorViewMode.Preview)
        {
            ViewMode = EditorViewMode.Split; // The heading is located in the editor, so it has to be visible.
        }

        NavigateRequested?.Invoke(this, heading.Line);
    }

    [RelayCommand]
    private async Task CopyCodeAsync(string? code)
    {
        if (!string.IsNullOrEmpty(code))
        {
            await _clipboard.SetTextAsync(code);
            _notifications.Show(Strings.Notify_CodeCopied);
        }
    }

    [RelayCommand]
    private async Task OpenLinkAsync(string? url)
    {
        if (!await _links.OpenAsync(url))
        {
            _notifications.Show(Strings.Notify_LinkBlocked);
        }
    }

    private async Task SaveCoreAsync(SaveMode mode)
    {
        if (_session is not { } session || Path is not { } path)
        {
            return;
        }

        if (mode == SaveMode.DetectConflicts && (State == SaveState.Conflict || !IsDirty))
        {
            return;
        }

        var text = Text;
        State = SaveState.Saving;
        try
        {
            var result = await session.Notes.SaveAsync(path, text, _baseHash, mode, CancellationToken.None);
            if (Path != path)
            {
                return;
            }

            switch (result)
            {
                case SaveResult.Saved saved:
                    ApplySaved(saved.Note, text);
                    break;
                case SaveResult.Conflict conflict:
                    EnterConflict(conflict.Kind);
                    break;
                default:
                    throw new InvalidOperationException($"Unknown save result '{result.GetType().Name}'.");
            }
        }
        catch (Exception exception) when (ErrorMessages.IsExpected(exception))
        {
            // The text stays in the buffer and the state says so; the next change or Ctrl+S retries.
            State = SaveState.Failed;
            _notifications.Show(ErrorMessages.Format(Strings.Error_SaveFailed, ErrorMessages.Describe(exception)), NotificationKind.Error);
        }
        finally
        {
            // An unexpected exception is a defect and keeps propagating, but it must not leave the note
            // "saving" forever: that state hides external changes and blocks leaving the note.
            if (State == SaveState.Saving && Path == path)
            {
                State = SaveState.Failed;
            }
        }
    }

    private void ApplySaved(OpenedNote saved, string textAtSave)
    {
        _baseHash = saved.Hash;
        _savedText = saved.Text;
        Conflict = null;

        if (string.Equals(Text, textAtSave, StringComparison.Ordinal))
        {
            // Adopt what was actually written (the frontmatter may have received an id / date).
            SetTextSilently(saved.Text);
        }
        else if (saved.Document.Metadata.Id is { } id)
        {
            // The user kept typing during the save: carry the same stamp over to the newer text so
            // the note keeps one stable id instead of getting another one on the next save.
            var restamped = NoteStamper.Stamp(Text, saved.Path.FileNameWithoutExtension, new FixedNoteIdGenerator(id), Today);
            SetTextSilently(restamped.Text);
        }

        State = IsDirty ? SaveState.Modified : SaveState.Saved;
        if (IsDirty)
        {
            _autosave.Signal();
            _derived.Signal();
        }
        else
        {
            // The buffer is exactly what was written and already parsed: show its metadata right away.
            _derived.Cancel();
            ApplyDerived(saved.Document, saved.Text);
        }
    }

    private void EnterConflict(SaveConflictKind kind)
    {
        _autosave.Cancel();
        Conflict = kind;
        State = SaveState.Conflict;
    }

    private void Load(OpenedNote note, bool isNewDocument)
    {
        _autosave.Cancel();
        _derived.Cancel();
        _loading = true;
        IsReplacingDocument = isNewDocument;
        try
        {
            var changedNote = Path != note.Path;
            Path = note.Path;
            _savedText = note.Text;
            _baseHash = note.Hash;
            Text = note.Text;
            Conflict = null;
            State = SaveState.Saved;
            ApplyDerived(note.Document, note.Text);
            if (isNewDocument)
            {
                DocumentVersion++;
            }
            else
            {
                ReloadVersion++;
            }

            if (changedNote)
            {
                NoteChanged?.Invoke(this, EventArgs.Empty);
            }
        }
        finally
        {
            _loading = false;
            IsReplacingDocument = false;
        }
    }

    private void Clear()
    {
        _autosave.Cancel();
        _derived.Cancel();
        _loading = true;
        IsReplacingDocument = true;
        try
        {
            var hadNote = Path is not null;
            Path = null;
            _savedText = string.Empty;
            _baseHash = default;
            Text = string.Empty;
            Title = string.Empty;
            PreviewMarkdown = string.Empty;
            Metadata = null;
            FrontmatterWarning = null;
            Outline = [];
            Conflict = null;
            State = SaveState.Saved;
            DocumentVersion++;
            if (hadNote)
            {
                NoteChanged?.Invoke(this, EventArgs.Empty);
            }
        }
        finally
        {
            _loading = false;
            IsReplacingDocument = false;
        }
    }

    private void SetTextSilently(string text)
    {
        if (string.Equals(Text, text, StringComparison.Ordinal))
        {
            return;
        }

        _loading = true;
        try
        {
            Text = text;
        }
        finally
        {
            _loading = false;
        }
    }

    private async Task RefreshDerivedAsync()
    {
        if (Path is not { } path)
        {
            return;
        }

        var text = Text;
        var version = ++_derivedVersion;

        // Parsing is pure CPU work: keep it off the UI thread so typing stays smooth in long notes.
        var (document, outline) = await Task.Run(() =>
        {
            var parsed = NoteDocumentParser.Parse(text, path.FileNameWithoutExtension);
            return (parsed, BuildOutline(parsed, text));
        });

        if (version == _derivedVersion && Path == path)
        {
            ApplyDerived(document, outline);
        }
    }

    private void ApplyDerived(NoteDocument document, string text) => ApplyDerived(document, BuildOutline(document, text));

    private void ApplyDerived(NoteDocument document, IReadOnlyList<OutlineItemViewModel> outline)
    {
        Title = document.Metadata.Title;
        Metadata = document.Metadata;
        FrontmatterWarning = document.FrontmatterStatus == FrontmatterStatus.Invalid
            ? ErrorMessages.Format(Strings.Frontmatter_Invalid, document.FrontmatterError)
            : null;
        Outline = outline;
        PreviewMarkdown = document.Body;
    }

    private static List<OutlineItemViewModel> BuildOutline(NoteDocument document, string text)
    {
        // Headings are located inside the body; the editor shows the whole file, so shift by the frontmatter lines.
        var offset = Math.Min(document.BodyOffset, text.Length);
        var lineOffset = text.AsSpan(0, offset).Count('\n');
        return [.. MarkdownOutline.Extract(document.Body).Select(heading => new OutlineItemViewModel(heading.Level, heading.Text, heading.Line + lineOffset))];
    }

    private void OnAppearanceChanged(object? sender, EventArgs e) => IsDarkTheme = _theme.IsDark;

    private void ReportBackgroundFailure(Exception exception) =>
        _notifications.Show(ErrorMessages.Describe(exception), NotificationKind.Error);

    private void ResumeEditing()
    {
        if (--_editingSuspensions == 0)
        {
            IsReadOnly = false;
        }
    }

    private sealed class EditingSuspension(NoteEditorViewModel owner) : IDisposable
    {
        private bool _disposed;

        public void Dispose()
        {
            if (!_disposed)
            {
                _disposed = true;
                owner.ResumeEditing();
            }
        }
    }
}
