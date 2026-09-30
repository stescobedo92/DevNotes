using System.Diagnostics.CodeAnalysis;
using DevNotes.Domain.Common;

namespace DevNotes.Domain.Vaults;

/// <summary>
/// Identifier of a registered vault. It is used to name the index database file, so it is
/// restricted to safe tokens even though it normally is an app-generated ULID.
/// </summary>
public readonly record struct VaultId
{
    private readonly string? _value;

    private VaultId(string value) => _value = value;

    public string Value => _value ?? string.Empty;

    public bool IsEmpty => string.IsNullOrEmpty(_value);

    public static VaultId NewId(DateTimeOffset timestamp) => new(Ulid.NewUlid(timestamp));

    public static VaultId Parse(string text) =>
        TryParse(text, out var id)
            ? id
            : throw new FormatException($"'{text}' is not a valid vault id.");

    public static bool TryParse([NotNullWhen(true)] string? text, out VaultId id)
    {
        if (text is not null && SafeToken.IsValid(text))
        {
            id = new VaultId(text);
            return true;
        }

        id = default;
        return false;
    }

    public override string ToString() => Value;
}

/// <summary>A folder of Markdown notes registered in the app.</summary>
public sealed record Vault
{
    public Vault(VaultId id, string name, string rootPath)
    {
        if (id.IsEmpty)
        {
            throw new ArgumentException("Vault id is empty.", nameof(id));
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentException.ThrowIfNullOrWhiteSpace(rootPath);
        if (!Path.IsPathFullyQualified(rootPath))
        {
            throw new ArgumentException("A vault root must be an absolute path.", nameof(rootPath));
        }

        Id = id;
        Name = name.Trim();
        RootPath = Path.TrimEndingDirectorySeparator(Path.GetFullPath(rootPath));
    }

    public VaultId Id { get; }

    public string Name { get; }

    /// <summary>Absolute, normalized path without a trailing separator.</summary>
    public string RootPath { get; }
}
