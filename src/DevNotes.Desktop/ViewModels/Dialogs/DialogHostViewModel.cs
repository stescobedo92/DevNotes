using CommunityToolkit.Mvvm.ComponentModel;

namespace DevNotes.Desktop.ViewModels.Dialogs;

/// <summary>Shows modal dialogs and returns their outcome.</summary>
public interface IDialogService
{
    Task<bool> ConfirmAsync(string title, string message, string confirmText, bool isDestructive = false);

    /// <summary>Asks for a line of text. Returns null when cancelled.</summary>
    Task<string?> PromptAsync(
        string title,
        string label,
        string initialValue,
        string confirmText,
        Func<string, string?> validate,
        IReadOnlyList<string>? suggestions = null);

    /// <summary>Shows any dialog and completes when it closes.</summary>
    Task ShowAsync(DialogViewModel dialog);
}

/// <summary>
/// Hosts dialogs inside the main window (an overlay instead of separate OS windows: it keeps
/// focus handling predictable, works the same on every platform and is testable headless).
/// Dialogs stack, so a confirmation can be shown on top of another dialog.
/// </summary>
public sealed partial class DialogHostViewModel : ObservableObject, IDialogService
{
    private readonly List<DialogViewModel> _stack = [];

    /// <summary>The dialog on top, or null when none is open.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsOpen))]
    public partial DialogViewModel? Current { get; private set; }

    public bool IsOpen => Current is not null;

    public async Task<bool> ConfirmAsync(string title, string message, string confirmText, bool isDestructive = false)
    {
        var dialog = new ConfirmDialogViewModel(title, message, confirmText, isDestructive);
        await ShowAsync(dialog);
        return dialog.Result;
    }

    public async Task<string?> PromptAsync(
        string title,
        string label,
        string initialValue,
        string confirmText,
        Func<string, string?> validate,
        IReadOnlyList<string>? suggestions = null)
    {
        var dialog = new PromptDialogViewModel(title, label, initialValue, confirmText, validate, suggestions);
        await ShowAsync(dialog);
        return dialog.Result;
    }

    public async Task ShowAsync(DialogViewModel dialog)
    {
        ArgumentNullException.ThrowIfNull(dialog);

        _stack.Add(dialog);
        Current = dialog;
        dialog.Dismissed += Remove;
        try
        {
            await dialog.Closed;
        }
        finally
        {
            dialog.Dismissed -= Remove;
            Remove(dialog); // Already gone when it closed normally; covers a dialog that never raised Dismissed.
        }
    }

    /// <summary>Takes the dialog off the stack the moment it closes, so callers see the next dialog (or none) at once.</summary>
    private void Remove(DialogViewModel dialog)
    {
        if (_stack.Remove(dialog))
        {
            Current = _stack.Count > 0 ? _stack[^1] : null;
        }
    }

    /// <summary>Cancels the dialog on top (Escape); a dialog with unsaved work may ask first.</summary>
    public void CancelCurrent() => Current?.Cancel();

    /// <summary>Dismisses every open dialog; returns false as soon as one of them decides to stay.</summary>
    public async Task<bool> CancelAllAsync()
    {
        while (Current is { } dialog)
        {
            if (!await dialog.CancelAsync())
            {
                return false;
            }
        }

        return true;
    }
}
