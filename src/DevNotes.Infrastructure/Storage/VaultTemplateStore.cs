using DevNotes.Application.Abstractions;
using DevNotes.Application.Notes;

namespace DevNotes.Infrastructure.Storage;

/// <summary>
/// Templates stored as <c>.devnotes/templates/&lt;key&gt;.md</c> inside the vault, so a team can
/// version them with the notes. Files are written atomically; links are never followed.
/// </summary>
public sealed class VaultTemplateStore : ITemplateStore
{
    public const string FolderName = "templates";

    /// <summary>A template beyond this size is ignored: it is not a template but a mistake (or an attack on the app).</summary>
    public const int MaxFileBytes = TemplateService.MaxTemplateLength * 4;

    private readonly string _root;
    private readonly string _folder;

    public VaultTemplateStore(string vaultRootPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(vaultRootPath);
        _root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(vaultRootPath));
        _folder = InternalFolder.PathOf(_root, FolderName);
    }

    public async Task<IReadOnlyDictionary<string, string>> LoadAllAsync(CancellationToken cancellationToken)
    {
        var templates = new Dictionary<string, string>(StringComparer.Ordinal);
        var folder = new DirectoryInfo(_folder);
        if (!folder.Exists)
        {
            return templates;
        }

        foreach (var file in folder.EnumerateFiles("*.md", SearchOption.TopDirectoryOnly))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var key = Path.GetFileNameWithoutExtension(file.Name);
            if (!TemplateKey.IsValid(key) || InternalFolder.IsLink(file) || file.Length > MaxFileBytes)
            {
                continue;
            }

            var bytes = await File.ReadAllBytesAsync(file.FullName, cancellationToken).ConfigureAwait(false);
            var text = NoteTextCodec.Decode(bytes, out _);
            if (text.Length <= TemplateService.MaxTemplateLength)
            {
                templates[key] = text;
            }
        }

        return templates;
    }

    public Task SaveAsync(string key, string text, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(text);
        RequireKey(key);

        var folder = InternalFolder.Ensure(_root, FolderName);
        var bytes = NoteTextCodec.Encode(text, NoteTextEncoding.Utf8, out _);
        return AtomicFile.WriteAsync(Path.Combine(folder, key + ".md"), bytes, overwrite: true, cancellationToken);
    }

    public Task DeleteAsync(string key, CancellationToken cancellationToken)
    {
        RequireKey(key);
        cancellationToken.ThrowIfCancellationRequested();

        var file = new FileInfo(Path.Combine(_folder, key + ".md"));
        if (file.Exists)
        {
            InternalFolder.EnsureNotLinked(_root, FolderName);
            file.Delete(); // A link is unlinked, never followed.
        }

        return Task.CompletedTask;
    }

    private static void RequireKey(string key)
    {
        if (!TemplateKey.IsValid(key))
        {
            throw new ArgumentException($"'{key}' is not a valid template key.", nameof(key));
        }
    }
}

public sealed class VaultTemplateStoreFactory : ITemplateStoreFactory
{
    public ITemplateStore Create(string vaultRootPath) => new VaultTemplateStore(vaultRootPath);
}
