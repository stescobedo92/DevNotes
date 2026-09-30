using System.Globalization;
using System.Text;

namespace DevNotes.Domain.Common;

/// <summary>
/// Comparison key of a piece of user text: case and diacritics are folded ("Déploiement" and
/// "deploiement" get the same key) so that sorting and filtering ignore both, on every platform.
/// </summary>
public static class TextKey
{
    public static string Of(string text)
    {
        ArgumentNullException.ThrowIfNull(text);

        var decomposed = text.Normalize(NormalizationForm.FormD);
        var builder = new StringBuilder(decomposed.Length);
        foreach (var c in decomposed)
        {
            if (CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.NonSpacingMark)
            {
                builder.Append(char.ToUpperInvariant(c));
            }
        }

        return builder.ToString();
    }
}
