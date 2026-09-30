namespace DevNotes.Domain.Common;

/// <summary>
/// Validation for identifiers that end up in file names, SQL keys and YAML scalars.
/// Only ASCII letters, digits, '.', '_' and '-' are allowed, so a token can never carry
/// path separators, traversal sequences or characters with special meaning in YAML.
/// </summary>
internal static class SafeToken
{
    public const int MaxLength = 64;

    public static bool IsValid(ReadOnlySpan<char> value)
    {
        if (value.IsEmpty || value.Length > MaxLength || value[0] == '.' || value[^1] == '.')
        {
            return false;
        }

        foreach (var c in value)
        {
            if (!char.IsAsciiLetterOrDigit(c) && c is not ('.' or '_' or '-'))
            {
                return false;
            }
        }

        return true;
    }
}
