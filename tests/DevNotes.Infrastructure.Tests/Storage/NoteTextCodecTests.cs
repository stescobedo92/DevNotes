using System.Text;
using DevNotes.Application.Abstractions;
using DevNotes.Infrastructure.Storage;

namespace DevNotes.Infrastructure.Tests.Storage;

public sealed class NoteTextCodecTests
{
    [Theory]
    [InlineData(new byte[] { 0x61, 0x62 }, "ab", NoteTextEncoding.Utf8)]
    [InlineData(new byte[] { 0xC3, 0xB1 }, "ñ", NoteTextEncoding.Utf8)]
    [InlineData(new byte[] { 0xEF, 0xBB, 0xBF, 0x61 }, "a", NoteTextEncoding.Utf8WithBom)]
    [InlineData(new byte[] { 0xFF, 0xFE, 0x61, 0x00 }, "a", NoteTextEncoding.Utf16LittleEndian)]
    [InlineData(new byte[] { 0xFE, 0xFF, 0x00, 0x61 }, "a", NoteTextEncoding.Utf16BigEndian)]
    [InlineData(new byte[0], "", NoteTextEncoding.Utf8)]
    public void Decode_Unicode_IsDetectedFromTheBytes(byte[] bytes, string expectedText, NoteTextEncoding expectedEncoding)
    {
        NoteTextCodec.Decode(bytes, out var encoding).Should().Be(expectedText);
        encoding.Should().Be(expectedEncoding);
    }

    [Theory]
    [InlineData(new byte[] { 0x63, 0x61, 0x6E, 0x63, 0x69, 0xF3, 0x6E }, "canción")] // Windows-1252 text
    [InlineData(new byte[] { 0x80, 0x93, 0x94 }, "€“”")] // the range where Windows-1252 differs from Latin-1
    [InlineData(new byte[] { 0xEF, 0xBB, 0xBF, 0xF3 }, "ï»¿ó")] // UTF-8 byte-order mark followed by invalid UTF-8
    [InlineData(new byte[] { 0xFF, 0xFE, 0x61 }, "ÿþa")] // UTF-16 byte-order mark followed by half a code unit
    [InlineData(new byte[] { 0x23, 0x20, 0xFF, 0xFE, 0x41 }, "# ÿþA")]
    public void Decode_NotUnicode_FallsBackToTheLegacyCodePage_WithoutReplacementCharacters(byte[] bytes, string expectedText)
    {
        var text = NoteTextCodec.Decode(bytes, out var encoding);

        encoding.Should().Be(NoteTextEncoding.Legacy);
        text.Should().Be(expectedText);
        text.Should().NotContain("�", "a replacement character would be written back and destroy the original byte");
        NoteTextCodec.Encode(text, encoding, out _).Should().Equal(bytes);
    }

    [Fact]
    public void Legacy_EveryByteValue_RoundTripsExactly()
    {
        // Whatever code page the file really uses, the bytes that are not edited must survive a save.
        var everyByte = Enumerable.Range(0, 256).Select(value => (byte)value).ToArray();
        var notUnicode = everyByte.Reverse().ToArray(); // Starts with 0xFF 0xFE 0xFD: an odd-length "UTF-16" file.

        var text = NoteTextCodec.Decode(notUnicode, out var encoding);

        encoding.Should().Be(NoteTextEncoding.Legacy);
        text.Should().HaveLength(256);
        text.Distinct().Should().HaveCount(256, "each byte maps to its own character");
        NoteTextCodec.Encode(text, NoteTextEncoding.Legacy, out var actual).Should().Equal(notUnicode);
        actual.Should().Be(NoteTextEncoding.Legacy);
    }

    [Theory]
    [InlineData(NoteTextEncoding.Utf8)]
    [InlineData(NoteTextEncoding.Utf8WithBom)]
    [InlineData(NoteTextEncoding.Utf16LittleEndian)]
    [InlineData(NoteTextEncoding.Utf16BigEndian)]
    public void Encode_Unicode_RoundTripsAnyText(NoteTextEncoding encoding)
    {
        const string text = "# Título ✓ 日本語 😀\r\nlínea\n";

        var bytes = NoteTextCodec.Encode(text, encoding, out var actual);

        actual.Should().Be(encoding);
        NoteTextCodec.Decode(bytes, out var detected).Should().Be(text);
        detected.Should().Be(encoding);
    }

    [Fact]
    public void Encode_LegacyTextWithACharacterOutsideTheCodePage_BecomesUtf8()
    {
        const string text = "canción ✓";

        var bytes = NoteTextCodec.Encode(text, NoteTextEncoding.Legacy, out var actual);

        actual.Should().Be(NoteTextEncoding.Utf8, "nothing the user typed may be dropped to keep the old encoding");
        bytes.Should().Equal(Encoding.UTF8.GetBytes(text));
    }
}
