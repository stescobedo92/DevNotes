using System.Text;
using DevNotes.Application.Abstractions;

namespace DevNotes.Infrastructure.Storage;

/// <summary>
/// Converts note files to text and back without ever losing a byte the user did not edit.
/// <para>
/// Decoding is strict: bytes that are not valid Unicode are never replaced by U+FFFD (which would
/// be written back on the next save and destroy the original). A file that is not valid UTF-8 or
/// UTF-16 is read as Windows-1252, where each byte maps to one character and back, and is written
/// in that same encoding, so it round-trips exactly whatever its real code page is.
/// </para>
/// </summary>
internal static class NoteTextCodec
{
    private static readonly UTF8Encoding _utf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);
    private static readonly UnicodeEncoding _utf16LittleEndian = new(bigEndian: false, byteOrderMark: true, throwOnInvalidBytes: true);
    private static readonly UnicodeEncoding _utf16BigEndian = new(bigEndian: true, byteOrderMark: true, throwOnInvalidBytes: true);

    // Lenient encoders: text that comes from the editor may contain a lone surrogate, which has no
    // valid encoding at all; it is written as U+FFFD instead of failing the save.
    private static readonly UTF8Encoding _utf8Writer = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: false);

    // Resolved from the provider instance, so no process-wide encoding registration is needed.
    private static readonly Encoding _legacy = CodePagesEncodingProvider.Instance.GetEncoding(
        1252,
        EncoderFallback.ExceptionFallback,
        DecoderFallback.ExceptionFallback)!;

    private static ReadOnlySpan<byte> Utf8Bom => [0xEF, 0xBB, 0xBF];

    private static ReadOnlySpan<byte> Utf16LittleEndianBom => [0xFF, 0xFE];

    private static ReadOnlySpan<byte> Utf16BigEndianBom => [0xFE, 0xFF];

    public static string Decode(ReadOnlySpan<byte> bytes, out NoteTextEncoding encoding)
    {
        try
        {
            if (bytes.StartsWith(Utf16LittleEndianBom))
            {
                encoding = NoteTextEncoding.Utf16LittleEndian;
                return _utf16LittleEndian.GetString(bytes[Utf16LittleEndianBom.Length..]);
            }

            if (bytes.StartsWith(Utf16BigEndianBom))
            {
                encoding = NoteTextEncoding.Utf16BigEndian;
                return _utf16BigEndian.GetString(bytes[Utf16BigEndianBom.Length..]);
            }

            if (bytes.StartsWith(Utf8Bom))
            {
                encoding = NoteTextEncoding.Utf8WithBom;
                return _utf8.GetString(bytes[Utf8Bom.Length..]);
            }

            encoding = NoteTextEncoding.Utf8;
            return _utf8.GetString(bytes);
        }
        catch (DecoderFallbackException)
        {
            // Not Unicode after all (including a byte-order mark followed by invalid data).
            encoding = NoteTextEncoding.Legacy;
            return _legacy.GetString(bytes);
        }
    }

    /// <param name="text">Text to write.</param>
    /// <param name="requested">Encoding the note had when it was read.</param>
    /// <param name="actual">Encoding that was used; UTF-8 when the text does not fit the legacy code page.</param>
    public static byte[] Encode(string text, NoteTextEncoding requested, out NoteTextEncoding actual)
    {
        actual = requested;
        switch (requested)
        {
            case NoteTextEncoding.Utf8WithBom:
                return WithPreamble(Utf8Bom, _utf8Writer, text);
            case NoteTextEncoding.Utf16LittleEndian:
                return WithPreamble(Utf16LittleEndianBom, Encoding.Unicode, text);
            case NoteTextEncoding.Utf16BigEndian:
                return WithPreamble(Utf16BigEndianBom, Encoding.BigEndianUnicode, text);
            case NoteTextEncoding.Legacy:
                try
                {
                    return _legacy.GetBytes(text);
                }
                catch (EncoderFallbackException)
                {
                    // The user typed something the code page cannot hold: the whole note becomes UTF-8.
                    break;
                }

            default:
                break;
        }

        actual = NoteTextEncoding.Utf8;
        return _utf8Writer.GetBytes(text);
    }

    private static byte[] WithPreamble(ReadOnlySpan<byte> preamble, Encoding encoding, string text)
    {
        var bytes = new byte[preamble.Length + encoding.GetByteCount(text)];
        preamble.CopyTo(bytes);
        encoding.GetBytes(text, bytes.AsSpan(preamble.Length));
        return bytes;
    }
}
