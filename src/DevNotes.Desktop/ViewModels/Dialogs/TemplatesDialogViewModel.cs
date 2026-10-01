using System.Collections.ObjectModel;
using System.Data.Common;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DevNotes.Application.Notes;
using DevNotes.Desktop.Resources;
using DevNotes.Desktop.Services;

namespace DevNotes.Desktop.ViewModels.Dialogs;

public sealed partial class TemplateItemViewModel(NoteTemplate template) : ObservableObject
{
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsCustomized), nameof(KindLabel), nameof(ResetLabel), nameof(CanReset))]
    public partial NoteTemplate Template { get; set; } = template ?? throw new ArgumentNullException(nameof(template));

    public string Key => Template.Key;

    public string Label => TemplateLabels.Get(Template.Key);

    public bool IsBuiltIn => Template.IsBuiltIn;

    public bool IsCustomized => Template.IsCustomized;

    /// <summary>Built-in, customized (a built-in with a version in the vault) or the user's own.</summary>
    public string KindLabel => Template switch
    {
        { IsBuiltIn: true, IsCustomized: false } => Strings.Dialog_Templates_BuiltIn,
        { IsBuiltIn: true } => Strings.Dialog_Templates_Customized,
        _ => Strings.Dialog_Templates_Custom,
    };

    /// <summary>Resetting a customized built-in restores the app's text; resetting the user's own template deletes it.</summary>
    public string ResetLabel => IsBuiltIn ? Strings.Dialog_Templates_Reset : Strings.Action_Delete;

    public bool CanReset => IsCustomized;
}

/// <summary>
/// Lets the user read and edit the templates of the vault. Edits are saved explicitly; changing
/// the selection with unsaved edits asks first, so nothing typed is dropped by accident.
/// </summary>
public sealed partial class TemplatesDialogViewModel : DialogViewModel
{
    private readonly ITemplateService _templates;
    private readonly IDialogService _dialogs;
    private readonly INotificationService _notifications;
    private bool _revertingSelection;

    public TemplatesDialogViewModel(ITemplateService templates, IDialogService dialogs, INotificationService notifications)
        : base(Strings.Dialog_Templates_Title)
    {
        _templates = templates ?? throw new ArgumentNullException(nameof(templates));
        _dialogs = dialogs ?? throw new ArgumentNullException(nameof(dialogs));
        _notifications = notifications ?? throw new ArgumentNullException(nameof(notifications));
        Items = [];
        EditorText = string.Empty;
    }

    public ObservableCollection<TemplateItemViewModel> Items { get; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSelection), nameof(IsDirty))]
    [NotifyCanExecuteChangedFor(nameof(SaveCommand), nameof(ResetCommand))]
    public partial TemplateItemViewModel? SelectedItem { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsDirty))]
    [NotifyCanExecuteChangedFor(nameof(SaveCommand))]
    public partial string EditorText { get; set; }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SaveCommand), nameof(ResetCommand), nameof(NewCommand))]
    public partial bool IsBusy { get; private set; }

    public bool HasSelection => SelectedItem is not null;

    public override bool IsWide => true;

    public bool IsDirty => SelectedItem is { } item && !string.Equals(EditorText, item.Template.Text, StringComparison.Ordinal);

    public string Hint => Strings.Dialog_Templates_Hint;

    /// <summary>Loads the templates of the vault. Must be called before the dialog is shown.</summary>
    public async Task LoadAsync()
    {
        IsBusy = true;
        try
        {
            var templates = await _templates.ListAsync(CancellationToken.None);
            Items.Clear();
            foreach (var template in templates)
            {
                Items.Add(new TemplateItemViewModel(template));
            }

            SelectedItem = Items.FirstOrDefault();
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>Escape or Close with unsaved edits asks first; nothing typed is dropped by accident.</summary>
    protected override Task<bool> ConfirmCancelAsync()
    {
        if (!IsDirty || SelectedItem is not { } item)
        {
            return Task.FromResult(true);
        }

        return ConfirmDiscardAsync(item);
    }

    private Task<bool> ConfirmDiscardAsync(TemplateItemViewModel item) =>
        _dialogs.ConfirmAsync(
            Strings.Dialog_Templates_Title,
            ErrorMessages.Format(Strings.Dialog_Templates_DiscardConfirm, item.Label),
            Strings.Conflict_Discard,
            isDestructive: true);

    partial void OnSelectedItemChanged(TemplateItemViewModel? oldValue, TemplateItemViewModel? newValue)
    {
        if (_revertingSelection)
        {
            return;
        }

        if (oldValue is not null && !string.Equals(EditorText, oldValue.Template.Text, StringComparison.Ordinal))
        {
            _ = ConfirmSelectionChangeAsync(oldValue, newValue);
            return;
        }

        EditorText = newValue?.Template.Text ?? string.Empty;
    }

    private async Task ConfirmSelectionChangeAsync(TemplateItemViewModel previous, TemplateItemViewModel? next)
    {
        var discard = await ConfirmDiscardAsync(previous);

        _revertingSelection = true;
        try
        {
            if (discard)
            {
                if (ReferenceEquals(SelectedItem, next))
                {
                    EditorText = next?.Template.Text ?? string.Empty;
                }
            }
            else
            {
                SelectedItem = previous; // The edited text is untouched.
            }
        }
        finally
        {
            _revertingSelection = false;
        }
    }

    [RelayCommand(CanExecute = nameof(CanSave))]
    private async Task SaveAsync()
    {
        if (SelectedItem is not { } item)
        {
            return;
        }

        await RunAsync(async () =>
        {
            var saved = await _templates.SaveAsync(item.Key, EditorText, CancellationToken.None);
            item.Template = saved;
            EditorText = saved.Text;
            OnPropertyChanged(nameof(IsDirty));
            _notifications.Show(Strings.Notify_TemplateSaved);
        });
    }

    private bool CanSave() => !IsBusy && IsDirty;

    [RelayCommand(CanExecute = nameof(CanReset))]
    private async Task ResetAsync()
    {
        if (SelectedItem is not { } item)
        {
            return;
        }

        var confirmed = await _dialogs.ConfirmAsync(
            Strings.Dialog_Templates_Title,
            ErrorMessages.Format(item.IsBuiltIn ? Strings.Dialog_Templates_ResetConfirm : Strings.Dialog_Templates_DeleteConfirm, item.Label),
            item.ResetLabel,
            isDestructive: true);
        if (!confirmed)
        {
            return;
        }

        await RunAsync(async () =>
        {
            await _templates.ResetAsync(item.Key, CancellationToken.None);
            if (item.IsBuiltIn)
            {
                var restored = await _templates.GetAsync(item.Key, CancellationToken.None);
                if (restored is not null)
                {
                    item.Template = restored;
                    EditorText = restored.Text;
                }
            }
            else
            {
                var index = Items.IndexOf(item);
                Items.Remove(item);
                SelectedItem = Items.Count == 0 ? null : Items[Math.Min(index, Items.Count - 1)];
            }
        });
    }

    private bool CanReset() => !IsBusy && SelectedItem is { CanReset: true };

    [RelayCommand(CanExecute = nameof(CanCreate))]
    private async Task NewAsync()
    {
        // The new template becomes the selection, so unsaved edits are settled first: refusing to
        // drop them cancels the whole thing before anything is written to the vault.
        var edited = IsDirty ? SelectedItem : null;
        if (edited is not null && !await ConfirmDiscardAsync(edited))
        {
            return;
        }

        var name = await _dialogs.PromptAsync(
            Strings.Dialog_Templates_New,
            Strings.Dialog_Templates_NewLabel,
            string.Empty,
            Strings.Action_Create,
            ValidateName);
        if (name is null || !TemplateKey.TryNormalize(name, out var key))
        {
            return;
        }

        await RunAsync(async () =>
        {
            // The new template starts as a copy of the plain note, so it is valid from the first save.
            var seed = (await _templates.GetAsync("note", CancellationToken.None))?.Text
                ?? BuiltInTemplates.Get("note", CultureInfo.CurrentUICulture);
            var saved = await _templates.SaveAsync(key, seed, CancellationToken.None);
            var item = new TemplateItemViewModel(saved);
            Items.Add(item);
            if (edited is not null && ReferenceEquals(SelectedItem, edited))
            {
                // Already confirmed above; the edits survived until the template existed (a cancelled prompt keeps them).
                EditorText = edited.Template.Text;
            }

            SelectedItem = item;
        });
    }

    private bool CanCreate() => !IsBusy;

    private string? ValidateName(string value)
    {
        if (!TemplateKey.TryNormalize(value, out var key))
        {
            return Strings.Error_TemplateName;
        }

        return Items.Any(item => item.Key == key) ? Strings.Error_TemplateExists : null;
    }

    private async Task RunAsync(Func<Task> operation)
    {
        IsBusy = true;
        try
        {
            await operation();
        }
        catch (Exception exception) when (exception is DbException || ErrorMessages.IsExpected(exception))
        {
            _notifications.Show(ErrorMessages.Describe(exception), NotificationKind.Error);
        }
        finally
        {
            IsBusy = false;
        }
    }
}
