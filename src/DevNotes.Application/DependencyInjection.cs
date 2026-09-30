using DevNotes.Application.Settings;
using DevNotes.Application.Vaults;
using DevNotes.Domain.Notes;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace DevNotes.Application;

public static class DependencyInjection
{
    /// <summary>
    /// Registers the application services. The infrastructure ports (file store, index, watcher and
    /// settings store factories) and <c>IOptions&lt;IndexingOptions&gt;</c> must be registered by the host.
    /// </summary>
    public static IServiceCollection AddDevNotesApplication(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.TryAddSingleton(TimeProvider.System);
        services.TryAddSingleton<INoteIdGenerator, UlidNoteIdGenerator>();
        services.TryAddSingleton<ISettingsService, SettingsService>();
        services.TryAddSingleton<IVaultRegistry, VaultRegistry>();
        services.TryAddSingleton<IVaultSessionFactory, VaultSessionFactory>();
        services.TryAddSingleton<IVaultSessionManager, VaultSessionManager>();
        return services;
    }
}
