using System.Globalization;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using DevNotes.Application.Settings;
using DevNotes.Desktop.Resources;
using DevNotes.Desktop.Services;
using DevNotes.Desktop.ViewModels;
using DevNotes.Desktop.Views;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace DevNotes.Desktop;

// Fully qualified: inside this assembly `Application` binds to the DevNotes.Application namespace.
public sealed partial class App : Avalonia.Application
{
    private IHost? _host;
    private ILogger? _logger;

    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        // Only the real desktop lifetime builds the host; headless UI tests compose what they need.
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            _host = AppHost.Build(desktop.Args ?? []);
            _logger = _host.Services.GetRequiredService<ILogger<App>>();
            RegisterExceptionLogging();

            desktop.ShutdownMode = Avalonia.Controls.ShutdownMode.OnMainWindowClose;
            desktop.Exit += OnExit;
            _ = StartAsync(desktop, _host.Services);
        }

        base.OnFrameworkInitializationCompleted();
    }

    /// <summary>
    /// Start-up sequence. The settings are read before any window exists because both the UI
    /// language and the theme depend on them (no flash of the wrong theme, no restart needed).
    /// </summary>
    private async Task StartAsync(IClassicDesktopStyleApplicationLifetime desktop, IServiceProvider services)
    {
        try
        {
            await StartCoreAsync(desktop, services);
        }
        catch (Exception exception)
        {
            // Without a window there is nothing the user could do: record why and exit with an error code.
            LogUnhandled(exception, "start-up");
            desktop.Shutdown(1);
            throw;
        }
    }

    private async Task StartCoreAsync(IClassicDesktopStyleApplicationLifetime desktop, IServiceProvider services)
    {
        var settings = services.GetRequiredService<ISettingsService>();
        try
        {
            await settings.LoadAsync(CancellationToken.None);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // Defaults are used for this session; the failure is logged and the app still starts.
            LogSettingsLoadFailed(exception);
        }

        ApplyLanguage(settings.Current.Language);
        services.GetRequiredService<IThemeService>().Apply(settings.Current);

        var viewModel = services.GetRequiredService<MainWindowViewModel>();
        var window = new MainWindow { DataContext = viewModel };
        window.ApplyLayout(settings.Current.Layout);
        services.GetRequiredService<TopLevelAccessor>().Current = window;

        desktop.MainWindow = window;
        window.Show();

        // The window is on screen; opening the vault and indexing continue in the background.
        await viewModel.InitializeAsync();
    }

    private static void ApplyLanguage(string? language)
    {
        if (string.IsNullOrWhiteSpace(language))
        {
            return; // Follow the operating system.
        }

        try
        {
            var culture = CultureInfo.GetCultureInfo(language);
            Strings.Culture = culture;
            CultureInfo.DefaultThreadCurrentUICulture = culture;
            CultureInfo.CurrentUICulture = culture;
        }
        catch (CultureNotFoundException)
        {
            // An unknown language code in the settings file falls back to the system language.
        }
    }

    private void RegisterExceptionLogging()
    {
        // Unexpected exceptions are never swallowed: they are logged with full detail. The UI-thread
        // handler leaves them unhandled so the process fails fast instead of running in an unknown state.
        Dispatcher.UIThread.UnhandledException += (_, e) => LogUnhandled(e.Exception, "UI thread");
        TaskScheduler.UnobservedTaskException += (_, e) => LogUnhandled(e.Exception, "unobserved task");
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
        {
            if (e.ExceptionObject is Exception exception)
            {
                LogUnhandled(exception, "application domain");
            }
        };
    }

    private void OnExit(object? sender, ControlledApplicationLifetimeExitEventArgs e)
    {
        if (_host is null)
        {
            return;
        }

        // The window already flushed pending saves; this releases the index, the watcher and the log file.
        var host = _host;
        _host = null;
        Task.Run(async () =>
        {
            if (host is IAsyncDisposable asyncDisposable)
            {
                await asyncDisposable.DisposeAsync();
            }
            else
            {
                host.Dispose();
            }
        }).GetAwaiter().GetResult();
    }

    [LoggerMessage(EventId = 900, Level = LogLevel.Critical, Message = "Unhandled exception ({Source})")]
    private static partial void LogUnhandledCore(ILogger logger, Exception exception, string source);

    [LoggerMessage(EventId = 901, Level = LogLevel.Error, Message = "The settings could not be loaded; defaults are used for this session")]
    private static partial void LogSettingsLoadFailedCore(ILogger logger, Exception exception);

    private void LogUnhandled(Exception exception, string source)
    {
        if (_logger is not null)
        {
            LogUnhandledCore(_logger, exception, source);
        }
    }

    private void LogSettingsLoadFailed(Exception exception)
    {
        if (_logger is not null)
        {
            LogSettingsLoadFailedCore(_logger, exception);
        }
    }
}
