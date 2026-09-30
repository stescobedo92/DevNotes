using DevNotes.Domain.Notes;

namespace DevNotes.Domain.Tests.Notes;

public sealed class NotePathTests
{
    [Theory]
    [InlineData("note.md", "note.md")]
    [InlineData("bugs/deadlock.md", "bugs/deadlock.md")]
    [InlineData(@"bugs\2026\deadlock.md", "bugs/2026/deadlock.md")]
    [InlineData("Notas con espacios/añadido ñ.md", "Notas con espacios/añadido ñ.md")]
    [InlineData("UPPER.MD", "UPPER.MD")]
    public void TryCreate_ValidRelativePath_NormalizesSeparators(string input, string expected)
    {
        NotePath.TryCreate(input, out var path, out var error).Should().BeTrue(error);

        path.Value.Should().Be(expected);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("../secret.md")]
    [InlineData("a/../../secret.md")]
    [InlineData(@"..\..\windows\system32\evil.md")]
    [InlineData("./note.md")]
    [InlineData("/etc/passwd.md")]
    [InlineData(@"\\server\share\note.md")]
    [InlineData(@"C:\Users\me\note.md")]
    [InlineData("C:note.md")]
    [InlineData("a//b.md")]
    [InlineData("note.txt")]
    [InlineData("note")]
    [InlineData(".md")]
    [InlineData("folder/.md")]
    [InlineData("con.md")]
    [InlineData("folder/NUL/note.md")]
    [InlineData("lpt1.backup.md")]
    [InlineData("what?.md")]
    [InlineData("pipe|name.md")]
    [InlineData("trailing /note.md")]
    [InlineData("trailing./note.md")]
    [InlineData("tab\tname.md")]
    [InlineData("nul\0byte.md")]
    public void TryCreate_UnsafeOrNonPortablePath_IsRejected(string? input)
    {
        NotePath.TryCreate(input, out var path, out var error).Should().BeFalse();

        path.IsEmpty.Should().BeTrue();
        error.Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public void TryCreate_PathLongerThanLimit_IsRejected()
    {
        var input = new string('a', NotePath.MaxLength) + ".md";

        NotePath.TryCreate(input, out _, out var error).Should().BeFalse();

        error.Should().Contain("longer");
    }

    [Fact]
    public void Create_InvalidPath_ThrowsWithReason()
    {
        var act = () => NotePath.Create("../x.md");

        act.Should().Throw<ArgumentException>().WithMessage("*relative navigation*");
    }

    [Fact]
    public void Parts_AreDerivedFromValue()
    {
        var path = NotePath.Create("bugs/2026/deadlock.md");

        path.FileName.Should().Be("deadlock.md");
        path.FileNameWithoutExtension.Should().Be("deadlock");
        path.Directory.Should().Be("bugs/2026");
        NotePath.Create("root.md").Directory.Should().BeEmpty();
    }

    [Theory]
    [InlineData(".git/note.md", true)]
    [InlineData("a/.obsidian/x.md", true)]
    [InlineData("a/.hidden.md", true)]
    [InlineData("a/visible.md", false)]
    [InlineData("a.b/c.d.md", false)]
    public void IsHidden_DetectsDotSegments(string input, bool expected)
    {
        NotePath.Create(input).IsHidden.Should().Be(expected);
    }

    [Theory]
    [InlineData("", "note", "note.md")]
    [InlineData(null, "note", "note.md")]
    [InlineData("bugs", "note", "bugs/note.md")]
    [InlineData(@"\bugs\2026\", "note", "bugs/2026/note.md")]
    [InlineData(" /bugs/ ", "note", "bugs/note.md")]
    public void TryCombine_BuildsPathInsideFolder(string? directory, string name, string expected)
    {
        NotePath.TryCombine(directory, name, out var path, out var error).Should().BeTrue(error);

        path.Value.Should().Be(expected);
    }

    [Theory]
    [InlineData("..", "note")]
    [InlineData("bugs/../..", "note")]
    [InlineData("bugs", "../escape")]
    [InlineData("bugs", "sub/name")]
    [InlineData("bugs", @"sub\name")]
    [InlineData("bugs", "")]
    public void TryCombine_TraversalOrSeparatorInName_IsRejected(string directory, string name)
    {
        NotePath.TryCombine(directory, name, out var path, out var error).Should().BeFalse();

        path.IsEmpty.Should().BeTrue();
        error.Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public void WithFileName_NameWithSeparator_Throws()
    {
        var act = () => NotePath.Create("bugs/old.md").WithFileName("../../escape");

        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void WithFileName_KeepsDirectory()
    {
        var renamed = NotePath.Create("bugs/old.md").WithFileName("new-name");

        renamed.Value.Should().Be("bugs/new-name.md");
    }

    [Fact]
    public void Equality_IsOrdinalOnValue()
    {
        NotePath.Create("a/b.md").Should().Be(NotePath.Create(@"a\b.md"));
        NotePath.Create("a/b.md").Should().NotBe(NotePath.Create("a/B.md"));
        (NotePath.Create("a.md") < NotePath.Create("b.md")).Should().BeTrue();
    }
}
