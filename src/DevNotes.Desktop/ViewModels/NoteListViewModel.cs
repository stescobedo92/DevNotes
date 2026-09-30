using System.Data.Common;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using DevNotes.Application.Search;
using DevNotes.Application.Vaults;
using DevNotes.Desktop.Resources;
using DevNotes.Desktop.Services;
using DevNotes.Domain.Notes;

namespace DevNotes.Desktop.ViewModels;

public sealed record NoteSortOption(NoteSortOrder Order, string Label);

public enum NoteListState
{
    /// <summary>No vault is open.</summary>
    NoVault,

    /// <summary>The list has rows.</summary>
    Notes,

    /// <summary>The vault is empty.</summary>
    NoNotes,

    /// <summary>The search text matches nothing.</summary>
    NoResults,
}

/// <summary>One row of the note list.</summary>
public sealed class NoteListItemViewModel
{
    public NoteListItemViewModel(NoteListEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        var note = entry.Note;

        Path = note.Path;
        Title = entry.Title.Count > 0 ? entry.Title : [new SnippetSegment(note.Title, IsMatch: false)];
        PlainTitle = note.Title;
        Snippet = entry.Snippet;
        Project = note.Project;
        Tags = note.Tags;
        TypeLabel = NoteTypeLabels.Get(note.Type);
        DateText = (note.Updated ?? DateOnly.FromDateTime(note.FileLastWriteUtc.LocalDateTime)).ToString("d", CultureInfo.CurrentCulture);
    }

    public NotePath Path { get; }

    public IReadOnlyList<SnippetSegment> Title { get; }

    public string PlainTitle { get; }

    public IReadOnlyList<SnippetSegment> Snippet { get; }

    public bool HasSnippet => Snippet.Count > 0;

    public string? Project { get; }

    public bool HasProject => !string.IsNullOrEmpty(Project);

    public IReadOnlyList<string> Tags { get; }

    public string TypeLabel { get; }

    public string DateText { get; }

    /// <summary>What a screen reader announces for the row.</summary>
    public string AccessibleName => HasProject ? $"{PlainTitle}, {Project}, {DateText}" : $"{PlainTitle}, {DateText}";
}

public static class NoteTypeLabels
{
    public static string Get(NoteType type) =>
        Strings.ResourceManager.GetString("Type_" + type.ToKey(), Strings.Culture) ?? type.ToKey();
}

/// <summary>Search box, sort order and result list of the open vault.</summary>
public sealed partial class NoteListViewModel : ObservableObject, IDisposable
{
    /// <summary>Rows shown when the vault is listed without a search.</summary>
    public const int ResultLimit = 2_000;

    /// <summary>
    /// Rows shown for a search. Lower than <see cref="ResultLimit"/> because every hit carries a
    /// highlighted fragment that SQLite builds by tokenizing the note; beyond a few hundred hits the
    /// user refines the search instead of scrolling, and the list says when it was cut.
    /// </summary>
    public const int SearchResultLimit = 500;

    /// <summary>The busy indicator only appears when a query is slower than this.</summary>
    public static readonly TimeSpan BusyIndicatorDelay = TimeSpan.FromMilliseconds(200);

    private readonly IUiDispatcher _dispatcher;
    private readonly INotificationService _notifications;
    private readonly TimeProvider _timeProvider;
    private IVaultSession? _session;
    private CancellationTokenSource? _queryCancellation;
    private int _queryVersion;
    private int _completedVersion;
    private Task _latestRefresh = Task.CompletedTask;
    private bool _syncingSelection;
    private NotePath? _activePath;

    public NoteListViewModel(IUiDispatcher dispatcher, INotificationService notifications, TimeProvider timeProvider)
    {
        _dispatcher = dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));
        _notifications = notifications ?? throw new ArgumentNullException(nameof(notifications));
        _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));

        SortOptions =
        [
            new NoteSortOption(NoteSortOrder.UpdatedDescending, Strings.Sort_Updated),
            new NoteSortOption(NoteSortOrder.TitleAscending, Strings.Sort_Title),
            new NoteSortOption(NoteSortOrder.Relevance, Strings.Sort_Relevance),
        ];
        SelectedSort = SortOptions[0];
        SearchText = string.Empty;
        Items = [];
    }

    /// <summary>Asks the shell to open a note. Returns false when the note could not be opened (e.g. unsaved changes).</summary>
    public Func<NotePath, Task<bool>>? OpenRequested { get; set; }

    public IReadOnlyList<NoteSortOption> SortOptions { get; }

    /// <summary>Hint of the empty-vault state (mentions the "new note" shortcut); provided by the shell.</summary>
    public string EmptyVaultHint { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string SearchText { get; set; }

    [ObservableProperty]
    public partial NoteSortOption SelectedSort { get; set; }

    [ObservableProperty]
    public partial IReadOnlyList<NoteListItemViewModel> Items { get; private set; }

    [ObservableProperty]
    public partial NoteListItemViewModel? SelectedItem { get; set; }

    [ObservableProperty]
    public partial bool IsBusy { get; private set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasNotes), nameof(IsEmptyVault), nameof(HasNoResults), nameof(IsNoVault))]
    public partial NoteListState State { get; private set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsTruncated))]
    public partial string? TruncatedText { get; private set; }

    public bool IsTruncated => TruncatedText is not null;

    public bool HasNotes => State == NoteListState.Notes;

    public bool IsEmptyVault => State == NoteListState.NoNotes;

    public bool HasNoResults => State == NoteListState.NoResults;

    public bool IsNoVault => State == NoteListState.NoVault;

    public NoteSortOrder SortOrder
    {
        get => SelectedSort.Order;
        set => SelectedSort = SortOptions.FirstOrDefault(option => option.Order == value) ?? SortOptions[0];
    }

    /// <summary>Switches to another vault session (or to none) and reloads the list.</summary>
    public Task SetSessionAsync(IVaultSession? session)
    {
        _session = session;
        _activePath = null;
        _syncingSelection = true;
        try
        {
            SearchText = string.Empty;
            SelectedItem = null;
        }
        finally
        {
            _syncingSelection = false;
        }

        return RefreshAsync();
    }

    /// <summary>Marks the note that is open in the editor so the list highlights it.</summary>
    public void SetActiveNote(NotePath? path)
    {
        _activePath = path;
        SyncSelection();
    }

    /// <summary>
    /// Runs the current query again. Only the most recent refresh updates the list: one that is
    /// overtaken by a newer refresh completes without applying its (stale) result, so awaiting it
    /// does not guarantee an up-to-date list. Use <see cref="WhenSettledAsync"/> for that.
    /// </summary>
    public Task RefreshAsync()
    {
        var refresh = RefreshCoreAsync();
        _latestRefresh = refresh;
        return refresh;
    }

    /// <summary>Completes when no refresh is in flight, i.e. the list shows the result of the latest query.</summary>
    public async Task WhenSettledAsync()
    {
        Task current;
        do
        {
            current = _latestRefresh;
            await current;
        }
        while (!ReferenceEquals(current, _latestRefresh));
    }

    private async Task RefreshCoreAsync()
    {
        var version = Interlocked.Increment(ref _queryVersion);
        // Synchronous on purpose: the new query must reach the index without an extra thread hop.
        var previous = _queryCancellation;
        _queryCancellation = null;
        if (previous is not null)
        {
#pragma warning disable CA1849 // CancelAsync would defer the start of the query that replaces this one.
            previous.Cancel();
#pragma warning restore CA1849
            previous.Dispose();
        }

        if (_session is not { } session)
        {
            Apply(new NoteQueryResult([], IsSearch: false, IsTruncated: false), NoteListState.NoVault);
            IsBusy = false;
            return;
        }

        var cancellation = new CancellationTokenSource();
        _queryCancellation = cancellation;
        var token = cancellation.Token;
        _ = ShowBusyWhenSlowAsync(version, token);

        try
        {
            var limit = string.IsNullOrWhiteSpace(SearchText) ? ResultLimit : SearchResultLimit;
            var result = await session.Queries.QueryAsync(SearchText, SelectedSort.Order, limit, token);
            if (version != Volatile.Read(ref _queryVersion))
            {
                return; // A newer query superseded this one.
            }

            var state = result.Entries.Count > 0
                ? NoteListState.Notes
                : result.IsSearch ? NoteListState.NoResults : NoteListState.NoNotes;
            Apply(result, state);
        }
        catch (OperationCanceledException)
        {
            return;
        }
        catch (Exception exception) when (exception is DbException or ObjectDisposedException || ErrorMessages.IsExpected(exception))
        {
            if (version == Volatile.Read(ref _queryVersion))
            {
                _notifications.Show(ErrorMessages.Describe(exception), NotificationKind.Error);
            }
        }
        finally
        {
            // Only the current query may report completion: a cancelled one can finish later than
            // the query that replaced it and must not make that one look unfinished again.
            if (version == Volatile.Read(ref _queryVersion))
            {
                Volatile.Write(ref _completedVersion, version);
                IsBusy = false;
            }
        }
    }

    /// <summary>Moves the selection from the search box (arrow keys) without leaving it.</summary>
    public void MoveSelection(int delta)
    {
        if (Items.Count == 0)
        {
            return;
        }

        var index = SelectedItem is null ? -1 : IndexOf(SelectedItem.Path);
        var next = Math.Clamp(index + delta, 0, Items.Count - 1);
        SelectedItem = Items[next];
    }

    public void Dispose()
    {
        _queryCancellation?.Cancel();
        _queryCancellation?.Dispose();
        _queryCancellation = null;
    }

    partial void OnSearchTextChanged(string value) => _ = RefreshAsync();

    partial void OnSelectedSortChanged(NoteSortOption value)
    {
        OnPropertyChanged(nameof(SortOrder));
        _ = RefreshAsync();
    }

    partial void OnSelectedItemChanged(NoteListItemViewModel? value)
    {
        if (!_syncingSelection && value is not null && value.Path != _activePath)
        {
            _ = OpenAsync(value.Path);
        }
    }

    private async Task OpenAsync(NotePath path)
    {
        var opened = OpenRequested is { } open && await open(path);
        if (!opened)
        {
            // The editor kept the previous note (for example because it has unsaved changes).
            SyncSelection();
        }
    }

    private void Apply(NoteQueryResult result, NoteListState state)
    {
        _syncingSelection = true;
        try
        {
            Items = [.. result.Entries.Select(entry => new NoteListItemViewModel(entry))];
            State = state;
            TruncatedText = result.IsTruncated ? ErrorMessages.Format(Strings.List_Truncated, result.Entries.Count) : null;
            SelectedItem = FindActive();
        }
        finally
        {
            _syncingSelection = false;
        }
    }

    private void SyncSelection()
    {
        _syncingSelection = true;
        try
        {
            SelectedItem = FindActive();
        }
        finally
        {
            _syncingSelection = false;
        }
    }

    private NoteListItemViewModel? FindActive()
    {
        if (_activePath is not { } path)
        {
            return null;
        }

        var index = IndexOf(path);
        return index >= 0 ? Items[index] : null;
    }

    private int IndexOf(NotePath path)
    {
        for (var i = 0; i < Items.Count; i++)
        {
            if (Items[i].Path == path)
            {
                return i;
            }
        }

        return -1;
    }

    private async Task ShowBusyWhenSlowAsync(int version, CancellationToken cancellationToken)
    {
        try
        {
            await Task.Delay(BusyIndicatorDelay, _timeProvider, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        _dispatcher.Post(() =>
        {
            // Only if this query is still the current one and has not finished in the meantime.
            if (version == Volatile.Read(ref _queryVersion) && version != Volatile.Read(ref _completedVersion))
            {
                IsBusy = true;
            }
        });
    }
}
