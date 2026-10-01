using System.Globalization;
using DevNotes.Application.Abstractions;
using DevNotes.Domain.Notes;

namespace DevNotes.Application.Notes;

public interface ITemplateService
{
    /// <summary>Built-in templates (customized copies take over) followed by the vault's own ones.</summary>
    Task<IReadOnlyList<NoteTemplate>> ListAsync(CancellationToken cancellationToken);

    /// <summary>Returns null when neither the app nor the vault has a template with this key.</summary>
    Task<NoteTemplate?> GetAsync(string key, CancellationToken cancellationToken);

    /// <summary>Stores the text as the vault's version of the template (a new one, or the override of a built-in).</summary>
    Task<NoteTemplate> SaveAsync(string key, string text, CancellationToken cancellationToken);

    /// <summary>Drops the vault's version: a built-in returns to the text the app ships, another template disappears.</summary>
    Task ResetAsync(string key, CancellationToken cancellationToken);
}

/// <summary>Merges the templates the app ships with the ones stored in the vault.</summary>
public sealed class TemplateService(ITemplateStore store) : ITemplateService
{
    public const int MaxTemplateLength = 64 * 1024;

    private readonly ITemplateStore _store = store ?? throw new ArgumentNullException(nameof(store));

    public async Task<IReadOnlyList<NoteTemplate>> ListAsync(CancellationToken cancellationToken)
    {
        var custom = await _store.LoadAllAsync(cancellationToken).ConfigureAwait(false);
        var culture = CultureInfo.CurrentUICulture;
        var templates = new List<NoteTemplate>(BuiltInTemplates.Keys.Count + custom.Count);
        foreach (var key in BuiltInTemplates.Keys)
        {
            templates.Add(custom.TryGetValue(key, out var text)
                ? Custom(key, text, isBuiltIn: true)
                : BuiltIn(key, culture));
        }

        foreach (var (key, text) in custom.OrderBy(pair => pair.Key, StringComparer.Ordinal))
        {
            if (!BuiltInTemplates.Contains(key))
            {
                templates.Add(Custom(key, text, isBuiltIn: false));
            }
        }

        return templates;
    }

    public async Task<NoteTemplate?> GetAsync(string key, CancellationToken cancellationToken)
    {
        if (!TemplateKey.IsValid(key))
        {
            return null;
        }

        var custom = await _store.LoadAllAsync(cancellationToken).ConfigureAwait(false);
        if (custom.TryGetValue(key, out var text))
        {
            return Custom(key, text, BuiltInTemplates.Contains(key));
        }

        return BuiltInTemplates.Contains(key) ? BuiltIn(key, CultureInfo.CurrentUICulture) : null;
    }

    public async Task<NoteTemplate> SaveAsync(string key, string text, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(text);
        if (!TemplateKey.IsValid(key))
        {
            throw new ArgumentException($"'{key}' is not a valid template key.", nameof(key));
        }

        if (text.Length > MaxTemplateLength)
        {
            throw new ArgumentException($"A template cannot be longer than {MaxTemplateLength / 1024} KB.", nameof(text));
        }

        // Stored with LF line endings and a final newline, like the notes the app writes.
        var normalized = text.ReplaceLineEndings("\n");
        if (!normalized.EndsWith('\n'))
        {
            normalized += "\n";
        }

        await _store.SaveAsync(key, normalized, cancellationToken).ConfigureAwait(false);
        return Custom(key, normalized, BuiltInTemplates.Contains(key));
    }

    public Task ResetAsync(string key, CancellationToken cancellationToken)
    {
        if (!TemplateKey.IsValid(key))
        {
            throw new ArgumentException($"'{key}' is not a valid template key.", nameof(key));
        }

        return _store.DeleteAsync(key, cancellationToken);
    }

    private static NoteTemplate BuiltIn(string key, CultureInfo culture) =>
        new(key, BuiltInTemplates.TypeOf(key), BuiltInTemplates.Get(key, culture), IsBuiltIn: true, IsCustomized: false);

    /// <summary>
    /// The type of a stored template is whatever its frontmatter declares once the placeholders are
    /// filled in (raw <c>{{id}}</c> is not valid YAML); a <c>{{type}}</c> placeholder or nothing means a plain note.
    /// </summary>
    private static NoteTemplate Custom(string key, string text, bool isBuiltIn)
    {
        var probe = new TemplateContext(NoteId.Parse("TEMPLATE"), "Title", Project: null, new DateOnly(2000, 1, 1), NoteType.Note);
        var type = NoteDocumentParser.Parse(NoteTemplates.Render(text, probe), key).Metadata.Type;
        return new NoteTemplate(key, type, text, isBuiltIn, IsCustomized: true);
    }
}
