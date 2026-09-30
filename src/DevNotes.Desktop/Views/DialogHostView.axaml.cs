using System.ComponentModel;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using DevNotes.Desktop.ViewModels.Dialogs;

namespace DevNotes.Desktop.Views;

public sealed partial class DialogHostView : UserControl
{
    private DialogHostViewModel? _viewModel;
    private IInputElement? _focusBeforeDialog;

    public DialogHostView()
    {
        InitializeComponent();
    }

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        _viewModel?.PropertyChanged -= OnViewModelPropertyChanged;
        _viewModel = DataContext as DialogHostViewModel;
        _viewModel?.PropertyChanged += OnViewModelPropertyChanged;
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(DialogHostViewModel.Current) || _viewModel is null)
        {
            return;
        }

        if (_viewModel.Current is { } dialog)
        {
            _focusBeforeDialog ??= TopLevel.GetTopLevel(this)?.FocusManager?.GetFocusedElement();

            // The dialog content is created by this same change; focus once it exists.
            Dispatcher.UIThread.Post(() => FocusInitialElement(dialog), DispatcherPriority.Input);
        }
        else
        {
            // Return focus to where the user was before the dialog opened.
            _focusBeforeDialog?.Focus();
            _focusBeforeDialog = null;
        }
    }

    private void FocusInitialElement(DialogViewModel dialog)
    {
        if (_viewModel?.Current != dialog)
        {
            return;
        }

        // Text input first; otherwise the safe choice: Cancel for destructive confirmations, the action for the rest.
        var target = dialog switch
        {
            PromptDialogViewModel => FindDescendant<TextBox>("PromptInput"),
            NewNoteDialogViewModel => FindDescendant<TextBox>("NewNoteTitle"),
            TemplatesDialogViewModel => FindDescendant<ListBox>("TemplateList"),
            ConfirmDialogViewModel { IsDestructive: true } => FindDescendant<Button>("CancelButton"),
            ConfirmDialogViewModel => FindDescendant<Button>("ConfirmButton"),
            _ => FindDescendant<Button>("CloseButton"),
        };

        var focused = target switch
        {
            TextBox textBox => FocusText(textBox),
            ListBox list => FocusSelectedItem(list),
            null => false,
            _ => target.Focus(),
        };

        // Focus must end up inside the card: Escape and the other keys of the dialog are handled there.
        if (!focused)
        {
            Card.Focus();
        }
    }

    private static bool FocusText(TextBox textBox)
    {
        var focused = textBox.Focus();
        textBox.SelectAll();
        return focused;
    }

    /// <summary>A list box is not focusable itself; its selected (or first) item is.</summary>
    private static bool FocusSelectedItem(ListBox list)
    {
        var index = list.SelectedIndex >= 0 ? list.SelectedIndex : 0;
        return list.ContainerFromIndex(index) is Control item && item.Focus();
    }

    private Control? FindDescendant<T>(string name)
        where T : Control =>
        DialogContent.GetVisualDescendants().OfType<T>().FirstOrDefault(control => control.Name == name);

    private void OnCardKeyDown(object? sender, KeyEventArgs e)
    {
        if (_viewModel?.Current is not { } dialog)
        {
            return;
        }

        if (e.Key == Key.Escape)
        {
            _viewModel.CancelCurrent();
            e.Handled = true;
        }
        else if (e.Key == Key.Enter && e.Source is TextBox { AcceptsReturn: false } source)
        {
            // Enter in a single-line field confirms the dialog (or applies the field, for the shortcut).
            switch (dialog)
            {
                case PromptDialogViewModel prompt:
                    prompt.ConfirmCommand.Execute(null);
                    e.Handled = true;
                    break;
                case NewNoteDialogViewModel newNote:
                    newNote.ConfirmCommand.Execute(null);
                    e.Handled = true;
                    break;
                case SettingsDialogViewModel settings when source.Name == "HotkeyBox":
                    settings.ApplyHotkeyCommand.Execute(null);
                    e.Handled = true;
                    break;
                default:
                    break;
            }
        }
    }

    private void OnHotkeyLostFocus(object? sender, RoutedEventArgs e)
    {
        if (_viewModel?.Current is SettingsDialogViewModel settings)
        {
            settings.ApplyHotkeyCommand.Execute(null);
        }
    }
}
