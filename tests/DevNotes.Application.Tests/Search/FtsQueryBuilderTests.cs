using DevNotes.Application.Search;

namespace DevNotes.Application.Tests.Search;

public sealed class FtsQueryBuilderTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   \t\r\n ")]
    [InlineData("\"\"")]
    [InlineData("* - + ( ) ^ : ~")]
    public void Build_NothingSearchable_ReturnsEmpty(string? input)
    {
        var query = FtsQueryBuilder.Build(input);

        query.IsEmpty.Should().BeTrue();
        query.Should().Be(FtsQuery.Empty);
    }

    [Theory]
    [InlineData("deadlock", "\"deadlock\"*", "\"deadlock\"")]
    [InlineData("  deadlock   inventory ", "\"deadlock\" \"inventory\"*", "\"deadlock\" \"inventory\"")]
    [InlineData("sql-server", "\"sql-server\"*", "\"sql-server\"")]
    [InlineData("C# go", "\"C#\" \"go\"*", null)]
    [InlineData("id", "\"id\"*", null)]
    [InlineData("UpdateInventoryAsync db", "\"UpdateInventoryAsync\" \"db\"*", "\"UpdateInventoryAsync\"")]
    [InlineData("actualización ñandú", "\"actualización\" \"ñandú\"*", "\"actualización\" \"ñandú\"")]
    public void Build_PlainTerms_AreQuotedAndLastTermIsPrefix(string input, string expectedMatch, string? expectedTrigram)
    {
        var query = FtsQueryBuilder.Build(input);

        query.Match.Should().Be(expectedMatch);
        query.TrigramMatch.Should().Be(expectedTrigram);
    }

    [Theory]
    [InlineData("foo OR bar", "\"foo\" \"OR\" \"bar\"*")]
    [InlineData("foo AND NOT bar", "\"foo\" \"AND\" \"NOT\" \"bar\"*")]
    [InlineData("NEAR(a b, 5)", "\"NEAR(a\" \"b,\" \"5)\"*")]
    [InlineData("title:secret", "\"title:secret\"*")]
    [InlineData("+required", "\"+required\"*")]
    [InlineData("pre* ^start", "\"pre*\" \"^start\"*")]
    [InlineData("a\"b\"\"c", "\"abc\"*")]
    [InlineData("x\" OR \"y", "\"x\" \"OR\" \"y\"*")]
    [InlineData("{col1 col2}: x", "\"{col1\" \"col2}:\" \"x\"*")]
    [InlineData("'; DROP TABLE notes; --", "\"DROP\" \"TABLE\" \"notes;\"*")]
    public void Build_Fts5Syntax_IsNeutralizedAsLiteralText(string input, string expectedMatch)
    {
        var query = FtsQueryBuilder.Build(input);

        query.Match.Should().Be(expectedMatch);
        AssertOnlyQuotedStrings(query.Match!);
        if (query.TrigramMatch is not null)
        {
            AssertOnlyQuotedStrings(query.TrigramMatch);
        }
    }

    [Fact]
    public void Build_Phrases_ArePassedAsOneQuotedString()
    {
        FtsQueryBuilder.Build("\"quoted phrase\"").Should().Be(new FtsQuery("\"quoted phrase\"", "\"quoted phrase\""));
        FtsQueryBuilder.Build("\"still typ").Should().Be(new FtsQuery("\"still typ\"*", "\"still typ\""));
        FtsQueryBuilder.Build("\"ab\" x").Should().Be(new FtsQuery("\"ab\" \"x\"*", null));
    }

    [Fact]
    public void Build_ExcludedTerms_GoToASeparateExpressionAsAlternatives()
    {
        var query = FtsQueryBuilder.Build("fix -draft -\"work in progress\"");

        query.Match.Should().Be("\"fix\"", "only the term at the end of the input is still being typed");
        query.TrigramMatch.Should().Be("\"fix\"");
        query.Exclude.Should().Be("\"draft\" OR \"work in progress\"");
        AssertOnlyQuotedStrings(query.Match);
    }

    [Fact]
    public void Build_OnlyExclusions_HasNoPositiveMatch()
    {
        var query = FtsQueryBuilder.Build("-draft");

        query.IsEmpty.Should().BeTrue();
        query.Exclude.Should().Be("\"draft\"");
    }

    [Fact]
    public void Build_FiltersOnly_IsEmpty()
    {
        FtsQueryBuilder.Build("project:x tag:y").Should().Be(FtsQuery.Empty);
    }

    [Fact]
    public void Build_ControlCharacters_AreRemoved()
    {
        var query = FtsQueryBuilder.Build("dead\0lock\u0007 x\u001by");

        query.Match.Should().Be("\"deadlock\" \"xy\"*");
    }

    [Fact]
    public void Build_TooManyTerms_KeepsTheFirstOnes()
    {
        var input = string.Join(' ', Enumerable.Range(1, 40).Select(i => $"term{i}"));

        var query = FtsQueryBuilder.Build(input);

        query.Match!.Split(' ').Should().HaveCount(FtsQueryBuilder.MaxTerms);
        query.Match.Should().EndWith($"\"term{FtsQueryBuilder.MaxTerms}\"*");
    }

    [Fact]
    public void Build_VeryLongInput_IsTruncated()
    {
        var query = FtsQueryBuilder.Build(new string('a', 10_000));

        query.Match.Should().Be('"' + new string('a', FtsQueryBuilder.MaxTermLength) + "\"*");
    }

    [Fact]
    public void Build_SurrogatePairs_AreKeptWholeOrDropped()
    {
        var astral = char.ConvertFromUtf32(0x1D538); // Mathematical double-struck A (a letter outside the BMP)
        var loneHigh = astral[0].ToString();

        FtsQueryBuilder.Build($"x{astral}y").Match.Should().Be($"\"x{astral}y\"*");
        FtsQueryBuilder.Build($"x{loneHigh}y").Match.Should().Be("\"xy\"*");
        FtsQueryBuilder.Build(astral).IsEmpty.Should().BeFalse();
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void SnippetParser_Parse_Empty_ReturnsNoSegments(string? snippet)
    {
        SnippetParser.Parse(snippet).Should().BeEmpty();
    }

    [Fact]
    public void SnippetParser_Parse_SplitsMatchesFromContext()
    {
        var snippet = $"…fix the {SnippetParser.MatchStart}deadlock{SnippetParser.MatchEnd} in {SnippetParser.MatchStart}inventory{SnippetParser.MatchEnd}";

        SnippetParser.Parse(snippet).Should().Equal(
            new SnippetSegment("…fix the ", false),
            new SnippetSegment("deadlock", true),
            new SnippetSegment(" in ", false),
            new SnippetSegment("inventory", true));
    }

    [Fact]
    public void SnippetParser_Parse_FlattensLineBreaksAndToleratesUnbalancedMarkers()
    {
        var snippet = $"line one\nline two {SnippetParser.MatchStart}open";

        SnippetParser.Parse(snippet).Should().Equal(
            new SnippetSegment("line one line two ", false),
            new SnippetSegment("open", true));
    }

    [Fact]
    public void SnippetParser_ToSingleLine_TruncatesAndCleans()
    {
        SnippetParser.ToSingleLine("\n# Title\r\n\r\nb\t c  " + SnippetParser.MatchStart, 100).Should().Be("# Title b c");
        SnippetParser.ToSingleLine("abcdef", 3).Should().Be("abc");
        SnippetParser.ToSingleLine("ab cdef", 3).Should().Be("ab", "a result never ends with a separator");
        SnippetParser.ToSingleLine(null, 3).Should().BeEmpty();
        SnippetParser.ToSingleLine("abc", 0).Should().BeEmpty();
    }

    /// <summary>A sanitized expression must be a sequence of "quoted strings", each optionally followed by '*'.</summary>
    private static void AssertOnlyQuotedStrings(string match)
    {
        var position = 0;
        while (position < match.Length)
        {
            match[position].Should().Be('"', $"every term must be quoted in: {match}");
            var close = match.IndexOf('"', position + 1);
            close.Should().BeGreaterThan(position, $"every quote must be closed in: {match}");
            position = close + 1;

            if (position < match.Length && match[position] == '*')
            {
                position++;
            }

            if (position < match.Length)
            {
                match[position].Should().Be(' ', $"terms must be separated by a single space in: {match}");
                position++;
            }
        }
    }
}
