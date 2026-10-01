namespace DevNotes.Desktop.Services;

/// <summary>
/// How the app was started: the folder that identifies the active project, given explicitly
/// (<c>--project-dir</c>, <c>DEVNOTES_PROJECT_DIR</c>) or taken from the working directory when
/// the app was launched from a terminal inside a repository.
/// </summary>
public sealed record LaunchContext(string? ProjectDirectory)
{
    public const string ProjectDirectoryOption = "--project-dir";
    public const string ProjectDirectoryVariable = "DEVNOTES_PROJECT_DIR";

    public static LaunchContext None { get; } = new((string?)null);

    public static LaunchContext FromArguments(IReadOnlyList<string> args)
    {
        ArgumentNullException.ThrowIfNull(args);

        for (var i = 0; i < args.Count; i++)
        {
            var argument = args[i];
            if (argument.Equals(ProjectDirectoryOption, StringComparison.OrdinalIgnoreCase) && i + 1 < args.Count)
            {
                return new LaunchContext(Normalize(args[i + 1]));
            }

            if (argument.StartsWith(ProjectDirectoryOption + "=", StringComparison.OrdinalIgnoreCase))
            {
                return new LaunchContext(Normalize(argument[(ProjectDirectoryOption.Length + 1)..]));
            }
        }

        if (Normalize(Environment.GetEnvironmentVariable(ProjectDirectoryVariable)) is { } fromEnvironment)
        {
            return new LaunchContext(fromEnvironment);
        }

        // A shortcut or launcher starts the app in its own folder, which says nothing about a project.
        var workingDirectory = Normalize(Environment.CurrentDirectory);
        var appDirectory = Normalize(AppContext.BaseDirectory);
        return workingDirectory is null || string.Equals(workingDirectory, appDirectory, StringComparison.OrdinalIgnoreCase)
            ? None
            : new LaunchContext(workingDirectory);
    }

    private static string? Normalize(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        try
        {
            return Path.TrimEndingDirectorySeparator(Path.GetFullPath(path.Trim()));
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return null;
        }
    }
}
