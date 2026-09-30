using DevNotes.Application;
using DevNotes.Application.Indexing;
using DevNotes.Desktop.Services;
using DevNotes.Desktop.ViewModels;
using DevNotes.Desktop.ViewModels.Dialogs;
using DevNotes.Infrastructure;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace DevNotes.Desktop;

/// <summary>Composition root: configuration, logging and every service of the app.</summary>
public static class AppHost
{
    public static IHost Build(string[] args)
    {
        // The empty builder skips host defaults the app does not need (environment providers,
        // configuration file watchers, console logging), which keeps cold start short.
        var builder = Host.CreateEmptyApplicationBuilder(new HostApplicationBuilderSettings
        {
            Args = args,
            ContentRootPath = AppContext.BaseDirectory,
        });

        builder.Configuration
            .AddJsonFile("appsettings.json", optional: true, reloadOnChange: false)
            .AddEnvironmentVariables(prefix: "DEVNOTES_");

        builder.Logging
            .AddConfiguration(builder.Configuration.GetSection("Logging"))
            .AddDevNotesFile();
#if DEBUG
        builder.Logging.AddDebug();
#endif

        builder.Services
            .AddDevNotesInfrastructure()
            .AddDevNotesApplication()
            .AddDevNotesDesktop(builder.Configuration);

        return builder.Build();
    }

    public static IServiceCollection AddDevNotesDesktop(this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        services.Configure<IndexingOptions>(configuration.GetSection(IndexingOptions.SectionName));
        services.Configure<EditorOptions>(configuration.GetSection(EditorOptions.SectionName));
        services.AddSingleton(provider => provider.GetRequiredService<IOptions<EditorOptions>>().Value);

        // Platform services
        services.AddSingleton<TopLevelAccessor>();
        services.AddSingleton<IUiDispatcher, AvaloniaUiDispatcher>();
        services.AddSingleton<IFolderPicker, AvaloniaFolderPicker>();
        services.AddSingleton<IClipboardService, AvaloniaClipboardService>();
        services.AddSingleton<ILinkOpener, AvaloniaLinkOpener>();
        services.AddSingleton<IThemeService, ThemeService>();
        services.AddSingleton<ICodeHighlighter, TextMateCodeHighlighter>();

        // View models: one instance each, they live as long as the main window.
        services.AddSingleton<DialogHostViewModel>();
        services.AddSingleton<IDialogService>(provider => provider.GetRequiredService<DialogHostViewModel>());
        services.AddSingleton<NotificationViewModel>();
        services.AddSingleton<INotificationService>(provider => provider.GetRequiredService<NotificationViewModel>());
        services.AddSingleton<NoteListViewModel>();
        services.AddSingleton<NoteEditorViewModel>();
        services.AddSingleton<QuickOpenViewModel>();
        services.AddSingleton<MainWindowViewModel>();
        return services;
    }
}
