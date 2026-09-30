using System.Buffers;
using System.Globalization;
using System.Text;
using DevNotes.Application.Common;

namespace DevNotes.Application.Notes;

/// <summary>Formats strings as single-line YAML scalars that read back as the same string.</summary>
public static class YamlScalar
{
    private static readonly SearchValues<char> _numberLikeCharacters =
        SearchValues.Create("0123456789_-.:+ xXoOabcdefABCDEFtTzZ");

    /// <summary>
    /// Returns <paramref name="value"/> unquoted when that is unambiguous, otherwise as a
    /// double-quoted scalar with escapes. Values that YAML would read as another type
    /// (numbers, booleans, null, dates) are always quoted.
    /// </summary>
    public static string Format(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        return CanBePlain(value) ? value : Quote(value);
    }

    public static string FormatDate(DateOnly date) => date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    private static bool CanBePlain(string value)
    {
        if (value.Length == 0 || !char.IsLetterOrDigit(value[0]) || value[^1] == ' ')
        {
            return false;
        }

        foreach (var c in value)
        {
            // Conservative allow-list: no YAML indicator can appear at all.
            if (!char.IsLetterOrDigit(c) && c is not (' ' or '_' or '-' or '.' or '/' or '(' or ')' or '+'))
            {
                return false;
            }
        }

        return !LooksLikeNonString(value);
    }

    private static bool LooksLikeNonString(string value)
    {
        switch (value.ToLowerInvariant())
        {
            case "true" or "false" or "yes" or "no" or "on" or "off" or "y" or "n" or "null":
                return true;
            default:
                break;
        }

        if (double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out _))
        {
            return true;
        }

        // Octal / hexadecimal integers, sexagesimal numbers and timestamps start with a digit and
        // contain only digits and a few separators.
        return char.IsAsciiDigit(value[0]) && !value.AsSpan().ContainsAnyExcept(_numberLikeCharacters);
    }

    private static string Quote(string value)
    {
        var builder = new StringBuilder(value.Length + 2).Append('"');
        foreach (var c in value)
        {
            switch (c)
            {
                case '"':
                    builder.Append("\\\"");
                    break;
                case '\\':
                    builder.Append("\\\\");
                    break;
                case '\n':
                    builder.Append("\\n");
                    break;
                case '\r':
                    builder.Append("\\r");
                    break;
                case '\t':
                    builder.Append("\\t");
                    break;
                default:
                    if (char.IsControl(c)
                        || c is TextConstants.LineSeparator or TextConstants.ParagraphSeparator or TextConstants.ByteOrderMark)
                    {
                        builder.Append("\\u").Append(((int)c).ToString("X4", CultureInfo.InvariantCulture));
                    }
                    else
                    {
                        builder.Append(c);
                    }

                    break;
            }
        }

        return builder.Append('"').ToString();
    }
}
