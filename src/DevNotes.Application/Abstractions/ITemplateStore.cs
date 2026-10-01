namespace DevNotes.Application.Abstractions;

/// <summary>
/// The templates a vault keeps under <c>.devnotes/templates</c>: one Markdown file per key.
/// Implementations write atomically and never follow links out of the vault.
/// </summary>
public interface ITemplateStore
{
    /// <summary>Every stored template, by key. Files with an invalid name or beyond the size limit are ignored.</summary>
    Task<IReadOnlyDictionary<string, string>> LoadAllAsync(CancellationToken cancellationToken);

    Task SaveAsync(string key, string text, CancellationToken cancellationToken);

    /// <summary>Removes a stored template; nothing happens when there is none.</summary>
    Task DeleteAsync(string key, CancellationToken cancellationToken);
}

public interface ITemplateStoreFactory
{
    ITemplateStore Create(string vaultRootPath);
}
