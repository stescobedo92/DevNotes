using DevNotes.Desktop.ViewModels;
using DevNotes.Desktop.Views;

namespace DevNotes.Desktop.Services;

/// <summary>
/// Owns the single quick-capture window of the process. The window is created on first use and
/// then only shown and hidden; the application shutdown closes it like any other window.
/// </summary>
public sealed class QuickCaptureWindowPresenter(QuickCaptureViewModel viewModel) : IQuickCapturePresenter
{
    private readonly QuickCaptureViewModel _viewModel = viewModel ?? throw new ArgumentNullException(nameof(viewModel));
    private QuickCaptureWindow? _window;
    private bool _subscribed;

    public void Show()
    {
        if (!_subscribed)
        {
            _viewModel.CloseRequested += (_, _) => Hide();
            _subscribed = true;
        }

        _viewModel.Prepare();
        _window ??= new QuickCaptureWindow { DataContext = _viewModel };
        if (!_window.IsVisible)
        {
            _window.Show();
        }

        _window.Activate();
        _window.FocusInput();
    }

    public void Hide() => _window?.Hide();
}
