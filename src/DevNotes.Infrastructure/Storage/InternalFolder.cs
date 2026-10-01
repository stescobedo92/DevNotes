namespace DevNotes.Infrastructure.Storage;

/// <summary>
/// The app's own folder inside a vault (<c>.devnotes</c>): the trash and the templates live there.
/// It is never used through links: a vault cloned from a repository could ship <c>.devnotes</c>
/// as a link to any other folder, and the app would then write to (or delete from) that folder.
/// </summary>
internal static class InternalFolder
{
    public const string Name = ".devnotes";

    /// <summary>
    /// Keeps the trash out of the user's Git history when the vault is a repository, while the
    /// templates (meant to be shared) stay versionable.
    /// </summary>
    public const string GitIgnore = "# DevNotes: the trash is local to this machine; templates are meant to be shared.\ntrash/\n*.tmp\n";

    public static string PathOf(string vaultRoot, params string[] parts) => Path.Combine([vaultRoot, Name, .. parts]);

    /// <summary>Creates <c>.devnotes</c> (with its <c>.gitignore</c>) and the given sub-folder, refusing links.</summary>
    public static string Ensure(string vaultRoot, string subFolder)
    {
        EnsureNotLinked(vaultRoot, subFolder);
        var folder = PathOf(vaultRoot, subFolder);
        Directory.CreateDirectory(folder);
        EnsureNotLinked(vaultRoot, subFolder);
        WriteGitIgnore(vaultRoot);
        return folder;
    }

    /// <summary>Throws when <c>.devnotes</c> or the sub-folder is a link.</summary>
    public static void EnsureNotLinked(string vaultRoot, string subFolder)
    {
        if (IsLink(new DirectoryInfo(PathOf(vaultRoot))) || IsLink(new DirectoryInfo(PathOf(vaultRoot, subFolder))))
        {
            throw new UnauthorizedAccessException($"'{Name}' in this vault is a link to another folder; it is not used through links.");
        }
    }

    // Cloud-sync placeholders (OneDrive…) are reparse points too but have no link target; only real links are rejected.
    public static bool IsLink(FileSystemInfo info) =>
        info.Exists && (info.Attributes & FileAttributes.ReparsePoint) != 0 && info.LinkTarget is not null;

    private static void WriteGitIgnore(string vaultRoot)
    {
        var gitignore = PathOf(vaultRoot, ".gitignore");
        try
        {
            // CreateNew never writes through an existing entry, be it a file or a (dangling) link.
            using var stream = new FileStream(gitignore, FileMode.CreateNew, FileAccess.Write, FileShare.None);
            stream.Write(System.Text.Encoding.UTF8.GetBytes(GitIgnore));
        }
        catch (IOException) when (File.Exists(gitignore) || new FileInfo(gitignore).LinkTarget is not null)
        {
            // Already there; whatever the user put in it is theirs.
        }
    }
}
