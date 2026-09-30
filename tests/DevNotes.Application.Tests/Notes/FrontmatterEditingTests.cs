using DevNotes.Application.Notes;
using DevNotes.Application.Tests.Fakes;
using DevNotes.Domain.Notes;

namespace DevNotes.Application.Tests.Notes;

public sealed class FrontmatterEditingTests
{
    private static readonly DateOnly _today = new(2026, 9, 30);

    [Theory]
    [InlineData("Simple title", "Simple title")]
    [InlineData("azure-microservices", "azure-microservices")]
    [InlineData("Título con ñ y acentos", "Título con ñ y acentos")]
    [InlineData("v1.2 (beta)", "v1.2 (beta)")]
    [InlineData("key: value", "\"key: value\"")]
    [InlineData("hash # comment", "\"hash # comment\"")]
    [InlineData("say \"hi\"", "\"say \\\"hi\\\"\"")]
    [InlineData("back\\slash", "\"back\\\\slash\"")]
    [InlineData("- leading dash", "\"- leading dash\"")]
    [InlineData("[flow]", "\"[flow]\"")]
    [InlineData("trailing ", "\"trailing \"")]
    [InlineData("", "\"\"")]
    [InlineData("true", "\"true\"")]
    [InlineData("No", "\"No\"")]
    [InlineData("null", "\"null\"")]
    [InlineData("123", "\"123\"")]
    [InlineData("1e5", "\"1e5\"")]
    [InlineData("0x1F", "\"0x1F\"")]
    [InlineData("2026-09-12", "\"2026-09-12\"")]
    [InlineData("line\nbreak", "\"line\\nbreak\"")]
    [InlineData("tab\there", "\"tab\\there\"")]
    public void YamlScalar_Format_QuotesOnlyWhenNeeded(string value, string expected)
    {
        YamlScalar.Format(value).Should().Be(expected);
    }

    [Theory]
    [InlineData("Simple title")]
    [InlineData("key: value # not a comment")]
    [InlineData("say \"hi\" and 'bye'")]
    [InlineData("C:\\path\\to\\file")]
    [InlineData("true")]
    [InlineData("12345")]
    [InlineData("2026-09-12")]
    [InlineData("- item")]
    [InlineData("@mention & *star !bang |pipe >fold %percent `tick")]
    [InlineData("emoji 🚀 and ñ")]
    [InlineData("01J8ZQ4M9T3N7K5W2X6Y8V0B1C")]
    [InlineData("0123456789ABCDEF0123456789")]
    public void YamlScalar_Format_RoundTripsThroughTheParser(string value)
    {
        var text = $"---\ntitle: {YamlScalar.Format(value)}\n---\n";

        var document = NoteDocumentParser.Parse(text, "fallback");

        document.FrontmatterStatus.Should().Be(FrontmatterStatus.Valid);
        document.Metadata.Title.Should().Be(value);
    }

    [Fact]
    public void SetScalars_ExistingKey_ReplacesOnlyThatLine()
    {
        const string text = "---\n# my comment\ntitle: Old\ncustom: keep me\ntags: [a, b]\n---\nBody\n";

        var result = FrontmatterEditor.SetScalars(text, [new FrontmatterAssignment("title", "New")]);

        result.Should().Be("---\n# my comment\ntitle: New\ncustom: keep me\ntags: [a, b]\n---\nBody\n");
    }

    [Fact]
    public void SetScalars_MissingKeys_AreInsertedAtRequestedPlacement()
    {
        const string text = "---\ntitle: T\n---\nBody\n";

        var result = FrontmatterEditor.SetScalars(
            text,
            [
                new FrontmatterAssignment("id", "ABC", FrontmatterPlacement.Start),
                new FrontmatterAssignment("updated", "2026-09-30"),
            ]);

        result.Should().Be("---\nid: ABC\ntitle: T\nupdated: 2026-09-30\n---\nBody\n");
    }

    [Fact]
    public void SetScalars_NoFrontmatter_CreatesBlockAboveContent()
    {
        var result = FrontmatterEditor.SetScalars("# Heading\n", [new FrontmatterAssignment("id", "ABC", FrontmatterPlacement.Start)]);

        result.Should().Be("---\nid: ABC\n---\n\n# Heading\n");
    }

    [Fact]
    public void SetScalars_EmptyDocument_CreatesOnlyTheBlock()
    {
        FrontmatterEditor.SetScalars(string.Empty, [new FrontmatterAssignment("id", "ABC")]).Should().Be("---\nid: ABC\n---\n");
    }

    [Fact]
    public void SetScalars_MultiLineValue_IsReplacedEntirely()
    {
        const string text = "---\ntitle: >\n  folded\n  title\n\n  continues\nother: 1\n---\n";

        var result = FrontmatterEditor.SetScalars(text, [new FrontmatterAssignment("title", "Flat")]);

        result.Should().Be("---\ntitle: Flat\nother: 1\n---\n");
    }

    [Fact]
    public void SetScalars_BlockSequenceAtColumnZero_IsReplacedEntirely()
    {
        const string text = "---\nupdated:\n- weird\n- list\nnext: 1\n---\n";

        var result = FrontmatterEditor.SetScalars(text, [new FrontmatterAssignment("updated", "2026-09-30")]);

        result.Should().Be("---\nupdated: 2026-09-30\nnext: 1\n---\n");
    }

    [Fact]
    public void SetScalars_KeyPrefixOfAnotherKey_IsNotConfused()
    {
        const string text = "---\ntitle_long: keep\nidentifier: keep\n---\n";

        var result = FrontmatterEditor.SetScalars(
            text,
            [new FrontmatterAssignment("title", "T"), new FrontmatterAssignment("id", "X", FrontmatterPlacement.Start)]);

        result.Should().Be("---\nid: X\ntitle_long: keep\nidentifier: keep\ntitle: T\n---\n");
    }

    [Fact]
    public void SetScalars_PreservesKeyCasingAndWindowsLineEndings()
    {
        const string text = "---\r\nTitle: Old\r\n---\r\nBody\r\n";

        var result = FrontmatterEditor.SetScalars(text, [new FrontmatterAssignment("title", "New"), new FrontmatterAssignment("updated", "2026-09-30")]);

        result.Should().Be("---\r\nTitle: New\r\nupdated: 2026-09-30\r\n---\r\nBody\r\n");
    }

    [Fact]
    public void SetScalars_NoAssignments_ReturnsSameInstance()
    {
        const string text = "anything";

        FrontmatterEditor.SetScalars(text, []).Should().BeSameAs(text);
    }

    [Fact]
    public void Stamp_NoteWithoutId_GetsIdAndUpdatedDate()
    {
        const string text = "---\ntitle: T\ncustom: x # keep\n---\nBody\n";

        var stamped = NoteStamper.Stamp(text, "fallback", new SequentialNoteIdGenerator(), _today);

        stamped.Text.Should().Be("---\nid: 01TEST00000000000000000001\ntitle: T\ncustom: x # keep\nupdated: 2026-09-30\n---\nBody\n");
        stamped.Document.Metadata.Id!.Value.Value.Should().Be("01TEST00000000000000000001");
        stamped.Document.Metadata.Updated.Should().Be(_today);
    }

    [Fact]
    public void Stamp_NoteUpToDate_IsReturnedUntouched()
    {
        const string text = "---\nid: ABC\nupdated: 2026-09-30\n---\nBody\n";
        var generator = new SequentialNoteIdGenerator();

        var stamped = NoteStamper.Stamp(text, "fallback", generator, _today);

        stamped.Text.Should().BeSameAs(text);
        generator.NewId().Value.Should().EndWith("1", "no id must have been consumed");
    }

    [Fact]
    public void Stamp_OldUpdatedDate_IsBumped()
    {
        var stamped = NoteStamper.Stamp("---\nid: ABC\nupdated: 2026-01-01\n---\nBody\n", "fallback", new SequentialNoteIdGenerator(), _today);

        stamped.Text.Should().Be("---\nid: ABC\nupdated: 2026-09-30\n---\nBody\n");
    }

    [Fact]
    public void Stamp_NoteWithoutFrontmatter_GetsNewBlockAndKeepsBody()
    {
        var stamped = NoteStamper.Stamp("# Title\n\nText\n", "fallback", new SequentialNoteIdGenerator(), _today);

        stamped.Text.Should().Be("---\nid: 01TEST00000000000000000001\nupdated: 2026-09-30\n---\n\n# Title\n\nText\n");
        stamped.Document.Metadata.Title.Should().Be("Title");
    }

    [Fact]
    public void Stamp_InvalidFrontmatter_IsNeverRewritten()
    {
        const string text = "---\ntitle: [broken\n---\nBody\n";

        var stamped = NoteStamper.Stamp(text, "fallback", new SequentialNoteIdGenerator(), _today);

        stamped.Text.Should().BeSameAs(text);
        stamped.Document.FrontmatterStatus.Should().Be(FrontmatterStatus.Invalid);
    }

    [Fact]
    public void Stamp_PatchThatWouldBreakTheBlock_IsDiscarded()
    {
        // A quoted key is valid YAML the textual editor does not recognise: appending `updated`
        // would create a duplicate key, so the stamp must be abandoned rather than corrupt the file.
        const string text = "---\nid: ABC\n\"updated\": 2026-01-01\n---\nBody\n";

        var stamped = NoteStamper.Stamp(text, "fallback", new SequentialNoteIdGenerator(), _today);

        stamped.Text.Should().BeSameAs(text);
        stamped.Document.FrontmatterStatus.Should().Be(FrontmatterStatus.Valid);
    }

    [Fact]
    public void SetTitle_UpdatesOrAddsTheTitleKey()
    {
        NoteStamper.SetTitle("---\ntitle: Old\n---\nB\n", "f", "New: title").Text
            .Should().Be("---\ntitle: \"New: title\"\n---\nB\n");
        NoteStamper.SetTitle("---\nid: X\n---\nB\n", "f", "Added").Text
            .Should().Be("---\nid: X\ntitle: Added\n---\nB\n");
        NoteStamper.SetTitle("B\n", "f", "Created").Document.Metadata.Title.Should().Be("Created");
    }

    [Fact]
    public void CreateDefault_ProducesParseableNoteWithAllFields()
    {
        var id = NoteId.Parse("01J8ZQ4M9T3N7K5W2X6Y8V0B1C");

        var text = NoteTemplates.CreateDefault(id, "Bug: deadlock #42", NoteType.Bug, " cslinq ", _today);
        var document = NoteDocumentParser.Parse(text, "fallback");

        document.FrontmatterStatus.Should().Be(FrontmatterStatus.Valid);
        document.Metadata.Should().BeEquivalentTo(new NoteMetadata
        {
            Id = id,
            Title = "Bug: deadlock #42",
            Project = "cslinq",
            Type = NoteType.Bug,
            Created = _today,
            Updated = _today,
        });
        document.Body.Should().Be("\n# Bug: deadlock #42\n\n");
    }

    [Fact]
    public void CreateDefault_WithoutProject_OmitsTheKey()
    {
        var text = NoteTemplates.CreateDefault(NoteId.Parse("ABC"), "T", NoteType.Note, null, _today);

        text.Should().NotContain("project:");
        NoteDocumentParser.Parse(text, "f").Metadata.Project.Should().BeNull();
    }

    [Fact]
    public void MarkdownOutline_Extract_ReturnsTopLevelHeadingsWithLines()
    {
        const string markdown = "# One\n\ntext\n\n## Two with `code` and **bold**\n\n> # quoted\n\n```\n# in code\n```\n\nSetext\n======\n\n###### Six\n";

        var outline = MarkdownOutline.Extract(markdown);

        outline.Should().Equal(
            new OutlineHeading(1, "One", 0),
            new OutlineHeading(2, "Two with code and bold", 4),
            new OutlineHeading(1, "Setext", 12),
            new OutlineHeading(6, "Six", 15));
    }

    [Fact]
    public void MarkdownOutline_Extract_EmptyInput_ReturnsNothing()
    {
        MarkdownOutline.Extract(string.Empty).Should().BeEmpty();
        MarkdownOutline.Extract("#\n\nno real heading").Should().BeEmpty();
    }
}
