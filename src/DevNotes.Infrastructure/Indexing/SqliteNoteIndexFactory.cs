using DevNotes.Application.Abstractions;
using DevNotes.Domain.Vaults;
using Microsoft.Extensions.Logging;

namespace DevNotes.Infrastructure.Indexing;

/// <summary>
/// Keeps one index database per vault under the app data folder (never inside the vault, so the
/// WAL files are not synced or committed together with the notes).
/// </summary>
public sealed class SqliteNoteIndexFactory(IAppPaths paths, ILoggerFactory loggerFactory) : INoteIndexFactory
{
    public INoteIndex Open(VaultId vaultId) =>
        SqliteNoteIndex.ForFile(GetDatabasePath(vaultId), loggerFactory.CreateLogger<SqliteNoteIndex>());

    public Task DeleteAsync(VaultId vaultId, CancellationToken cancellationToken) =>
        Task.Run(() => SqliteNoteIndex.DeleteDatabaseFiles(GetDatabasePath(vaultId)), cancellationToken);

    /// <summary>A <see cref="VaultId"/> is a safe token, so it cannot escape the index directory.</summary>
    internal string GetDatabasePath(VaultId vaultId)
    {
        if (vaultId.IsEmpty)
        {
            throw new ArgumentException("The vault id is empty.", nameof(vaultId));
        }

        return Path.Combine(paths.IndexDirectory, vaultId.Value + ".db");
    }
}
