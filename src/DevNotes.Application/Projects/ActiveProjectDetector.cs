using DevNotes.Application.Abstractions;
using DevNotes.Application.Settings;
using DevNotes.Domain.Common;

namespace DevNotes.Application.Projects;

/// <summary>The project the user is working on, as detected from the repository the app was opened in.</summary>
/// <param name="Name">Project name as used in the notes.</param>
/// <param name="Repository">The repository that identified it.</param>
/// <param name="IsConfigured">True when a configured repository path matched; false when only the folder name did.</param>
public sealed record ActiveProject(string Name, GitRepositoryInfo Repository, bool IsConfigured);

/// <summary>
/// Maps a Git repository to a project: first through the repository paths configured in the
/// settings, then by the name of the repository folder when a project with that name exists.
/// </summary>
public static class ActiveProjectDetector
{
    private static readonly StringComparison _pathComparison =
        OperatingSystem.IsLinux() ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;

    /// <param name="repository">The repository found around the start folder, or null.</param>
    /// <param name="projects">Project settings (configured repository paths).</param>
    /// <param name="knownProjects">Names of the projects that exist in the vault (from the facets).</param>
    public static ActiveProject? Detect(GitRepositoryInfo? repository, IReadOnlyList<ProjectSettings> projects, IReadOnlyCollection<string> knownProjects)
    {
        ArgumentNullException.ThrowIfNull(projects);
        ArgumentNullException.ThrowIfNull(knownProjects);
        if (repository is null)
        {
            return null;
        }

        var root = Path.TrimEndingDirectorySeparator(repository.RootPath);
        foreach (var project in projects)
        {
            if (project.RepositoryPath is { } configured && IsSameOrInside(root, configured))
            {
                return new ActiveProject(project.Name, repository, IsConfigured: true);
            }
        }

        var folderName = Path.GetFileName(root);
        if (folderName.Length == 0)
        {
            return null;
        }

        var key = TextKey.Of(folderName);
        var byName = knownProjects.FirstOrDefault(name => TextKey.Of(name) == key);
        return byName is null ? null : new ActiveProject(byName, repository, IsConfigured: false);
    }

    /// <summary>True when <paramref name="root"/> is the configured folder or lies inside it (a nested worktree, a monorepo).</summary>
    private static bool IsSameOrInside(string root, string configured)
    {
        string configuredFull;
        try
        {
            configuredFull = Path.TrimEndingDirectorySeparator(Path.GetFullPath(configured));
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }

        if (string.Equals(root, configuredFull, _pathComparison))
        {
            return true;
        }

        return root.Length > configuredFull.Length
            && root.StartsWith(configuredFull, _pathComparison)
            && (root[configuredFull.Length] == Path.DirectorySeparatorChar || root[configuredFull.Length] == Path.AltDirectorySeparatorChar);
    }
}
