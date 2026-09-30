using DevNotes.Application.Search;
using DevNotes.Domain.Notes;

namespace DevNotes.Application.Tests.Search;

public sealed class SearchQueryParserTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("- \"\" #")]
    public void Parse_Nothing_IsEmpty(string? input)
    {
        var parsed = SearchQueryParser.Parse(input);

        parsed.IsEmpty.Should().BeTrue();
        parsed.Terms.Should().BeEmpty();
        parsed.Filter.IsEmpty.Should().BeTrue();
    }

    [Fact]
    public void Parse_Words_LastOneMatchesByPrefix()
    {
        var parsed = SearchQueryParser.Parse("deadlock inventory");

        parsed.Terms.Should().Equal(
            new SearchTerm("deadlock", IsPhrase: false, IsExcluded: false, IsPrefix: false),
            new SearchTerm("inventory", IsPhrase: false, IsExcluded: false, IsPrefix: true));
        parsed.Filter.IsEmpty.Should().BeTrue();
    }

    [Fact]
    public void Parse_ClosedPhrase_IsExactAndKeepsInnerSpaces()
    {
        var parsed = SearchQueryParser.Parse("\"exact   phrase\"");

        parsed.Terms.Should().ContainSingle().Which.Should().Be(new SearchTerm("exact phrase", IsPhrase: true, IsExcluded: false, IsPrefix: false));
    }

    [Fact]
    public void Parse_UnterminatedPhrase_IsStillBeingTyped()
    {
        var parsed = SearchQueryParser.Parse("fix \"dead inv");

        parsed.Terms.Should().Equal(
            new SearchTerm("fix", IsPhrase: false, IsExcluded: false, IsPrefix: false),
            new SearchTerm("dead inv", IsPhrase: true, IsExcluded: false, IsPrefix: true));
    }

    [Fact]
    public void Parse_ExcludedWordsAndPhrases_NeverMatchByPrefix()
    {
        var parsed = SearchQueryParser.Parse("-draft -\"work in progress\" x -last");

        parsed.Terms.Should().Equal(
            new SearchTerm("draft", IsPhrase: false, IsExcluded: true, IsPrefix: false),
            new SearchTerm("work in progress", IsPhrase: true, IsExcluded: true, IsPrefix: false),
            new SearchTerm("x", IsPhrase: false, IsExcluded: false, IsPrefix: false),
            new SearchTerm("last", IsPhrase: false, IsExcluded: true, IsPrefix: false));
        parsed.HasIncludedTerms.Should().BeTrue();
        parsed.HasExcludedTerms.Should().BeTrue();
    }

    [Theory]
    [InlineData("- alone", "alone")]
    [InlineData("sql-server", "sql-server")]
    [InlineData("--", null)]
    public void Parse_Dash_OnlyExcludesWhenItPrefixesAWord(string input, string? expectedTerm)
    {
        var parsed = SearchQueryParser.Parse(input);

        if (expectedTerm is null)
        {
            parsed.Terms.Should().BeEmpty();
        }
        else
        {
            parsed.Terms.Should().ContainSingle().Which.Should().Match<SearchTerm>(term => term.Text == expectedTerm && !term.IsExcluded);
        }
    }

    [Theory]
    [InlineData("project:cslinq", "cslinq")]
    [InlineData("PROJECT:cslinq", "cslinq")]
    [InlineData("proyecto:cslinq", "cslinq")]
    [InlineData("p:cslinq", "cslinq")]
    [InlineData("project:\"azure micro services\"", "azure micro services")]
    [InlineData("project:\"  padded  \"", "padded")]
    public void Parse_ProjectFilter_AcceptsAliasesAndQuotedValues(string input, string expected)
    {
        var parsed = SearchQueryParser.Parse(input);

        parsed.Terms.Should().BeEmpty();
        parsed.Filter.Projects.Should().Equal(expected);
    }

    [Fact]
    public void Parse_TagFilters_AreNormalizedAndAccumulate()
    {
        var parsed = SearchQueryParser.Parse("tag:SQL-Server #Performance etiqueta:deadlock,Perf tag:sql-server t:c#");

        parsed.Filter.Tags.Select(tag => tag.Value).Should().Equal("sql-server", "performance", "deadlock", "perf", "c#");
    }

    [Theory]
    [InlineData("#1")]
    [InlineData("#")]
    [InlineData("c#")]
    public void Parse_Hash_IsATagOnlyBeforeALetter(string input)
    {
        var parsed = SearchQueryParser.Parse(input);

        parsed.Filter.Tags.Should().BeEmpty();
    }

    [Fact]
    public void Parse_TypeFilters_AreAlternatives()
    {
        var parsed = SearchQueryParser.Parse("type:bug tipo:ADR type:bug");

        parsed.Filter.Types.Should().Equal(NoteType.Bug, NoteType.Adr);
        parsed.Filter.IsUnsatisfiable.Should().BeFalse();
    }

    [Fact]
    public void Parse_UnknownType_CannotMatchAnything()
    {
        var parsed = SearchQueryParser.Parse("type:poem");

        parsed.Filter.IsUnsatisfiable.Should().BeTrue();
    }

    [Fact]
    public void Parse_CommitAndTicketFilters()
    {
        var parsed = SearchQueryParser.Parse("commit:a3f9c21 ticket:MS-482 commit:A3F9C21 ticket:ms-482");

        parsed.Filter.Commits.Should().Equal("a3f9c21");
        parsed.Filter.Tickets.Should().Equal("MS-482");
    }

    [Theory]
    [InlineData("created:2026-09-01", "2026-09-01", true, "2026-09-01", true)]
    [InlineData("created:=2026-09-01", "2026-09-01", true, "2026-09-01", true)]
    [InlineData("created:>2026-09-01", "2026-09-01", false, null, false)]
    [InlineData("created:>=2026-09-01", "2026-09-01", true, null, false)]
    [InlineData("created:<2026-09-01", null, false, "2026-09-01", false)]
    [InlineData("created:<=2026-09-01", null, false, "2026-09-01", true)]
    [InlineData("created:2026-09", "2026-09-01", true, "2026-09-30", true)]
    [InlineData("created:>2026-09", "2026-09-30", false, null, false)]
    [InlineData("created:<2026-09", null, false, "2026-09-01", false)]
    [InlineData("created:2026", "2026-01-01", true, "2026-12-31", true)]
    [InlineData("created:2026-09-01..2026-09-30", "2026-09-01", true, "2026-09-30", true)]
    [InlineData("created:2026-08..2026-09", "2026-08-01", true, "2026-09-30", true)]
    [InlineData("creada:2026-02", "2026-02-01", true, "2026-02-28", true)]
    public void Parse_CreatedFilter_Syntax(string input, string? from, bool fromInclusive, string? to, bool toInclusive)
    {
        var parsed = SearchQueryParser.Parse(input);

        var range = parsed.Filter.Created;
        range.From.Should().Be(from is null ? null : new DateBound(DateOnly.Parse(from, System.Globalization.CultureInfo.InvariantCulture), fromInclusive));
        range.To.Should().Be(to is null ? null : new DateBound(DateOnly.Parse(to, System.Globalization.CultureInfo.InvariantCulture), toInclusive));
        parsed.Filter.Updated.IsEmpty.Should().BeTrue();
    }

    [Fact]
    public void Parse_UpdatedFilters_Combine()
    {
        var parsed = SearchQueryParser.Parse("updated:>=2026-09-01 actualizada:<2026-10-01");

        parsed.Filter.Updated.Should().Be(new DateRange(
            new DateBound(new DateOnly(2026, 9, 1), Inclusive: true),
            new DateBound(new DateOnly(2026, 10, 1), Inclusive: false)));
    }

    [Fact]
    public void Parse_ContradictoryDates_CannotMatchAnything()
    {
        var parsed = SearchQueryParser.Parse("created:>2026-09-30 created:<2026-09-01");

        parsed.Filter.IsUnsatisfiable.Should().BeTrue();
    }

    [Theory]
    [InlineData("created:2026-13-01")]
    [InlineData("created:yesterday")]
    [InlineData("created:>2026-09-3")]
    [InlineData("created:2026-09-01..")]
    [InlineData("created:")]
    [InlineData("created:>")]
    public void Parse_MalformedDate_IsIgnored(string input)
    {
        var parsed = SearchQueryParser.Parse(input);

        parsed.Filter.IsEmpty.Should().BeTrue();
        parsed.Terms.Should().BeEmpty();
    }

    [Fact]
    public void Parse_EmptyFilterValue_IsIgnoredWhileTyping()
    {
        var parsed = SearchQueryParser.Parse("deadlock project:");

        parsed.Terms.Should().ContainSingle().Which.Text.Should().Be("deadlock");
        parsed.Filter.IsEmpty.Should().BeTrue();
    }

    [Fact]
    public void Parse_NegatedFilter_IsIgnored()
    {
        var parsed = SearchQueryParser.Parse("-project:x -#tag deadlock");

        parsed.Filter.IsEmpty.Should().BeTrue();
        parsed.Terms.Select(term => term.Text).Should().Equal("#tag", "deadlock");
        parsed.Terms[0].IsExcluded.Should().BeTrue();
    }

    [Theory]
    [InlineData("title:secret", "title:secret")]
    [InlineData("http://x.example/a", "http://x.example/a")]
    [InlineData(":value", ":value")]
    [InlineData("unknownkeythatislong:v", "unknownkeythatislong:v")]
    public void Parse_UnknownKey_IsPlainText(string input, string expectedTerm)
    {
        var parsed = SearchQueryParser.Parse(input);

        parsed.Filter.IsEmpty.Should().BeTrue();
        parsed.Terms.Should().ContainSingle().Which.Text.Should().Be(expectedTerm);
    }

    [Fact]
    public void Parse_MixedQuery()
    {
        var parsed = SearchQueryParser.Parse("deadlock project:cslinq tag:design-decision -draft \"index scan\" type:adr created:>2026-09-01");

        parsed.Terms.Select(term => term.Text).Should().Equal("deadlock", "draft", "index scan");
        parsed.Terms.Single(term => term.Text == "index scan").IsPrefix.Should().BeFalse("the phrase is closed");
        parsed.Filter.Projects.Should().Equal("cslinq");
        parsed.Filter.Tags.Select(tag => tag.Value).Should().Equal("design-decision");
        parsed.Filter.Types.Should().Equal(NoteType.Adr);
        parsed.Filter.Created.From.Should().Be(new DateBound(new DateOnly(2026, 9, 1), Inclusive: false));
    }

    [Fact]
    public void Parse_TooManyTerms_KeepsTheFirstOnesButAllFilters()
    {
        var input = string.Join(' ', Enumerable.Range(1, 40).Select(i => $"w{i}")) + " project:late";

        var parsed = SearchQueryParser.Parse(input);

        parsed.Terms.Should().HaveCount(SearchQueryParser.MaxTerms);
        parsed.Filter.Projects.Should().Equal("late");
    }

    [Fact]
    public void Parse_VeryLongInput_IsTruncated()
    {
        var parsed = SearchQueryParser.Parse(new string('a', 2_000) + " project:cut");

        parsed.Filter.IsEmpty.Should().BeTrue("the filter lies beyond the accepted length");
        parsed.Terms.Should().ContainSingle().Which.Text.Should().HaveLength(FtsQueryBuilder.MaxTermLength);
    }
}
