using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DevNotes.Application.Abstractions;
using DevNotes.Desktop.Resources;
using DevNotes.Desktop.Services;

namespace DevNotes.Desktop.ViewModels.Dialogs;

/// <summary>Base class of the modal dialogs shown inside the main window.</summary>
public abstract partial class DialogViewModel : ObservableObject
{
    private readonly TaskCompletionSource _closed = new(TaskCreationOptions.RunContinuationsAsynchronously);

    protected DialogViewModel(string title)
    {
        Title = title;
    }

    public string Title { get; }

    /// <summary>Completes when the dialog has been confirmed or cancelled.</summary>
    public Task Closed => _closed.Task;

    /// <summary>Dismisses the dialog without applying anything (Escape, Cancel, click outside).</summary>
    [RelayCommand]
    public void Cancel()
    {
        OnCancelled();
        Close();
    }

    protected virtual void OnCancelled()
    {
    }

    protected void Close() => _closed.TrySetResult();
}

public sealed partial class ConfirmDialogViewModel : DialogViewModel
{
    public ConfirmDialogViewModel(string title, string message, string confirmText, bool isDestructive)
        : base(title)
    {
        Message = message;
        ConfirmText = confirmText;
        IsDestructive = isDestructive;
    }

    public string Message { get; }

    public string ConfirmText { get; }

    /// <summary>Destructive confirmations use the danger style and do not take the default focus.</summary>
    public bool IsDestructive { get; }

    public bool Result { get; private set; }

    [RelayCommand]
    private void Confirm()
    {
        Result = true;
        Close();
    }
}

public sealed partial class PromptDialogViewModel : DialogViewModel
{
    private readonly Func<string, string?> _validate;

    /// <param name="title">Dialog title.</param>
    /// <param name="label">Label of the text field.</param>
    /// <param name="initialValue">Text shown when the dialog opens.</param>
    /// <param name="confirmText">Caption of the confirm button.</param>
    /// <param name="validate">Returns an error message for invalid input, or null when the input is acceptable.</param>
    /// <param name="suggestions">Optional values offered below the field (e.g. existing folders).</param>
    public PromptDialogViewModel(
        string title,
        string label,
        string initialValue,
        string confirmText,
        Func<string, string?> validate,
        IReadOnlyList<string>? suggestions = null)
        : base(title)
    {
        Label = label;
        ConfirmText = confirmText;
        _validate = validate ?? throw new ArgumentNullException(nameof(validate));
        Suggestions = suggestions ?? [];
        Value = initialValue;
    }

    public string Label { get; }

    public string ConfirmText { get; }

    public IReadOnlyList<string> Suggestions { get; }

    public bool HasSuggestions => Suggestions.Count > 0;

    /// <summary>The accepted value, or null when the dialog was cancelled.</summary>
    public string? Result { get; private set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasError))]
    public partial string? Error { get; private set; }

    public bool HasError => !string.IsNullOrEmpty(Error);

    [ObservableProperty]
    public partial string Value { get; set; }

    partial void OnValueChanged(string value)
    {
        // Clear a stale message as soon as the user edits; validation runs again on confirm.
        Error = null;
    }

    [RelayCommand]
    private void Confirm()
    {
        var value = Value ?? string.Empty;
        if (_validate(value) is { } error)
        {
            Error = error;
            return;
        }

        Result = value;
        Close();
    }

    [RelayCommand]
    private void UseSuggestion(string? suggestion)
    {
        if (suggestion is not null)
        {
            Value = suggestion;
        }
    }
}

public sealed partial class TrashItemViewModel(TrashEntry entry) : ObservableObject
{
    public TrashEntry Entry { get; } = entry;

    public string Name => Entry.OriginalPath.FileNameWithoutExtension;

    public string Path => Entry.OriginalPath.Value;

    public string DeletedAt => ErrorMessages.Format(
        Strings.Dialog_Trash_DeletedAt,
        Entry.DeletedAtUtc.ToLocalTime().ToString("g", CultureInfo.CurrentCulture));
}

/// <summary>Lists the notes in the vault trash and lets the user restore or purge them.</summary>
public sealed partial class TrashDialogViewModel : DialogViewModel
{
    private readonly Func<TrashItemViewModel, Task<bool>> _restore;
    private readonly Func<TrashItemViewModel, Task<bool>> _deleteForever;
    private readonly Func<int, Task<bool>> _emptyAll;

    /// <param name="entries">Current content of the trash.</param>
    /// <param name="restore">Restores one entry; returns false when it could not be restored.</param>
    /// <param name="deleteForever">Purges one entry after confirmation; returns false when the user backed out.</param>
    /// <param name="emptyAll">Purges everything after confirmation; returns false when the user backed out.</param>
    public TrashDialogViewModel(
        IEnumerable<TrashEntry> entries,
        Func<TrashItemViewModel, Task<bool>> restore,
        Func<TrashItemViewModel, Task<bool>> deleteForever,
        Func<int, Task<bool>> emptyAll)
        : base(Strings.Dialog_Trash_Title)
    {
        ArgumentNullException.ThrowIfNull(entries);
        _restore = restore ?? throw new ArgumentNullException(nameof(restore));
        _deleteForever = deleteForever ?? throw new ArgumentNullException(nameof(deleteForever));
        _emptyAll = emptyAll ?? throw new ArgumentNullException(nameof(emptyAll));

        Items = new ObservableCollection<TrashItemViewModel>(entries.Select(entry => new TrashItemViewModel(entry)));
        Items.CollectionChanged += (_, _) =>
        {
            OnPropertyChanged(nameof(IsEmpty));
            EmptyAllCommand.NotifyCanExecuteChanged();
        };
    }

    public ObservableCollection<TrashItemViewModel> Items { get; }

    public bool IsEmpty => Items.Count == 0;

    [RelayCommand]
    private async Task RestoreAsync(TrashItemViewModel? item)
    {
        if (item is not null && await _restore(item))
        {
            Items.Remove(item);
        }
    }

    [RelayCommand]
    private async Task DeleteForeverAsync(TrashItemViewModel? item)
    {
        if (item is not null && await _deleteForever(item))
        {
            Items.Remove(item);
        }
    }

    [RelayCommand(CanExecute = nameof(CanEmptyAll))]
    private async Task EmptyAllAsync()
    {
        if (await _emptyAll(Items.Count))
        {
            Items.Clear();
        }
    }

    private bool CanEmptyAll() => Items.Count > 0;
}
