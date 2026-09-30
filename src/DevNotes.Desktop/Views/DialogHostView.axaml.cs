using System.ComponentModel;
using Avalonia.Controls;
using Avalonia.Input;
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
            ConfirmDialogViewModel { IsDestructive: true } => FindDescendant<Button>("CancelButton"),
            ConfirmDialogViewModel => FindDescendant<Button>("ConfirmButton"),
            _ => FindDescendant<Button>("CloseButton"),
        };

        if (target is TextBox textBox)
        {
            textBox.Focus();
            textBox.SelectAll();
        }
        else
        {
            (target ?? Card).Focus();
        }
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
        else if (e.Key == Key.Enter && dialog is PromptDialogViewModel prompt && e.Source is TextBox)
        {
            prompt.ConfirmCommand.Execute(null);
            e.Handled = true;
        }
    }
}
