using Avalonia.Controls;
using DevNotes.Application.Settings;

namespace DevNotes.Desktop.Services;

/// <summary>Shows and hides the quick-capture window.</summary>
public interface IQuickCapturePresenter
{
    /// <summary>Brings the capture window to the front (creating it the first time). UI thread only.</summary>
    void Show();

    void Hide();
}

/// <summary>
/// Keeps the system-wide shortcut in line with the settings and opens the capture window when it
/// is pressed. The shortcut is registered once the main window exists (Windows registers it
/// against that window) and re-registered whenever the setting changes.
/// </summary>
public sealed class QuickCaptureCoordinator : IDisposable
{
    private readonly IGlobalHotkeyService _hotkey;
    private readonly ISettingsService _settings;
    private readonly IQuickCapturePresenter _presenter;
    private readonly IUiDispatcher _dispatcher;
    private QuickCaptureSettings? _applied;
    private bool _started;
    private bool _disposed;

    public QuickCaptureCoordinator(IGlobalHotkeyService hotkey, ISettingsService settings, IQuickCapturePresenter presenter, IUiDispatcher dispatcher)
    {
        _hotkey = hotkey ?? throw new ArgumentNullException(nameof(hotkey));
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _presenter = presenter ?? throw new ArgumentNullException(nameof(presenter));
        _dispatcher = dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));
    }

    public IGlobalHotkeyService Hotkey => _hotkey;

    /// <summary>Attaches to the main window and registers the shortcut if the settings enable it.</summary>
    public void Start(TopLevel mainWindow)
    {
        ArgumentNullException.ThrowIfNull(mainWindow);
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_started)
        {
            return;
        }

        _started = true;
        _hotkey.Attach(mainWindow);
        _hotkey.Pressed += OnPressed;
        _settings.Changed += OnSettingsChanged;
        Apply(_settings.Current.QuickCapture);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _settings.Changed -= OnSettingsChanged;
        _hotkey.Pressed -= OnPressed;
        _hotkey.Dispose();
    }

    private void OnPressed(object? sender, EventArgs e) => _dispatcher.Post(_presenter.Show);

    private void OnSettingsChanged(object? sender, AppSettings settings) => _dispatcher.Post(() => Apply(settings.QuickCapture));

    private void Apply(QuickCaptureSettings quickCapture)
    {
        if (_disposed || quickCapture == _applied)
        {
            return;
        }

        _applied = quickCapture;
        if (quickCapture.GlobalHotkeyEnabled)
        {
            _hotkey.Register(quickCapture.Gesture);
        }
        else
        {
            _hotkey.Unregister();
        }
    }
}
