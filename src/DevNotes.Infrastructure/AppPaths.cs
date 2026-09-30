namespace DevNotes.Infrastructure;

/// <summary>Locations of the files the app owns outside the vaults (settings, indexes, logs).</summary>
public interface IAppPaths
{
    string DataDirectory { get; }

    string SettingsFile { get; }

    string IndexDirectory { get; }

    string LogDirectory { get; }
}

public sealed class AppPaths : IAppPaths
{
    /// <summary>Environment variable that relocates all app data (portable installs, tests).</summary>
    public const string DataDirectoryVariable = "DEVNOTES_DATA_DIR";

    /// <param name="dataDirectory">
    /// Root for app data. When null, <see cref="DataDirectoryVariable"/> is honoured and otherwise the
    /// per-user local application data folder of the platform is used.
    /// </param>
    public AppPaths(string? dataDirectory = null)
    {
        var root = dataDirectory ?? Environment.GetEnvironmentVariable(DataDirectoryVariable);
        if (string.IsNullOrWhiteSpace(root))
        {
            root = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData, Environment.SpecialFolderOption.Create),
                "DevNotes");
        }

        DataDirectory = Path.GetFullPath(root);
        SettingsFile = Path.Combine(DataDirectory, "settings.json");
        IndexDirectory = Path.Combine(DataDirectory, "indexes");
        LogDirectory = Path.Combine(DataDirectory, "logs");
    }

    public string DataDirectory { get; }

    public string SettingsFile { get; }

    public string IndexDirectory { get; }

    public string LogDirectory { get; }
}
