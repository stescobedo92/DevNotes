using DevNotes.Domain.Notes;
using DevNotes.Domain.Vaults;

namespace DevNotes.Domain.Tests.Notes;

public sealed class ValueObjectTests
{
    [Theory]
    [InlineData("SQL-Server", "sql-server")]
    [InlineData("  #Performance ", "performance")]
    [InlineData("design decision", "design-decision")]
    [InlineData("C#", "c#")]
    [InlineData("c++", "c++")]
    [InlineData(".NET", ".net")]
    [InlineData("multi   space\ttab", "multi-space-tab")]
    public void Tag_TryCreate_Normalizes(string raw, string expected)
    {
        Tag.TryCreate(raw, out var tag).Should().BeTrue();

        tag.Value.Should().Be(expected);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("  ")]
    [InlineData("a,b")]
    [InlineData("[flow]")]
    [InlineData("quo\"te")]
    [InlineData("#")]
    [InlineData("#  ")]
    public void Tag_TryCreate_InvalidInput_IsRejected(string? raw)
    {
        Tag.TryCreate(raw, out var tag).Should().BeFalse();

        tag.Value.Should().BeEmpty();
    }

    [Fact]
    public void Tag_TryCreate_TooLong_IsRejected()
    {
        Tag.TryCreate(new string('x', Tag.MaxLength + 1), out _).Should().BeFalse();
    }

    [Fact]
    public void Tag_NormalizeMany_DropsInvalidAndDuplicatesKeepingOrder()
    {
        var tags = Tag.NormalizeMany(["SQL", "deadlock", "sql", null, " ", "a,b", "Perf"]);

        tags.Select(t => t.Value).Should().Equal("sql", "deadlock", "perf");
    }

    [Theory]
    [InlineData("bug", NoteType.Bug)]
    [InlineData("ADR", NoteType.Adr)]
    [InlineData(" runbook ", NoteType.Runbook)]
    [InlineData("learning", NoteType.Learning)]
    [InlineData("snippet", NoteType.Snippet)]
    [InlineData("note", NoteType.Note)]
    public void NoteType_TryParse_IsCaseInsensitive(string text, NoteType expected)
    {
        NoteTypes.TryParse(text, out var type).Should().BeTrue();

        type.Should().Be(expected);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("meeting")]
    public void NoteType_ParseOrDefault_UnknownValue_FallsBackToNote(string? text)
    {
        NoteTypes.TryParse(text, out _).Should().BeFalse();
        NoteTypes.ParseOrDefault(text).Should().Be(NoteType.Note);
    }

    [Fact]
    public void NoteType_ToKey_RoundTripsForEveryValue()
    {
        foreach (var type in NoteTypes.All)
        {
            NoteTypes.TryParse(type.ToKey(), out var parsed).Should().BeTrue();
            parsed.Should().Be(type);
        }

        NoteTypes.All.Should().BeEquivalentTo(Enum.GetValues<NoteType>());
    }

    [Theory]
    [InlineData("Deadlock en actualización de inventario", "deadlock-en-actualizacion-de-inventario")]
    [InlineData("  ¿Qué pasó con el índice FTS5?  ", "que-paso-con-el-indice-fts5")]
    [InlineData("C# / .NET: async & await", "c-net-async-await")]
    [InlineData("snake_case_name", "snake-case-name")]
    [InlineData("---", "untitled")]
    [InlineData("", "untitled")]
    [InlineData(null, "untitled")]
    [InlineData("CON", "con-note")]
    [InlineData("日本語 タイトル", "日本語-タイトル")]
    public void Slug_From_ProducesPortableFileName(string? title, string expected)
    {
        var slug = Slug.From(title);

        slug.Should().Be(expected);
        NotePath.TryCombine(null, slug, out _, out var error).Should().BeTrue(error);
    }

    [Fact]
    public void Slug_From_LongTitle_IsTruncatedWithoutTrailingDash()
    {
        var slug = Slug.From(string.Join(' ', Enumerable.Repeat("palabra", 40)));

        slug.Length.Should().BeLessThanOrEqualTo(Slug.MaxLength);
        slug.Should().NotEndWith("-").And.NotStartWith("-");
    }

    [Theory]
    [InlineData("  Título   con\tespacios \n y saltos ", "Título con espacios y saltos")]
    [InlineData("Simple", "Simple")]
    public void NoteTitle_TryNormalize_CollapsesWhitespace(string raw, string expected)
    {
        NoteTitle.TryNormalize(raw, out var title).Should().BeTrue();

        title.Should().Be(expected);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" \r\n\t ")]
    public void NoteTitle_TryNormalize_Blank_IsRejected(string? raw)
    {
        NoteTitle.TryNormalize(raw, out var title).Should().BeFalse();

        title.Should().BeNull();
    }

    [Fact]
    public void NoteTitle_TryNormalize_TooLong_IsRejected()
    {
        NoteTitle.TryNormalize(new string('t', NoteTitle.MaxLength + 1), out _).Should().BeFalse();
    }

    [Fact]
    public void ContentHash_Compute_IsStableSha256()
    {
        var hash = ContentHash.Compute("abc");

        hash.Hex.Should().Be("ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad");
        ContentHash.Compute("abc"u8).Should().Be(hash);
        ContentHash.FromHex(hash.Hex).Should().Be(hash);
        ContentHash.Compute("abd").Should().NotBe(hash);
    }

    [Theory]
    [InlineData("")]
    [InlineData("xyz")]
    [InlineData("BA7816BF8F01CFEA414140DE5DAE2223B00361A396177A9CB410FF61F20015AD")]
    public void ContentHash_FromHex_Malformed_Throws(string hex)
    {
        var act = () => ContentHash.FromHex(hex);

        act.Should().Throw<FormatException>();
    }

    [Theory]
    [InlineData("[[benchmark-where-select-2026-08]]", "benchmark-where-select-2026-08")]
    [InlineData("  [[ target | Alias ]] ", "target")]
    [InlineData("plain-target", "plain-target")]
    [InlineData("[[]]", "")]
    public void NoteLinks_NormalizeNoteTarget_StripsWikiSyntax(string reference, string expected)
    {
        NoteLinks.NormalizeNoteTarget(reference).Should().Be(expected);
    }

    [Fact]
    public void NoteLinks_Enumerate_FlattensAllKinds()
    {
        var links = new NoteLinks(["a3f9c21"], ["MS-482"], ["other"]);

        links.IsEmpty.Should().BeFalse();
        links.Enumerate().Should().Equal(
            (NoteLinkKind.Commit, "a3f9c21"),
            (NoteLinkKind.Ticket, "MS-482"),
            (NoteLinkKind.Note, "other"));
        NoteLinks.Empty.IsEmpty.Should().BeTrue();
        NoteLinkKind.Commit.ToKey().Should().Be("commit");
        NoteLinkKind.Ticket.ToKey().Should().Be("ticket");
        NoteLinkKind.Note.ToKey().Should().Be("note");
    }

    [Fact]
    public void Vault_Constructor_NormalizesRootAndValidates()
    {
        var root = Path.Combine(Path.GetTempPath(), "vault-x") + Path.DirectorySeparatorChar;
        var id = VaultId.NewId(DateTimeOffset.UtcNow);

        var vault = new Vault(id, "  Work  ", root);

        vault.Name.Should().Be("Work");
        vault.RootPath.Should().Be(Path.TrimEndingDirectorySeparator(Path.GetFullPath(root)));
        FluentActions.Invoking(() => new Vault(id, "Work", "relative/path")).Should().Throw<ArgumentException>();
        FluentActions.Invoking(() => new Vault(default, "Work", root)).Should().Throw<ArgumentException>();
        FluentActions.Invoking(() => new Vault(id, " ", root)).Should().Throw<ArgumentException>();
    }
}
