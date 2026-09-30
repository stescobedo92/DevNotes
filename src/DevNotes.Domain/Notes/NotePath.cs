using System.Diagnostics.CodeAnalysis;
using DevNotes.Domain.Common;

namespace DevNotes.Domain.Notes;

/// <summary>
/// Path of a note relative to the vault root, always with forward slashes.
/// An instance can only be created from a path that stays inside the vault: rooted paths,
/// <c>..</c> segments and non-portable names are rejected, which makes path traversal
/// unrepresentable for every layer that works with <see cref="NotePath"/>.
/// </summary>
public readonly record struct NotePath : IComparable<NotePath>
{
    public const string Extension = ".md";
    public const int MaxLength = 240;

    private readonly string? _value;

    private NotePath(string value) => _value = value;

    public string Value => _value ?? string.Empty;

    public bool IsEmpty => string.IsNullOrEmpty(_value);

    /// <summary>File name including the extension.</summary>
    public string FileName => Value[(Value.LastIndexOf('/') + 1)..];

    public string FileNameWithoutExtension => FileName[..^Extension.Length];

    /// <summary>Containing folder relative to the vault ("" for the vault root).</summary>
    public string Directory
    {
        get
        {
            var index = Value.LastIndexOf('/');
            return index < 0 ? string.Empty : Value[..index];
        }
    }

    /// <summary>True when any segment starts with a dot (hidden files and tool folders such as .git).</summary>
    public bool IsHidden => Value.StartsWith('.') || Value.Contains("/.", StringComparison.Ordinal);

    public static NotePath Create(string relativePath) =>
        TryCreate(relativePath, out var path, out var error)
            ? path
            : throw new ArgumentException($"Invalid note path '{relativePath}': {error}.", nameof(relativePath));

    public static bool TryCreate(string? relativePath, out NotePath path) =>
        TryCreate(relativePath, out path, out _);

    public static bool TryCreate(string? relativePath, out NotePath path, [NotNullWhen(false)] out string? error)
    {
        path = default;
        if (string.IsNullOrWhiteSpace(relativePath))
        {
            error = "path is empty";
            return false;
        }

        var normalized = relativePath.Replace('\\', '/');
        if (normalized.Length > MaxLength)
        {
            error = $"path is longer than {MaxLength} characters";
            return false;
        }

        if (normalized[0] == '/')
        {
            error = "path must be relative to the vault";
            return false;
        }

        if (!normalized.EndsWith(Extension, StringComparison.OrdinalIgnoreCase))
        {
            error = $"notes must use the '{Extension}' extension";
            return false;
        }

        foreach (var range in normalized.AsSpan().Split('/'))
        {
            error = FileNameRules.ValidateSegment(normalized.AsSpan(range));
            if (error is not null)
            {
                return false;
            }
        }

        if (normalized.AsSpan(normalized.LastIndexOf('/') + 1).Length == Extension.Length)
        {
            error = "file name is empty";
            return false;
        }

        path = new NotePath(normalized);
        error = null;
        return true;
    }

    /// <summary>Builds a path from a folder ("" for the root) and a file name without extension.</summary>
    public static bool TryCombine(string? directory, string fileNameWithoutExtension, out NotePath path, [NotNullWhen(false)] out string? error)
    {
        if (string.IsNullOrEmpty(fileNameWithoutExtension) || fileNameWithoutExtension.AsSpan().ContainsAny('/', '\\'))
        {
            path = default;
            error = "a file name cannot be empty or contain path separators";
            return false;
        }

        var folder = NormalizeDirectory(directory);
        var relative = folder.Length == 0
            ? fileNameWithoutExtension + Extension
            : folder + "/" + fileNameWithoutExtension + Extension;
        return TryCreate(relative, out path, out error);
    }

    /// <summary>Normalizes a folder typed by the user: slashes unified, surrounding separators and spaces removed.</summary>
    public static string NormalizeDirectory(string? directory) =>
        string.IsNullOrWhiteSpace(directory)
            ? string.Empty
            : directory.Replace('\\', '/').Trim().Trim('/');

    public NotePath WithFileName(string fileNameWithoutExtension) =>
        TryCombine(Directory, fileNameWithoutExtension, out var path, out var error)
            ? path
            : throw new ArgumentException($"Invalid file name '{fileNameWithoutExtension}': {error}.", nameof(fileNameWithoutExtension));

    public int CompareTo(NotePath other) => string.CompareOrdinal(Value, other.Value);

    public override string ToString() => Value;

    public static bool operator <(NotePath left, NotePath right) => left.CompareTo(right) < 0;

    public static bool operator <=(NotePath left, NotePath right) => left.CompareTo(right) <= 0;

    public static bool operator >(NotePath left, NotePath right) => left.CompareTo(right) > 0;

    public static bool operator >=(NotePath left, NotePath right) => left.CompareTo(right) >= 0;
}
