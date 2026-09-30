using System.Globalization;
using System.Text;
using DevNotes.Domain.Common;

namespace DevNotes.Domain.Notes;

/// <summary>Derives portable, readable file names from note titles.</summary>
public static class Slug
{
    public const int MaxLength = 80;
    public const string Fallback = "untitled";

    /// <summary>
    /// "Deadlock en actualización de inventario" → "deadlock-en-actualizacion-de-inventario".
    /// Diacritics are removed, letters and digits of any script are kept, everything else becomes '-'.
    /// </summary>
    public static string From(string? title)
    {
        if (string.IsNullOrWhiteSpace(title))
        {
            return Fallback;
        }

        var decomposed = title.Normalize(NormalizationForm.FormD);
        var builder = new StringBuilder(Math.Min(decomposed.Length, MaxLength));
        var pendingDash = false;

        foreach (var c in decomposed)
        {
            if (CharUnicodeInfo.GetUnicodeCategory(c) is UnicodeCategory.NonSpacingMark or UnicodeCategory.EnclosingMark)
            {
                continue;
            }

            if (char.IsLetterOrDigit(c))
            {
                if (pendingDash && builder.Length > 0)
                {
                    if (builder.Length + 1 >= MaxLength)
                    {
                        break;
                    }

                    builder.Append('-');
                }

                pendingDash = false;
                builder.Append(char.ToLowerInvariant(c));
                if (builder.Length >= MaxLength)
                {
                    break;
                }
            }
            else
            {
                pendingDash = true;
            }
        }

        if (builder.Length == 0)
        {
            return Fallback;
        }

        var slug = builder.ToString().Normalize(NormalizationForm.FormC);
        return FileNameRules.IsReservedDeviceName(slug) ? slug + "-note" : slug;
    }
}
