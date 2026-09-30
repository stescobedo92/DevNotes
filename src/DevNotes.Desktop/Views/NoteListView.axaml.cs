using Avalonia.Controls;
using Avalonia.Input;
using DevNotes.Desktop.ViewModels;

namespace DevNotes.Desktop.Views;

public sealed partial class NoteListView : UserControl
{
    public NoteListView()
    {
        InitializeComponent();
    }

    /// <summary>Moves keyboard focus to the search box.</summary>
    public void FocusSearch()
    {
        SearchBox.Focus();
        SearchBox.SelectAll();
    }

    // Keyboard-only flow: arrows move through the results without leaving the search box.
    private void OnSearchBoxKeyDown(object? sender, KeyEventArgs e)
    {
        if (DataContext is not NoteListViewModel viewModel)
        {
            return;
        }

        switch (e.Key)
        {
            case Key.Down:
                viewModel.MoveSelection(1);
                e.Handled = true;
                break;
            case Key.Up:
                viewModel.MoveSelection(-1);
                e.Handled = true;
                break;
            case Key.Escape when !string.IsNullOrEmpty(viewModel.SearchText):
                viewModel.SearchText = string.Empty;
                e.Handled = true;
                break;
            default:
                break;
        }
    }
}
