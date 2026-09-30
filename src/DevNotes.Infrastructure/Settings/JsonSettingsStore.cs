using System.Globalization;
using System.Text.Json;
using DevNotes.Application.Settings;
using DevNotes.Infrastructure.Storage;
using Microsoft.Extensions.Logging;

namespace DevNotes.Infrastructure.Settings;

/// <summary>
/// Stores the settings as a human-readable JSON file written atomically. An unreadable file is
/// kept aside as a backup and replaced by defaults: a broken settings file must never prevent
/// the app from starting, and nothing the user wrote is deleted.
/// </summary>
public sealed partial class JsonSettingsStore : ISettingsStore
{
    private readonly IAppPaths _paths;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger _logger;

    public JsonSettingsStore(IAppPaths paths, TimeProvider timeProvider, ILogger<JsonSettingsStore> logger)
    {
        _paths = paths ?? throw new ArgumentNullException(nameof(paths));
        _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public async Task<AppSettings> LoadAsync(CancellationToken cancellationToken)
    {
        byte[] bytes;
        try
        {
            bytes = await File.ReadAllBytesAsync(_paths.SettingsFile, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is FileNotFoundException or DirectoryNotFoundException)
        {
            return new AppSettings(); // First run.
        }

        try
        {
            return JsonSerializer.Deserialize(bytes, SettingsJsonContext.Default.SettingsFileModel)?.ToSettings() ?? new AppSettings();
        }
        catch (JsonException exception)
        {
            var backup = BackUpUnreadableFile();
            LogSettingsUnreadable(exception, _paths.SettingsFile, backup);
            return new AppSettings();
        }
    }

    public async Task SaveAsync(AppSettings settings, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(settings);

        Directory.CreateDirectory(_paths.DataDirectory);
        var bytes = JsonSerializer.SerializeToUtf8Bytes(SettingsFileModel.From(settings), SettingsJsonContext.Default.SettingsFileModel);
        await AtomicFile.WriteAsync(_paths.SettingsFile, bytes, overwrite: true, cancellationToken).ConfigureAwait(false);
    }

    private string BackUpUnreadableFile()
    {
        var stamp = _timeProvider.GetUtcNow().ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
        var backup = Path.Combine(_paths.DataDirectory, $"settings.unreadable-{stamp}.json");
        File.Move(_paths.SettingsFile, backup, overwrite: true);
        return backup;
    }

    [LoggerMessage(EventId = 600, Level = LogLevel.Warning,
        Message = "The settings file '{Path}' could not be read; defaults are used and the file was kept as '{Backup}'")]
    private partial void LogSettingsUnreadable(Exception exception, string path, string backup);
}
