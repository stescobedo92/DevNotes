namespace DevNotes.Application.Abstractions;

/// <summary>Where a Git working tree starts and what is checked out.</summary>
/// <param name="RootPath">Absolute path of the working tree (the folder that contains <c>.git</c>).</param>
/// <param name="Branch">Name of the checked-out branch, or null when HEAD is detached.</param>
/// <param name="Commit">Short hash of the checked-out commit when HEAD is detached; otherwise null.</param>
public sealed record GitRepositoryInfo(string RootPath, string? Branch, string? Commit)
{
    /// <summary>What to show for the checkout: the branch, or the short commit when detached.</summary>
    public string? Display => Branch ?? Commit;
}

/// <summary>Finds the Git repository a path belongs to by reading the <c>.git</c> entry, without any Git library.</summary>
public interface IGitRepositoryLocator
{
    /// <summary>The repository containing <paramref name="path"/> (a file or folder), or null when there is none or it cannot be read.</summary>
    GitRepositoryInfo? Find(string? path);
}
