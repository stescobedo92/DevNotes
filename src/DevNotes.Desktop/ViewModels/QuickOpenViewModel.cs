using System.Data.Common;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DevNotes.Application.Search;
using DevNotes.Application.Vaults;
using DevNotes.Desktop.Resources;
using DevNotes.Desktop.Services;
using DevNotes.Domain.Notes;

namespace DevNotes.Desktop.ViewModels;

public enum QuickOpenMode
{
    /// <summary>Live note search and quick switching (Ctrl+K / Ctrl+P).</summary>
    Notes,

    /// <summary>Command palette (Ctrl+Shift+P).</summary>
    Commands,
}

public sealed class QuickOpenItemViewModel
{
    public QuickOpenItemViewModel(NoteListEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        NotePath = entry.Note.Path;
        Title = entry.Title.Count > 0 ? entry.Title : [new SnippetSegment(entry.Note.Title, IsMatch: false)];
        Detail = entry.Snippet;
        Hint = entry.Note.Project ?? string.Empty;
        AccessibleName = entry.Note.Title;
    }

    public QuickOpenItemViewModel(AppCommand command)
    {
        Command = command ?? throw new ArgumentNullException(nameof(command));
        Title = [new SnippetSegment(command.Title, IsMatch: false)];
        Detail = [];
        Hint = command.ShortcutText;
        AccessibleName = command.Title;
    }

    public NotePath? NotePath { get; }

    public AppCommand? Command { get; }

    public IReadOnlyList<SnippetSegment> Title { get; }

    public IReadOnlyList<SnippetSegment> Detail { get; }

    public bool HasDetail => Detail.Count > 0;

    /// <summary>Right-aligned secondary text: the project of a note or the shortcut of a command.</summary>
    public string Hint { get; }

    public string AccessibleName { get; }
}

/// <summary>
/// The centered overlay used for quick search, note switching and the command palette: a text
/// box with live results, driven entirely from the keyboard.
/// </summary>
public sealed partial class QuickOpenViewModel : ObservableObject
{
    public const int MaxResults = 30;

    private readonly INotificationService _notifications;
    private IVaultSession? _session;
    private IReadOnlyList<AppCommand> _commands = [];
    private int _queryVersion;

    public QuickOpenViewModel(INotificationService notifications)
    {
        _notifications = notifications ?? throw new ArgumentNullException(nameof(notifications));
        Query = string.Empty;
        Items = [];
    }

    /// <summary>Opens a note chosen in the overlay.</summary>
    public Func<NotePath, Task<bool>>? OpenNoteRequested { get; set; }

    [ObservableProperty]
    public partial bool IsOpen { get; private set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Placeholder))]
    public partial QuickOpenMode Mode { get; private set; }

    [ObservableProperty]
    public partial string Query { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasNoMatches))]
    public partial IReadOnlyList<QuickOpenItemViewModel> Items { get; private set; }

    [ObservableProperty]
    public partial QuickOpenItemViewModel? SelectedItem { get; set; }

    public bool HasNoMatches => IsOpen && Items.Count == 0;

    public string Placeholder => Mode == QuickOpenMode.Commands ? Strings.Quick_CommandsPlaceholder : Strings.Quick_NotesPlaceholder;

    public void SetSession(IVaultSession? session) => _session = session;

    public void SetCommands(IReadOnlyList<AppCommand> commands) => _commands = commands ?? throw new ArgumentNullException(nameof(commands));

    public void Open(QuickOpenMode mode)
    {
        Mode = mode;
        IsOpen = true;
        if (Query.Length == 0)
        {
            _ = RefreshAsync();
        }
        else
        {
            Query = string.Empty; // Triggers the refresh.
        }
    }

    [RelayCommand]
    public void Close()
    {
        Interlocked.Increment(ref _queryVersion);
        IsOpen = false;
        Items = [];
        SelectedItem = null;
    }

    public void MoveSelection(int delta)
    {
        if (Items.Count == 0)
        {
            return;
        }

        var index = SelectedItem is null ? -1 : IndexOf(SelectedItem);

        // Wraps around: Up on the first row goes to the last one.
        var next = ((index + delta) % Items.Count + Items.Count) % Items.Count;
        SelectedItem = Items[next];
    }

    /// <summary>Runs the selected row: opens the note or executes the command.</summary>
    [RelayCommand]
    public async Task AcceptAsync()
    {
        if (SelectedItem is not { } item)
        {
            return;
        }

        Close();
        if (item.NotePath is { } path)
        {
            if (OpenNoteRequested is { } open)
            {
                await open(path);
            }
        }
        else if (item.Command is { } command && command.Command.CanExecute(null))
        {
            command.Command.Execute(null);
        }
    }

    partial void OnQueryChanged(string value)
    {
        if (IsOpen)
        {
            _ = RefreshAsync();
        }
    }

    partial void OnIsOpenChanged(bool value) => OnPropertyChanged(nameof(HasNoMatches));

    private async Task RefreshAsync()
    {
        var version = Interlocked.Increment(ref _queryVersion);
        IReadOnlyList<QuickOpenItemViewModel> items;

        if (Mode == QuickOpenMode.Commands)
        {
            items = [.. _commands
                .Where(command => command.Title.Contains(Query.Trim(), StringComparison.CurrentCultureIgnoreCase) && command.Command.CanExecute(null))
                .Select(command => new QuickOpenItemViewModel(command))];
        }
        else if (_session is { } session)
        {
            try
            {
                var result = await session.Queries.QueryAsync(Query, NoteSortOrder.Relevance, MaxResults, CancellationToken.None);
                items = [.. result.Entries.Select(entry => new QuickOpenItemViewModel(entry))];
            }
            catch (Exception exception) when (exception is DbException or ObjectDisposedException || ErrorMessages.IsExpected(exception))
            {
                _notifications.Show(ErrorMessages.Describe(exception), NotificationKind.Error);
                items = [];
            }
        }
        else
        {
            items = [];
        }

        if (version != Volatile.Read(ref _queryVersion) || !IsOpen)
        {
            return; // The query changed (or the overlay closed) while this one was running.
        }

        Items = items;
        SelectedItem = items.Count > 0 ? items[0] : null;
    }

    private int IndexOf(QuickOpenItemViewModel item)
    {
        for (var i = 0; i < Items.Count; i++)
        {
            if (ReferenceEquals(Items[i], item))
            {
                return i;
            }
        }

        return -1;
    }
}
