using System.Data.Common;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DevNotes.Application.Notes;
using DevNotes.Application.Vaults;
using DevNotes.Desktop.Resources;
using DevNotes.Desktop.Services;
using DevNotes.Domain.Notes;

namespace DevNotes.Desktop.ViewModels;

/// <summary>
/// The small always-on-top capture window: a title, a text and a project. Ctrl+Enter saves the
/// note in the open vault and closes the window; Escape hides it and keeps the draft. The draft
/// is also stored on disk (shortly after each change, when the window hides and at shutdown), so
/// quitting the app or a crash never loses it; it is only cleared once a note has been written.
/// </summary>
public sealed partial class QuickCaptureViewModel : ObservableObject, IDisposable
{
    /// <summary>Quiet time after a change before the draft is written to disk.</summary>
    public static readonly TimeSpan DraftDelay = TimeSpan.FromSeconds(1.5);

    private readonly IVaultSessionManager _sessions;
    private readonly INotificationService _notifications;
    private readonly IQuickCaptureDraftStore _drafts;
    private readonly TimeProvider _timeProvider;
    private readonly ITimer _draftTimer;
    private bool _draftLoaded;
    private bool _applyingDraft;

    public QuickCaptureViewModel(IVaultSessionManager sessions, INotificationService notifications, IQuickCaptureDraftStore drafts, TimeProvider timeProvider)
    {
        _sessions = sessions ?? throw new ArgumentNullException(nameof(sessions));
        _notifications = notifications ?? throw new ArgumentNullException(nameof(notifications));
        _drafts = drafts ?? throw new ArgumentNullException(nameof(drafts));
        _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));

        // Before the properties: their setters schedule a draft write through this timer.
        _draftTimer = timeProvider.CreateTimer(_ => PersistDraft(), null, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
        _applyingDraft = true;
        Title = string.Empty;
        Text = string.Empty;
        Project = string.Empty;
        _applyingDraft = false;
    }

    /// <summary>Raised when the window should go away (after a save, or on Escape).</summary>
    public event EventHandler? CloseRequested;

    /// <summary>Raised on the UI thread after a note was written.</summary>
    public event EventHandler<NotePath>? Saved;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SaveCommand))]
    public partial string Title { get; set; }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SaveCommand))]
    public partial string Text { get; set; }

    [ObservableProperty]
    public partial string Project { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasError))]
    public partial string? Error { get; private set; }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SaveCommand))]
    public partial bool IsSaving { get; private set; }

    public bool HasError => !string.IsNullOrEmpty(Error);

    public bool HasDraft => !string.IsNullOrWhiteSpace(Title) || !string.IsNullOrWhiteSpace(Text);

    /// <summary>Project offered when the window opens with an empty draft (the active project, if any).</summary>
    public string? DefaultProject { get; set; }

    /// <summary>
    /// Prepares the window for the user: a draft left by a previous session is restored the first
    /// time, and the project is prefilled unless a draft is pending.
    /// </summary>
    public void Prepare()
    {
        if (!_draftLoaded)
        {
            _draftLoaded = true;
            if (!HasDraft && _drafts.Load() is { } stored)
            {
                ApplyDraft(stored);
            }
        }

        if (!HasDraft && string.IsNullOrWhiteSpace(Project) && DefaultProject is { } project)
        {
            Project = project;
        }

        Error = null;
    }

    /// <summary>Writes the draft to disk now (or removes the stored one when there is nothing to keep).</summary>
    public void PersistDraft()
    {
        _draftTimer.Change(Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
        _drafts.Save(new CaptureDraft(Title, Text, Project));
    }

    public void Dispose() => _draftTimer.Dispose();

    [RelayCommand(CanExecute = nameof(CanSave))]
    private async Task SaveAsync()
    {
        if (_sessions.Current is not { } session)
        {
            Error = Strings.Capture_NoVault;
            return;
        }

        // What is being saved: anything typed while the write is in flight stays in the window.
        var draft = new CaptureDraft(Title, Text, Project);
        if (Resolve(draft, out var title, out var body) is { } problem)
        {
            Error = problem;
            return;
        }

        IsSaving = true;
        Error = null;
        try
        {
            var template = await session.Templates.GetAsync("note", CancellationToken.None);
            var project = string.IsNullOrWhiteSpace(draft.Project) ? null : draft.Project.Trim();
            var request = new NewNoteRequest(title, Folder: null, template?.Type ?? NoteType.Note, project, template?.Text, body);
            var created = await session.Notes.CreateAsync(request, CancellationToken.None);

            // The draft is only dropped once the note is on disk, and only the part that was saved.
            var untouched = string.Equals(Title, draft.Title, StringComparison.Ordinal) && string.Equals(Text, draft.Text, StringComparison.Ordinal);
            if (untouched)
            {
                _applyingDraft = true;
                try
                {
                    Title = string.Empty;
                    Text = string.Empty;
                }
                finally
                {
                    _applyingDraft = false;
                }

                _draftTimer.Change(Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
                _drafts.Clear();
            }

            _notifications.Show(ErrorMessages.Format(Strings.Notify_Captured, title));
            Saved?.Invoke(this, created.Path);
            if (untouched)
            {
                CloseRequested?.Invoke(this, EventArgs.Empty);
            }
        }
        catch (Exception exception) when (exception is DbException or ObjectDisposedException || ErrorMessages.IsExpected(exception))
        {
            Error = ErrorMessages.Describe(exception);
        }
        finally
        {
            IsSaving = false;
        }
    }

    /// <summary>Escape: the window goes away, the draft stays (in memory and on disk) for the next time.</summary>
    [RelayCommand]
    private void Cancel()
    {
        PersistDraft();
        CloseRequested?.Invoke(this, EventArgs.Empty);
    }

    private bool CanSave() => !IsSaving;

    partial void OnTitleChanged(string value) => ScheduleDraft();

    partial void OnTextChanged(string value) => ScheduleDraft();

    partial void OnProjectChanged(string value) => ScheduleDraft();

    private void ScheduleDraft()
    {
        if (!_applyingDraft)
        {
            _draftTimer.Change(DraftDelay, Timeout.InfiniteTimeSpan);
        }
    }

    private void ApplyDraft(CaptureDraft draft)
    {
        _applyingDraft = true;
        try
        {
            Title = draft.Title;
            Text = draft.Text;
            if (!string.IsNullOrWhiteSpace(draft.Project))
            {
                Project = draft.Project;
            }
        }
        finally
        {
            _applyingDraft = false;
        }
    }

    /// <summary>
    /// The title and body of the note, or a message explaining why there is nothing to save yet.
    /// A typed title is used as it is (an invalid one is reported, never replaced). Without one,
    /// the first line of the text becomes the title and leaves the body only when that loses
    /// nothing; otherwise the whole text stays in the body. Text without a usable line gets a
    /// time stamp as title.
    /// </summary>
    private string? Resolve(CaptureDraft draft, out string title, out string body)
    {
        body = draft.Text;
        if (!string.IsNullOrWhiteSpace(draft.Title))
        {
            if (NoteTitle.TryNormalize(draft.Title, out var typed))
            {
                title = typed;
                return null;
            }

            title = string.Empty;
            return ErrorMessages.Format(Strings.Error_TitleRequired, NoteTitle.MaxLength);
        }

        var (firstLine, remainder, lossless) = SplitFirstLine(draft.Text);
        if (NoteTitle.TryNormalize(firstLine, out var fromText))
        {
            title = fromText;
            if (lossless && string.Equals(fromText, firstLine, StringComparison.Ordinal))
            {
                body = remainder;
            }

            return null;
        }

        var stamp = ErrorMessages.Format(Strings.Capture_DefaultTitle, _timeProvider.GetLocalNow().ToString("g", CultureInfo.CurrentCulture));
        if (!string.IsNullOrWhiteSpace(draft.Text) && NoteTitle.TryNormalize(stamp, out var fallback))
        {
            title = fallback;
            return null;
        }

        title = string.Empty;
        return Strings.Capture_Empty;
    }

    internal static (string FirstLine, string Remainder, bool Lossless) SplitFirstLineForTests(string text) => SplitFirstLine(text);

    /// <summary>
    /// First non-empty line (without the marks of a Markdown heading) and everything after it.
    /// <c>Lossless</c> is false when the line had to be cut to the title length: the caller must
    /// then keep the whole text in the body.
    /// </summary>
    private static (string FirstLine, string Remainder, bool Lossless) SplitFirstLine(string text)
    {
        var span = text.AsSpan();
        var offset = 0;
        while (offset < span.Length)
        {
            var remaining = span[offset..];
            var lineBreak = remaining.IndexOfAny('\r', '\n');
            var line = lineBreak < 0 ? remaining : remaining[..lineBreak];
            var content = StripHeadingMarks(line.Trim());
            if (content.Length > 0)
            {
                var remainder = lineBreak < 0 ? ReadOnlySpan<char>.Empty : remaining[lineBreak..].TrimStart("\r\n");
                var lossless = content.Length <= NoteTitle.MaxLength;
                var title = lossless ? content.ToString() : content[..NoteTitle.MaxLength].TrimEnd().ToString();
                return (title, remainder.ToString(), lossless);
            }

            if (lineBreak < 0)
            {
                break;
            }

            offset += lineBreak + 1; // The second character of a CR LF pair yields an empty line, which is skipped like any other.
        }

        return (string.Empty, text, true);
    }

    /// <summary>"## Heading" → "Heading"; "#todo" is a word, not a heading, and stays as it is.</summary>
    private static ReadOnlySpan<char> StripHeadingMarks(ReadOnlySpan<char> line)
    {
        var marks = 0;
        while (marks < line.Length && line[marks] == '#')
        {
            marks++;
        }

        return marks is > 0 and <= 6 && marks < line.Length && char.IsWhiteSpace(line[marks]) ? line[marks..].Trim() : line;
    }
}
