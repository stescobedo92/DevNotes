using DevNotes.Application.Abstractions;
using DevNotes.Application.Settings;
using DevNotes.Infrastructure.Indexing;
using DevNotes.Infrastructure.Logging;
using DevNotes.Infrastructure.Settings;
using DevNotes.Infrastructure.Storage;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;

namespace DevNotes.Infrastructure;

public static class DependencyInjection
{
    /// <summary>Registers the file system, SQLite and settings adapters behind the application ports.</summary>
    /// <param name="services">Service collection to add to.</param>
    /// <param name="dataDirectory">Overrides the app data folder (tests, portable installs); null uses the platform default.</param>
    public static IServiceCollection AddDevNotesInfrastructure(this IServiceCollection services, string? dataDirectory = null)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.TryAddSingleton(TimeProvider.System);
        services.TryAddSingleton<IAppPaths>(_ => new AppPaths(dataDirectory));
        services.TryAddSingleton<ISettingsStore, JsonSettingsStore>();
        services.TryAddSingleton<INoteFileStoreFactory, VaultFileStoreFactory>();
        services.TryAddSingleton<INoteIndexFactory, SqliteNoteIndexFactory>();
        services.TryAddSingleton<IVaultWatcherFactory, FileSystemVaultWatcherFactory>();
        return services;
    }

    /// <summary>Adds the rolling file logger that writes under the app log folder.</summary>
    public static ILoggingBuilder AddDevNotesFile(this ILoggingBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);
        builder.Services.TryAddEnumerable(ServiceDescriptor.Singleton<ILoggerProvider, FileLoggerProvider>());
        return builder;
    }
}
