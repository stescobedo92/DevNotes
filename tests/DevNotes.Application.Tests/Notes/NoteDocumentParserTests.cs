using DevNotes.Application.Notes;
using DevNotes.Domain.Notes;

namespace DevNotes.Application.Tests.Notes;

public sealed class NoteDocumentParserTests
{
    private const string SpecExample = """
        ---
        id: 01J8ZQ4M9T3N7K5W2X6Y8V0B1C
        title: Deadlock en actualización de inventario
        project: azure-microservices
        tags: [sql-server, deadlock, performance]
        type: bug
        created: 2026-09-12
        updated: 2026-09-14
        links:
          commits: [a3f9c21, 7be04d8]
          tickets: [MS-482]
          notes: ["[[benchmark-where-select-2026-08]]"]
        ---

        # Contexto

        El proceso de inventario se bloqueaba.
        """;

    [Fact]
    public void Parse_SpecExample_ReadsEveryField()
    {
        var document = NoteDocumentParser.Parse(SpecExample, "fallback");

        document.FrontmatterStatus.Should().Be(FrontmatterStatus.Valid);
        document.FrontmatterError.Should().BeNull();

        var metadata = document.Metadata;
        metadata.Id.Should().Be(NoteId.Parse("01J8ZQ4M9T3N7K5W2X6Y8V0B1C"));
        metadata.Title.Should().Be("Deadlock en actualización de inventario");
        metadata.Project.Should().Be("azure-microservices");
        metadata.Tags.Select(tag => tag.Value).Should().Equal("sql-server", "deadlock", "performance");
        metadata.Type.Should().Be(NoteType.Bug);
        metadata.Created.Should().Be(new DateOnly(2026, 9, 12));
        metadata.Updated.Should().Be(new DateOnly(2026, 9, 14));
        metadata.Links.Commits.Should().Equal("a3f9c21", "7be04d8");
        metadata.Links.Tickets.Should().Equal("MS-482");
        metadata.Links.Notes.Should().Equal("benchmark-where-select-2026-08");
    }

    [Fact]
    public void Parse_SplitsBodyFromFrontmatter()
    {
        var document = NoteDocumentParser.Parse(SpecExample, "fallback");

        document.Body.Should().StartWith("\n# Contexto").And.NotContain("01J8ZQ4M9T3N7K5W2X6Y8V0B1C");
        SpecExample[document.BodyOffset..].Should().Be(document.Body);
    }

    [Fact]
    public void Parse_NoFrontmatter_UsesFirstHeadingAsTitle()
    {
        var document = NoteDocumentParser.Parse("Intro line\n\n# Real title ##\n\nText", "file-name");

        document.FrontmatterStatus.Should().Be(FrontmatterStatus.None);
        document.Metadata.Title.Should().Be("Real title");
        document.Metadata.Id.Should().BeNull();
        document.Metadata.Type.Should().Be(NoteType.Note);
        document.Body.Should().StartWith("Intro line");
        document.BodyOffset.Should().Be(0);
    }

    [Fact]
    public void Parse_NoFrontmatterAndNoHeading_UsesFallbackTitle()
    {
        NoteDocumentParser.Parse("just text", "file-name").Metadata.Title.Should().Be("file-name");
        NoteDocumentParser.Parse(string.Empty, "file-name").Metadata.Title.Should().Be("file-name");
    }

    [Fact]
    public void Parse_HeadingInsideCodeFence_IsNotATitle()
    {
        const string text = "```bash\n# not a title\n```\n\n    # indented code\n\n# Actual";

        NoteDocumentParser.Parse(text, "fallback").Metadata.Title.Should().Be("Actual");
    }

    [Fact]
    public void Parse_TitleMissingInFrontmatter_FallsBackToHeadingThenFileName()
    {
        NoteDocumentParser.Parse("---\ntype: adr\n---\n# From heading\n", "file").Metadata.Title.Should().Be("From heading");
        NoteDocumentParser.Parse("---\ntype: adr\n---\nno heading\n", "file").Metadata.Title.Should().Be("file");
        NoteDocumentParser.Parse("---\ntitle: \"   \"\n---\nno heading\n", "file").Metadata.Title.Should().Be("file");
    }

    [Theory]
    [InlineData("tags: [One, two, ONE]", new[] { "one", "two" })]
    [InlineData("tags:\n  - alpha\n  - \"#beta\"", new[] { "alpha", "beta" })]
    [InlineData("tags:\n- alpha\n- beta", new[] { "alpha", "beta" })]
    [InlineData("tags: alpha, beta gamma", new[] { "alpha", "beta-gamma" })]
    [InlineData("tags: single", new[] { "single" })]
    [InlineData("tags:", new string[0])]
    [InlineData("tags: ~", new string[0])]
    [InlineData("tags: {nested: map}", new string[0])]
    public void Parse_TagsInAnyCommonShape_AreNormalized(string yaml, string[] expected)
    {
        var document = NoteDocumentParser.Parse($"---\n{yaml}\n---\n", "fallback");

        document.FrontmatterStatus.Should().Be(FrontmatterStatus.Valid);
        document.Metadata.Tags.Select(tag => tag.Value).Should().Equal(expected);
    }

    [Theory]
    [InlineData("created: 2026-09-12", 2026, 9, 12)]
    [InlineData("created: 2026-09-12T23:59:59Z", 2026, 9, 12)]
    [InlineData("created: \"2026-09-12 10:00\"", 2026, 9, 12)]
    public void Parse_DateWithOrWithoutTime_KeepsTheDay(string yaml, int year, int month, int day)
    {
        var document = NoteDocumentParser.Parse($"---\n{yaml}\n---\n", "fallback");

        document.Metadata.Created.Should().Be(new DateOnly(year, month, day));
    }

    [Theory]
    [InlineData("created: yesterday")]
    [InlineData("created: 12/09/2026")]
    [InlineData("created: 2026-13-45")]
    [InlineData("created:")]
    public void Parse_UnparseableDate_IsIgnored(string yaml)
    {
        var document = NoteDocumentParser.Parse($"---\n{yaml}\n---\n", "fallback");

        document.FrontmatterStatus.Should().Be(FrontmatterStatus.Valid);
        document.Metadata.Created.Should().BeNull();
    }

    [Theory]
    [InlineData("id: \"../../etc/passwd\"")]
    [InlineData("id: has space")]
    [InlineData("id:")]
    [InlineData("id: [a, b]")]
    public void Parse_UnsafeOrMalformedId_IsTreatedAsMissing(string yaml)
    {
        NoteDocumentParser.Parse($"---\n{yaml}\n---\n", "fallback").Metadata.Id.Should().BeNull();
    }

    [Fact]
    public void Parse_UnknownTypeAndUnknownKeys_AreTolerated()
    {
        var document = NoteDocumentParser.Parse("---\ntype: meeting\naliases: [x]\ncustom:\n  deep: 1\n---\nBody", "fallback");

        document.FrontmatterStatus.Should().Be(FrontmatterStatus.Valid);
        document.Metadata.Type.Should().Be(NoteType.Note);
    }

    [Fact]
    public void Parse_KeysAreCaseInsensitive()
    {
        var document = NoteDocumentParser.Parse("---\nTitle: Upper\nTAGS: [A]\nType: ADR\n---\n", "fallback");

        document.Metadata.Title.Should().Be("Upper");
        document.Metadata.Tags.Should().ContainSingle().Which.Value.Should().Be("a");
        document.Metadata.Type.Should().Be(NoteType.Adr);
    }

    [Theory]
    [InlineData("---\ntitle: [unclosed\n---\nBody")]
    [InlineData("---\ntitle: a\ntitle: b\n---\nBody")]
    [InlineData("---\n- just\n- a list\n---\nBody")]
    [InlineData("---\nkey: value\n  bad: indentation\n---\nBody")]
    public void Parse_InvalidYaml_KeepsWholeTextAsBodyAndReportsError(string text)
    {
        var document = NoteDocumentParser.Parse(text, "fallback");

        document.FrontmatterStatus.Should().Be(FrontmatterStatus.Invalid);
        document.FrontmatterError.Should().NotBeNullOrWhiteSpace();
        document.Body.Should().Be(text);
        document.BodyOffset.Should().Be(0);
        document.Metadata.Title.Should().Be("fallback");
    }

    [Fact]
    public void Parse_DeeplyNestedFlowCollections_AreRejectedWithoutRecursing()
    {
        var bomb = "---\nx: " + new string('[', 20_000) + "\n---\nBody";

        var document = NoteDocumentParser.Parse(bomb, "fallback");

        document.FrontmatterStatus.Should().Be(FrontmatterStatus.Invalid);
        document.FrontmatterError.Should().Contain("deeply");
    }

    [Fact]
    public void Parse_AliasExpansionBomb_DoesNotExplode()
    {
        const string bomb = """
            ---
            a: &a ["x","x","x","x","x","x","x","x","x"]
            b: &b [*a,*a,*a,*a,*a,*a,*a,*a,*a]
            c: &c [*b,*b,*b,*b,*b,*b,*b,*b,*b]
            d: &d [*c,*c,*c,*c,*c,*c,*c,*c,*c]
            tags: [*d,*d,*d,*d,*d,*d,*d,*d,*d]
            title: survived
            ---
            Body
            """;

        var document = NoteDocumentParser.Parse(bomb, "fallback");

        document.Metadata.Title.Should().Be("survived");
        document.Metadata.Tags.Should().BeEmpty("nested sequences are not tags");
    }

    [Fact]
    public void Parse_OversizedFrontmatter_IsTreatedAsContent()
    {
        var text = "---\nnotes: \"" + new string('x', FrontmatterBlock.MaxYamlLength) + "\"\n---\nBody";

        var document = NoteDocumentParser.Parse(text, "fallback");

        document.FrontmatterStatus.Should().Be(FrontmatterStatus.None);
        document.Body.Should().Be(text);
    }

    [Fact]
    public void Parse_EmptyFrontmatter_IsValidWithDefaults()
    {
        var document = NoteDocumentParser.Parse("---\n---\n# Title\n", "fallback");

        document.FrontmatterStatus.Should().Be(FrontmatterStatus.Valid);
        document.Metadata.Title.Should().Be("Title");
        document.Body.Should().Be("# Title\n");
    }

    [Fact]
    public void Parse_WindowsLineEndingsAndByteOrderMark_AreHandled()
    {
        var text = (char)0xFEFF + "---\r\ntitle: Con BOM\r\ntags: [a]\r\n---\r\nCuerpo\r\n";

        var document = NoteDocumentParser.Parse(text, "fallback");

        document.FrontmatterStatus.Should().Be(FrontmatterStatus.Valid);
        document.Metadata.Title.Should().Be("Con BOM");
        document.Body.Should().Be("Cuerpo\r\n");
    }

    [Fact]
    public void Parse_LinksAsScalarsAndWithDuplicates_AreNormalized()
    {
        const string text = """
            ---
            links:
              commits: a3f9c21, a3f9c21, 7be04d8
              tickets: MS-1
              notes:
                - "[[target|Alias]]"
                - plain
                - "[[target]]"
            ---
            """;

        var links = NoteDocumentParser.Parse(text, "fallback").Metadata.Links;

        links.Commits.Should().Equal("a3f9c21", "7be04d8");
        links.Tickets.Should().Equal("MS-1");
        links.Notes.Should().Equal("target", "plain");
    }

    [Fact]
    public void Parse_LinksThatAreNotAMapping_AreIgnored()
    {
        NoteDocumentParser.Parse("---\nlinks: nope\n---\n", "fallback").Metadata.Links.IsEmpty.Should().BeTrue();
    }

    [Fact]
    public void Parse_ThematicBreakDocument_IsNotMistakenForMetadata()
    {
        const string text = "---\nA paragraph between two horizontal rules\n---\nRest";

        var document = NoteDocumentParser.Parse(text, "fallback");

        // The block is not a YAML mapping, so nothing is dropped from the searchable body.
        document.FrontmatterStatus.Should().Be(FrontmatterStatus.Invalid);
        document.Body.Should().Be(text);
    }

    [Fact]
    public void Parse_NullArguments_Throw()
    {
        FluentActions.Invoking(() => NoteDocumentParser.Parse(null!, "x")).Should().Throw<ArgumentNullException>();
        FluentActions.Invoking(() => NoteDocumentParser.Parse("text", " ")).Should().Throw<ArgumentException>();
    }

    [Theory]
    [InlineData("---\ntitle: x\n---\nbody", true, 4, 9, 17)]
    [InlineData("---\n---\n", true, 4, 0, 8)]
    [InlineData("---\ntitle: x\n...\nbody", true, 4, 9, 17)]
    [InlineData("--- \ntitle: x\n---", true, 5, 9, 17)]
    [InlineData("---\ntitle: x\nno closing fence", false, 0, 0, 0)]
    [InlineData("text\n---\ntitle: x\n---\n", false, 0, 0, 0)]
    [InlineData("----\ntitle: x\n---\n", false, 0, 0, 0)]
    [InlineData("", false, 0, 0, 0)]
    public void FrontmatterBlock_TryFind_LocatesTheBlock(string text, bool found, int yamlStart, int yamlLength, int bodyStart)
    {
        FrontmatterBlock.TryFind(text, out var block).Should().Be(found);

        block.Should().Be(found ? new FrontmatterBlock(yamlStart, yamlLength, bodyStart) : default);
    }
}
