namespace DevNotes.Application.Settings;

/// <summary>Persistence of <see cref="AppSettings"/>. Implementations write atomically.</summary>
public interface ISettingsStore
{
    /// <summary>Returns the stored settings, or defaults when nothing has been saved yet.</summary>
    Task<AppSettings> LoadAsync(CancellationToken cancellationToken);

    Task SaveAsync(AppSettings settings, CancellationToken cancellationToken);
}

/// <summary>In-memory view of the current settings with serialized, persisted updates.</summary>
public interface ISettingsService
{
    AppSettings Current { get; }

    /// <summary>Raised after the settings were replaced. May be raised on any thread.</summary>
    event EventHandler<AppSettings>? Changed;

    Task LoadAsync(CancellationToken cancellationToken);

    /// <summary>Applies <paramref name="update"/> to the current settings and persists the result.</summary>
    Task<AppSettings> UpdateAsync(Func<AppSettings, AppSettings> update, CancellationToken cancellationToken);
}
