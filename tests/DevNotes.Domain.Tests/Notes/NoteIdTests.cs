using DevNotes.Domain.Notes;
using DevNotes.Domain.Vaults;

namespace DevNotes.Domain.Tests.Notes;

public sealed class NoteIdTests
{
    [Fact]
    public void NewId_ProducesUlid()
    {
        var id = NoteId.NewId(new DateTimeOffset(2026, 9, 12, 0, 0, 0, TimeSpan.Zero));

        id.Value.Should().HaveLength(26);
        id.IsPathDerived.Should().BeFalse();
        NoteId.TryParse(id.Value, out var parsed).Should().BeTrue();
        parsed.Should().Be(id);
    }

    [Theory]
    [InlineData("01J8ZQ4M9T3N7K5W2X6Y8V0B1C")]
    [InlineData("202409121030")]
    [InlineData("my-note_v2.1")]
    [InlineData("  padded  ")]
    public void TryParse_SafeToken_IsAccepted(string text)
    {
        NoteId.TryParse(text, out var id).Should().BeTrue();

        id.Value.Should().Be(text.Trim());
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("has space")]
    [InlineData("../escape")]
    [InlineData("a/b")]
    [InlineData("quote\"inside")]
    [InlineData(".hidden")]
    [InlineData("trailing.")]
    [InlineData("ñandú")]
    public void TryParse_UnsafeToken_IsRejected(string? text)
    {
        NoteId.TryParse(text, out var id).Should().BeFalse();

        id.IsEmpty.Should().BeTrue();
    }

    [Fact]
    public void TryParse_TooLong_IsRejected()
    {
        NoteId.TryParse(new string('a', 65), out _).Should().BeFalse();
        NoteId.TryParse(new string('a', 64), out _).Should().BeTrue();
    }

    [Fact]
    public void Parse_Invalid_ThrowsFormatException()
    {
        var act = () => NoteId.Parse("not valid");

        act.Should().Throw<FormatException>();
    }

    [Fact]
    public void FromPath_IsDeterministicAndPathSpecific()
    {
        var first = NoteId.FromPath(NotePath.Create("a/b.md"));
        var again = NoteId.FromPath(NotePath.Create(@"a\b.md"));
        var other = NoteId.FromPath(NotePath.Create("a/c.md"));

        first.Should().Be(again);
        first.Should().NotBe(other);
        first.IsPathDerived.Should().BeTrue();
        NoteId.TryParse(first.Value, out _).Should().BeTrue("a path-derived id must round-trip through the index");
    }

    [Fact]
    public void FromPath_EmptyPath_Throws()
    {
        var act = () => NoteId.FromPath(default);

        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void UlidGenerator_UsesInjectedClock()
    {
        var clock = new FixedTimeProvider(new DateTimeOffset(2030, 1, 1, 0, 0, 0, TimeSpan.Zero));
        var generator = new UlidNoteIdGenerator(clock);

        var first = generator.NewId();
        var second = generator.NewId();

        first.Should().NotBe(second);
        first.Value[..10].Should().Be(second.Value[..10], "both ids share the same millisecond timestamp");
    }

    [Theory]
    [InlineData("01J8ZQ4M9T3N7K5W2X6Y8V0B1C", true)]
    [InlineData("vault_1", true)]
    [InlineData(@"..\..\evil", false)]
    [InlineData("a/b", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void VaultId_TryParse_OnlyAcceptsSafeTokens(string? text, bool expected)
    {
        VaultId.TryParse(text, out _).Should().Be(expected);
    }

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
