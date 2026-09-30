using System.ComponentModel;
using System.Runtime.ExceptionServices;
using System.Windows.Input;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
using DevNotes.Application.Settings;
using DevNotes.Desktop.ViewModels;

namespace DevNotes.Desktop.Views;

public sealed partial class MainWindow : Window
{
    private MainWindowViewModel? _viewModel;
    private double _sidebarWidth;
    private double _inspectorWidth;
    private bool _closeConfirmed;
    private bool _closing;

    public MainWindow()
    {
        InitializeComponent();
        ApplyLayout(new LayoutSettings());
    }

    /// <summary>Applies persisted window and panel sizes.</summary>
    public void ApplyLayout(LayoutSettings layout)
    {
        ArgumentNullException.ThrowIfNull(layout);

        Width = layout.WindowWidth;
        Height = layout.WindowHeight;
        if (layout.IsWindowMaximized)
        {
            WindowState = WindowState.Maximized;
        }

        _sidebarWidth = layout.SidebarWidth;
        _inspectorWidth = layout.InspectorWidth;
        Center.RowDefinitions[0].Height = new GridLength(layout.NoteListHeight);
        Center.RowDefinitions[0].MinHeight = LayoutSettings.MinPanelWidth / 2;
        ApplyPanelWidths();
    }

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        if (_viewModel is not null)
        {
            _viewModel.PropertyChanged -= OnViewModelPropertyChanged;
            _viewModel.ListSearchFocusRequested -= OnListSearchFocusRequested;
        }

        _viewModel = DataContext as MainWindowViewModel;
        if (_viewModel is not null)
        {
            _viewModel.PropertyChanged += OnViewModelPropertyChanged;
            _viewModel.ListSearchFocusRequested += OnListSearchFocusRequested;
            RegisterKeyBindings(_viewModel);
            ApplyPanelWidths();
        }
    }

    protected override void OnOpened(EventArgs e)
    {
        base.OnOpened(e);

        // The platform command modifier (Ctrl / Cmd) is only known once the window is on screen.
        if (_viewModel is not null)
        {
            RegisterKeyBindings(_viewModel);
        }
    }

    protected override void OnClosing(WindowClosingEventArgs e)
    {
        base.OnClosing(e);
        if (_closeConfirmed || _viewModel is null)
        {
            return;
        }

        // Closing waits for pending saves: nothing typed may be lost because the window went away.
        e.Cancel = true;
        if (!_closing)
        {
            _closing = true;
            _ = ShutdownThenCloseAsync(_viewModel);
        }
    }

    private async Task ShutdownThenCloseAsync(MainWindowViewModel viewModel)
    {
        try
        {
            if (await viewModel.ShutdownAsync(CaptureLayout()))
            {
                _closeConfirmed = true;
                Close();
            }
        }
        catch (Exception exception)
        {
            // Nobody awaits this task, so an unexpected failure would vanish and leave a window that
            // silently refuses to close. It is raised on the UI thread instead, where it is logged
            // and handled like any other unhandled error.
            var captured = ExceptionDispatchInfo.Capture(exception);
            Dispatcher.UIThread.Post(captured.Throw);
        }
        finally
        {
            _closing = false;
        }
    }

    private void OnListSearchFocusRequested(object? sender, EventArgs e) => NoteList.FocusSearch();

    private WindowLayout CaptureLayout()
    {
        RememberPanelWidths();
        var maximized = WindowState == WindowState.Maximized;
        var defaults = _viewModel?.Layout ?? new LayoutSettings();
        return new WindowLayout(
            _sidebarWidth,
            _inspectorWidth,
            Center.RowDefinitions[0].ActualHeight > 0 ? Center.RowDefinitions[0].ActualHeight : defaults.NoteListHeight,
            maximized ? defaults.WindowWidth : Width,
            maximized ? defaults.WindowHeight : Height,
            maximized);
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(MainWindowViewModel.IsSidebarCollapsed) or nameof(MainWindowViewModel.IsInspectorCollapsed))
        {
            RememberPanelWidths();
            ApplyPanelWidths();
        }
    }

    /// <summary>Keeps the width a panel had before it was collapsed, so expanding restores it.</summary>
    private void RememberPanelWidths()
    {
        var columns = Shell.ColumnDefinitions;
        if (columns[0].ActualWidth > 0)
        {
            _sidebarWidth = columns[0].ActualWidth;
        }

        if (columns[4].ActualWidth > 0)
        {
            _inspectorWidth = columns[4].ActualWidth;
        }
    }

    private void ApplyPanelWidths()
    {
        var sidebarCollapsed = _viewModel?.IsSidebarCollapsed ?? false;
        var inspectorCollapsed = _viewModel?.IsInspectorCollapsed ?? false;
        var columns = Shell.ColumnDefinitions;

        columns[0].Width = sidebarCollapsed ? new GridLength(0) : new GridLength(_sidebarWidth);
        columns[0].MinWidth = sidebarCollapsed ? 0 : LayoutSettings.MinPanelWidth;
        columns[0].MaxWidth = LayoutSettings.MaxPanelWidth;
        columns[4].Width = inspectorCollapsed ? new GridLength(0) : new GridLength(_inspectorWidth);
        columns[4].MinWidth = inspectorCollapsed ? 0 : LayoutSettings.MinPanelWidth;
        columns[4].MaxWidth = LayoutSettings.MaxPanelWidth;
    }

    private void RegisterKeyBindings(MainWindowViewModel viewModel)
    {
        KeyBindings.Clear();
        var primary = this.GetPlatformSettings()?.HotkeyConfiguration.CommandModifiers
            ?? (OperatingSystem.IsMacOS() ? KeyModifiers.Meta : KeyModifiers.Control);

        foreach (var command in viewModel.Commands)
        {
            if (command.Shortcut is not { } shortcut)
            {
                continue;
            }

            var modifiers = (shortcut.Primary ? primary : KeyModifiers.None)
                | (shortcut.Shift ? KeyModifiers.Shift : KeyModifiers.None)
                | (shortcut.Alt ? KeyModifiers.Alt : KeyModifiers.None);
            var guarded = new ShortcutCommand(viewModel, command.Command);
            foreach (var key in ExpandKeys(shortcut.Key))
            {
                KeyBindings.Add(new KeyBinding { Gesture = new KeyGesture(key, modifiers), Command = guarded });
            }
        }
    }

    /// <summary>Keys that exist twice on a keyboard (main block and numeric pad) are bound in both places.</summary>
    private static Key[] ExpandKeys(string name) => name switch
    {
        "OemPlus" => [Key.OemPlus, Key.Add],
        "OemMinus" => [Key.OemMinus, Key.Subtract],
        "D0" => [Key.D0, Key.NumPad0],
        _ => [Enum.Parse<Key>(name)],
    };

    /// <summary>
    /// Shortcut wrapper: while a modal dialog is open, window shortcuts do nothing; an open quick-open
    /// overlay is dismissed before another command takes over.
    /// </summary>
    private sealed class ShortcutCommand(MainWindowViewModel viewModel, ICommand inner) : ICommand
    {
        public event EventHandler? CanExecuteChanged
        {
            add => inner.CanExecuteChanged += value;
            remove => inner.CanExecuteChanged -= value;
        }

        public bool CanExecute(object? parameter) => !viewModel.Dialogs.IsOpen && inner.CanExecute(parameter);

        public void Execute(object? parameter)
        {
            if (viewModel.Dialogs.IsOpen)
            {
                return;
            }

            if (viewModel.QuickOpen.IsOpen)
            {
                viewModel.QuickOpen.Close();
            }

            inner.Execute(parameter);
        }
    }
}
