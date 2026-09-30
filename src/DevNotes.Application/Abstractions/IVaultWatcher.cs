namespace DevNotes.Application.Abstractions;

public enum VaultFileEventKind
{
    /// <summary>A file or folder appeared (created, copied or moved into the vault).</summary>
    Created,

    /// <summary>The content of a file changed.</summary>
    Changed,

    Deleted,

    Renamed,

    /// <summary>Events were lost (buffer overflow or watcher failure); a full rescan is required.</summary>
    Overflow,
}

/// <summary>
/// Raw file-system notification with paths relative to the vault root (forward slashes).
/// Paths are not validated: they may point to folders or to files that are not notes.
/// </summary>
public readonly record struct VaultFileEvent(VaultFileEventKind Kind, string RelativePath, string? OldRelativePath = null);

/// <summary>Watches a vault folder for external changes. Events may be raised on any thread.</summary>
public interface IVaultWatcher : IDisposable
{
    event EventHandler<VaultFileEvent>? Changed;

    void Start();
}

public interface IVaultWatcherFactory
{
    IVaultWatcher Create(string vaultRootPath);
}
