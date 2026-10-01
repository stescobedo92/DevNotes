using DevNotes.Domain.Common;

namespace DevNotes.Domain.Tests.Common;

public sealed class TextKeyTests
{
    [Theory]
    [InlineData("Ábaco", "ABACO")]
    [InlineData("ñandú", "NANDU")]
    [InlineData("plain", "PLAIN")]
    [InlineData("Déploiement", "DEPLOIEMENT")]
    [InlineData("", "")]
    [InlineData("C# .NET", "C# .NET")]
    public void Of_FoldsCaseAndDiacritics(string text, string expected)
    {
        TextKey.Of(text).Should().Be(expected);
    }

    [Fact]
    public void Of_IsStableForEquivalentSpellings()
    {
        TextKey.Of("azure micro").Should().Be(TextKey.Of("AZURE MICRO"));
        TextKey.Of("é").Should().Be(TextKey.Of("é"), "precomposed and decomposed forms are the same text");
    }
}
