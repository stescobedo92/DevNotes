using DevNotes.Application.Abstractions;
using DevNotes.Application.Settings;
using DevNotes.Domain.Vaults;

namespace DevNotes.Application.Vaults;

public interface IVaultRegistry
{
    /// <summary>Registered vaults whose settings entry is well formed.</summary>
    IReadOnlyList<Vault> Vaults { get; }

    /// <summary>The vault to open at startup, or null when none is registered.</summary>
    Vault? ActiveVault { get; }

    /// <summary>Registers a folder as a vault (or returns the existing registration) and makes it active.</summary>
    Task<Vault> AddAsync(string folderPath, CancellationToken cancellationToken);

    Task SetActiveAsync(VaultId id, CancellationToken cancellationToken);

    /// <summary>Forgets a vault and deletes its index. The notes on disk are never touched.</summary>
    Task RemoveAsync(VaultId id, CancellationToken cancellationToken);
}

public sealed class VaultFolderNotFoundException(string path)
    : DirectoryNotFoundException($"The folder '{path}' does not exist.")
{
    public string FolderPath { get; } = path;
}

public sealed class VaultRegistry(ISettingsService settings, INoteIndexFactory indexFactory, TimeProvider timeProvider) : IVaultRegistry
{
    private static readonly StringComparison _pathComparison =
        OperatingSystem.IsLinux() ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;

    private readonly ISettingsService _settings = settings ?? throw new ArgumentNullException(nameof(settings));
    private readonly INoteIndexFactory _indexFactory = indexFactory ?? throw new ArgumentNullException(nameof(indexFactory));
    private readonly TimeProvider _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));

    public IReadOnlyList<Vault> Vaults => [.. ReadVaults(_settings.Current)];

    public Vault? ActiveVault
    {
        get
        {
            var current = _settings.Current;
            var vaults = ReadVaults(current).ToList();
            return vaults.FirstOrDefault(v => v.Id.Value == current.ActiveVaultId) ?? vaults.FirstOrDefault();
        }
    }

    public async Task<Vault> AddAsync(string folderPath, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(folderPath);

        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(folderPath));
        if (!Directory.Exists(root))
        {
            throw new VaultFolderNotFoundException(root);
        }

        Vault? result = null;
        await _settings.UpdateAsync(
            current =>
            {
                var existing = ReadVaults(current).FirstOrDefault(v => string.Equals(v.RootPath, root, _pathComparison));
                if (existing is not null)
                {
                    result = existing;
                    return current with { ActiveVaultId = existing.Id.Value };
                }

                var name = Path.GetFileName(root);
                result = new Vault(
                    VaultId.NewId(_timeProvider.GetUtcNow()),
                    string.IsNullOrWhiteSpace(name) ? root : name,
                    root);

                return current with
                {
                    Vaults = [.. current.Vaults, new VaultSettings(result.Id.Value, result.Name, result.RootPath)],
                    ActiveVaultId = result.Id.Value,
                };
            },
            cancellationToken).ConfigureAwait(false);

        return result!;
    }

    public Task SetActiveAsync(VaultId id, CancellationToken cancellationToken) =>
        _settings.UpdateAsync(
            current => current.Vaults.Any(v => v.Id == id.Value)
                ? current with { ActiveVaultId = id.Value }
                : throw new InvalidOperationException($"The vault '{id}' is not registered."),
            cancellationToken);

    public async Task RemoveAsync(VaultId id, CancellationToken cancellationToken)
    {
        await _settings.UpdateAsync(
            current =>
            {
                var remaining = current.Vaults.Where(v => v.Id != id.Value).ToList();
                return current with
                {
                    Vaults = remaining,
                    ActiveVaultId = current.ActiveVaultId == id.Value ? remaining.FirstOrDefault()?.Id : current.ActiveVaultId,
                };
            },
            cancellationToken).ConfigureAwait(false);

        await _indexFactory.DeleteAsync(id, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Entries edited by hand into something unusable are ignored instead of crashing the app.</summary>
    private static IEnumerable<Vault> ReadVaults(AppSettings settings)
    {
        foreach (var entry in settings.Vaults)
        {
            if (entry is not null
                && VaultId.TryParse(entry.Id, out var id)
                && !string.IsNullOrWhiteSpace(entry.Name)
                && !string.IsNullOrWhiteSpace(entry.Path)
                && Path.IsPathFullyQualified(entry.Path))
            {
                yield return new Vault(id, entry.Name, entry.Path);
            }
        }
    }
}
