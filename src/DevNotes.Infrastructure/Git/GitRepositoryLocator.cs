using System.Text;
using DevNotes.Application.Abstractions;

namespace DevNotes.Infrastructure.Git;

/// <summary>
/// Reads just enough of a repository to know where it is and what is checked out: the <c>.git</c>
/// folder (or the <c>gitdir:</c> file of a worktree or submodule) and its <c>HEAD</c>. It never
/// throws: anything unreadable simply means "no repository".
/// </summary>
public sealed class GitRepositoryLocator : IGitRepositoryLocator
{
    private const int MaxDepth = 64;
    private const int MaxHeadBytes = 4096;
    private const int ShortHashLength = 7;

    public GitRepositoryInfo? Find(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        try
        {
            var directory = StartDirectory(path);
            for (var depth = 0; directory is not null && depth < MaxDepth; depth++)
            {
                var gitDirectory = ResolveGitDirectory(directory.FullName);
                if (gitDirectory is not null)
                {
                    var (branch, commit) = ReadHead(gitDirectory);
                    return new GitRepositoryInfo(Path.TrimEndingDirectorySeparator(directory.FullName), branch, commit);
                }

                directory = directory.Parent;
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            // A path on a drive that is gone, a folder without permission, a malformed path: no repository.
        }

        return null;
    }

    private static DirectoryInfo? StartDirectory(string path)
    {
        var full = Path.GetFullPath(path);
        if (Directory.Exists(full))
        {
            return new DirectoryInfo(full);
        }

        return File.Exists(full) ? new FileInfo(full).Directory : null;
    }

    /// <summary>The folder that holds HEAD: <c>.git</c> itself, or wherever a <c>.git</c> file points.</summary>
    private static string? ResolveGitDirectory(string workingTree)
    {
        var entry = Path.Combine(workingTree, ".git");
        if (Directory.Exists(entry))
        {
            return entry;
        }

        if (!File.Exists(entry))
        {
            return null;
        }

        // Worktrees and submodules: a one-line file "gitdir: <path>", relative to the file's folder.
        var content = ReadSmallFile(entry);
        const string Prefix = "gitdir:";
        if (content is null || !content.StartsWith(Prefix, StringComparison.Ordinal))
        {
            return null;
        }

        var target = content[Prefix.Length..].Trim();
        var gitDirectory = Path.GetFullPath(Path.IsPathRooted(target) ? target : Path.Combine(workingTree, target));
        return Directory.Exists(gitDirectory) ? gitDirectory : null;
    }

    private static (string? Branch, string? Commit) ReadHead(string gitDirectory)
    {
        var head = ReadSmallFile(Path.Combine(gitDirectory, "HEAD"))?.Trim();
        if (string.IsNullOrEmpty(head))
        {
            return (null, null);
        }

        const string RefPrefix = "ref: refs/heads/";
        if (head.StartsWith(RefPrefix, StringComparison.Ordinal))
        {
            var branch = head[RefPrefix.Length..].Trim();
            return branch.Length > 0 ? (branch, null) : (null, null);
        }

        if (head.StartsWith("ref: ", StringComparison.Ordinal))
        {
            return (head[5..].Trim(), null); // A ref outside refs/heads (rare); show it as it is.
        }

        // Detached HEAD: a full object hash.
        return head.Length >= ShortHashLength && head.All(char.IsAsciiHexDigit) ? (null, head[..ShortHashLength]) : (null, null);
    }

    private static string? ReadSmallFile(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, bufferSize: 1, FileOptions.SequentialScan);
        if (stream.Length > MaxHeadBytes)
        {
            return null;
        }

        var buffer = new byte[stream.Length];
        stream.ReadExactly(buffer);
        return Encoding.UTF8.GetString(buffer);
    }
}
