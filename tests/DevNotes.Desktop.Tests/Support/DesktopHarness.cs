using Avalonia.Threading;
using DevNotes.Application;
using DevNotes.Application.Indexing;
using DevNotes.Application.Settings;
using DevNotes.Application.Vaults;
using DevNotes.Desktop.Services;
using DevNotes.Desktop.ViewModels;
using DevNotes.Desktop.ViewModels.Dialogs;
using DevNotes.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace DevNotes.Desktop.Tests.Support;

/// <summary>
/// The application composed as in production (real file store, real SQLite index, real view
/// models) over temporary folders, with only the operating-system services replaced by fakes.
/// </summary>
public sealed class DesktopHarness : IAsyncDisposable
{
    private readonly ServiceProvider _services;

    private DesktopHarness(IUiDispatcher dispatcher, Action<IServiceCollection>? configure)
    {
        var services = new ServiceCollection()
            .AddLogging()
            .AddSingleton(Options.Create(new IndexingOptions { WatcherDebounceMilliseconds = 50 }))
            .AddDevNotesInfrastructure(DataFolder.Path)
            .AddDevNotesApplication();

        services.AddSingleton(new EditorOptions { AutosaveDelayMilliseconds = 60_000, PreviewDelayMilliseconds = 0 });
        services.AddSingleton(dispatcher);
        services.AddSingleton<IFolderPicker>(FolderPicker);
        services.AddSingleton<IClipboardService>(Clipboard);
        services.AddSingleton<ILinkOpener>(Links);
        services.AddSingleton<IThemeService>(Theme);
        services.AddSingleton<ICodeHighlighter, TextMateCodeHighlighter>();
        services.AddSingleton<DialogHostViewModel>();
        services.AddSingleton<IDialogService>(provider => provider.GetRequiredService<DialogHostViewModel>());
        services.AddSingleton<NotificationViewModel>();
        services.AddSingleton<INotificationService>(provider => provider.GetRequiredService<NotificationViewModel>());
        services.AddSingleton<NoteListViewModel>();
        services.AddSingleton<NoteEditorViewModel>();
        services.AddSingleton<QuickOpenViewModel>();
        services.AddSingleton<MainWindowViewModel>();
        configure?.Invoke(services);
        _services = services.BuildServiceProvider();
    }

    public TempDirectory VaultFolder { get; } = new("devnotes-ui-vault-");

    public TempDirectory DataFolder { get; } = new("devnotes-ui-data-");

    public FakeFolderPicker FolderPicker { get; } = new();

    public FakeClipboard Clipboard { get; } = new();

    public FakeLinkOpener Links { get; } = new();

    public FakeThemeService Theme { get; } = new();

    public MainWindowViewModel Shell => _services.GetRequiredService<MainWindowViewModel>();

    public ISettingsService Settings => _services.GetRequiredService<ISettingsService>();

    public IVaultSession Session => _services.GetRequiredService<IVaultSessionManager>().Current
        ?? throw new InvalidOperationException("No vault is open.");

    /// <param name="dispatcher">UI dispatcher for the view models.</param>
    /// <param name="openVault">When true the temporary vault is registered and opened; otherwise the app starts on the welcome screen.</param>
    /// <param name="seed">Writes the initial notes of the vault before it is opened.</param>
    /// <param name="configure">Replaces services of the production composition (the last registration wins).</param>
    public static async Task<DesktopHarness> StartAsync(
        IUiDispatcher dispatcher,
        bool openVault = true,
        Action<TempDirectory>? seed = null,
        Action<IServiceCollection>? configure = null)
    {
        var harness = new DesktopHarness(dispatcher, configure);
        seed?.Invoke(harness.VaultFolder);
        await harness.Settings.LoadAsync(CancellationToken.None);
        if (openVault)
        {
            await harness._services.GetRequiredService<IVaultRegistry>().AddAsync(harness.VaultFolder.Path, CancellationToken.None);
        }

        await harness.Shell.InitializeAsync();
        if (openVault)
        {
            await harness.SettleAsync();
        }

        return harness;
    }

    /// <summary>Waits until the index reflects everything reported so far and the list has been refreshed.</summary>
    public async Task SettleAsync()
    {
        await Session.WhenIdleAsync(CancellationToken.None);

        // The indexer has already posted its notifications to the UI thread, and the shell answers
        // each one with a refresh of its own that would overtake (cancel) the one started here.
        // Deliver them first, then wait for whichever refresh ends up being the latest.
        Dispatcher.UIThread.RunJobs();
        await Shell.NoteList.RefreshAsync();
        await Shell.NoteList.WhenSettledAsync();
    }

    public async ValueTask DisposeAsync()
    {
        await _services.DisposeAsync();
        VaultFolder.Dispose();
        DataFolder.Dispose();
    }
}
