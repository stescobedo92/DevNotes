using System.ComponentModel;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Threading;
using DevNotes.Desktop.ViewModels;

namespace DevNotes.Desktop.Views;

public sealed partial class QuickOpenView : UserControl
{
    private QuickOpenViewModel? _viewModel;
    private IInputElement? _focusBeforeOpening;

    public QuickOpenView()
    {
        InitializeComponent();
    }

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        _viewModel?.PropertyChanged -= OnViewModelPropertyChanged;
        _viewModel = DataContext as QuickOpenViewModel;
        _viewModel?.PropertyChanged += OnViewModelPropertyChanged;
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (_viewModel is null)
        {
            return;
        }

        if (e.PropertyName == nameof(QuickOpenViewModel.IsOpen) && _viewModel.IsOpen)
        {
            var focused = TopLevel.GetTopLevel(this)?.FocusManager?.GetFocusedElement();
            if (!ReferenceEquals(focused, QueryBox))
            {
                _focusBeforeOpening = focused;
            }

            // The overlay becomes visible with this same change; focus once layout has caught up.
            Dispatcher.UIThread.Post(() => QueryBox.Focus(), DispatcherPriority.Input);
        }
        else if (e.PropertyName == nameof(QuickOpenViewModel.IsOpen))
        {
            // Closing hides the focused text box. Without this the focus would be nowhere and the
            // keyboard would do nothing until the user clicked; a command that was accepted may still
            // move it elsewhere afterwards (the editor after opening a note, a dialog).
            var previous = _focusBeforeOpening;
            _focusBeforeOpening = null;
            if (previous is Control { IsEffectivelyVisible: true } control)
            {
                control.Focus();
            }
        }
        else if (e.PropertyName == nameof(QuickOpenViewModel.SelectedItem) && _viewModel.SelectedItem is { } selected)
        {
            Results.ScrollIntoView(selected);
        }
    }

    // The whole overlay is driven from the text box: arrows move, Enter accepts, Escape closes.
    private void OnCardKeyDown(object? sender, KeyEventArgs e)
    {
        if (_viewModel is null)
        {
            return;
        }

        switch (e.Key)
        {
            case Key.Down:
                _viewModel.MoveSelection(1);
                e.Handled = true;
                break;
            case Key.Up:
                _viewModel.MoveSelection(-1);
                e.Handled = true;
                break;
            case Key.Enter:
                _viewModel.AcceptCommand.Execute(null);
                e.Handled = true;
                break;
            case Key.Escape:
                _viewModel.Close();
                e.Handled = true;
                break;
            default:
                break;
        }
    }

    private void OnResultDoubleTapped(object? sender, TappedEventArgs e) => _viewModel?.AcceptCommand.Execute(null);

    private void OnScrimPressed(object? sender, PointerPressedEventArgs e) => _viewModel?.Close();
}
