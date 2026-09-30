using System.Diagnostics.CodeAnalysis;
using DevNotes.Domain.Notes;

namespace DevNotes.Application.Notes;

/// <summary>Identifier of a template: the file name under <c>.devnotes/templates</c>, without extension.</summary>
public static class TemplateKey
{
    public const int MaxLength = 40;

    /// <summary>"Bug resuelto" → "bug-resuelto". Fails for names with no letter or digit or that are too long.</summary>
    public static bool TryNormalize(string? name, [NotNullWhen(true)] out string? key)
    {
        key = null;
        if (name is null || !name.Any(char.IsLetterOrDigit))
        {
            return false;
        }

        var slug = Slug.From(name);
        if (slug.Length > MaxLength)
        {
            return false;
        }

        key = slug;
        return true;
    }

    public static bool IsValid(string? key) => TryNormalize(key, out var normalized) && normalized == key;
}

/// <summary>A note template: full note text with placeholders.</summary>
/// <param name="Key">Identifier; also the file name of a customized template.</param>
/// <param name="Type">Type of the notes it creates (declared by its <c>type:</c> key).</param>
/// <param name="Text">Template text with <c>{{placeholders}}</c>.</param>
/// <param name="IsBuiltIn">Whether the app ships a template with this key.</param>
/// <param name="IsCustomized">Whether the text comes from the vault rather than from the app.</param>
public sealed record NoteTemplate(string Key, NoteType Type, string Text, bool IsBuiltIn, bool IsCustomized);

/// <summary>Values substituted into a template.</summary>
public sealed record TemplateContext(NoteId Id, string Title, string? Project, DateOnly Date, NoteType Type, string? Body = null);
