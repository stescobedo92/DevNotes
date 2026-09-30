using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using DevNotes.Desktop.ViewModels;

namespace DevNotes.Desktop.Views;

/// <summary>
/// The floating capture window. It is never really closed while the app runs: closing hides it
/// and keeps the draft, so nothing typed can be lost by a stray Escape or a click on the close button.
/// </summary>
public sealed partial class QuickCaptureWindow : Window
{
    public QuickCaptureWindow()
    {
        InitializeComponent();

        // Tunnelling: the multi-line text box would otherwise take Enter (and Ctrl+Enter) for itself.
        AddHandler(KeyDownEvent, OnPreviewKeyDown, RoutingStrategies.Tunnel);
    }

    /// <summary>Focuses the text box with the caret at the end, so typing can start at once.</summary>
    public void FocusInput()
    {
        BodyBox.Focus();
        BodyBox.CaretIndex = BodyBox.Text?.Length ?? 0;
    }

    protected override void OnClosing(WindowClosingEventArgs e)
    {
        base.OnClosing(e);

        // Only the user's own close (the title-bar button) is turned into a hide; the app shutting down goes through.
        if (e.CloseReason == WindowCloseReason.WindowClosing)
        {
            e.Cancel = true;
            Hide();
        }
    }

    private void OnPreviewKeyDown(object? sender, KeyEventArgs e)
    {
        if (DataContext is not QuickCaptureViewModel viewModel)
        {
            return;
        }

        var primary = e.KeyModifiers.HasFlag(KeyModifiers.Control) || e.KeyModifiers.HasFlag(KeyModifiers.Meta);
        if (e.Key == Key.Enter && primary)
        {
            if (viewModel.SaveCommand.CanExecute(null))
            {
                viewModel.SaveCommand.Execute(null);
            }

            e.Handled = true;
        }
        else if (e.Key == Key.Escape)
        {
            viewModel.CancelCommand.Execute(null);
            e.Handled = true;
        }
    }
}
