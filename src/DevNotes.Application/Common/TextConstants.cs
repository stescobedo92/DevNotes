namespace DevNotes.Application.Common;

/// <summary>
/// Special characters referenced by code point so that no invisible character ever has to
/// appear literally in the source files.
/// </summary>
internal static class TextConstants
{
    public const char ByteOrderMark = (char)0xFEFF;
    public const char NoBreakSpace = (char)0x00A0;
    public const char LineSeparator = (char)0x2028;
    public const char ParagraphSeparator = (char)0x2029;
}
