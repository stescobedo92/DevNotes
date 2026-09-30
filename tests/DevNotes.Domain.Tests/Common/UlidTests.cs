using DevNotes.Domain.Common;

namespace DevNotes.Domain.Tests.Common;

public sealed class UlidTests
{
    private static readonly DateTimeOffset _instant = new(2026, 9, 12, 10, 30, 0, TimeSpan.Zero);

    [Fact]
    public void NewUlid_ReturnsTwentySixCrockfordCharacters()
    {
        var ulid = Ulid.NewUlid(_instant);

        ulid.Should().HaveLength(Ulid.Length).And.MatchRegex("^[0-7][0-9A-HJKMNP-TV-Z]{25}$");
        Ulid.IsValid(ulid).Should().BeTrue();
    }

    [Fact]
    public void NewUlid_SameMillisecond_IsStrictlyIncreasing()
    {
        var ids = Enumerable.Range(0, 1_000).Select(_ => Ulid.NewUlid(_instant.AddYears(1))).ToList();

        ids.Should().OnlyHaveUniqueItems();
        ids.Should().BeInAscendingOrder(StringComparer.Ordinal);
    }

    [Fact]
    public void NewUlid_LaterTimestamp_SortsAfterEarlierOne()
    {
        var earlier = Ulid.NewUlid(_instant.AddYears(2));
        var later = Ulid.NewUlid(_instant.AddYears(2).AddSeconds(1));

        string.CompareOrdinal(earlier, later).Should().BeNegative();
    }

    [Fact]
    public void NewUlid_FromManyThreads_NeverRepeats()
    {
        var ids = new System.Collections.Concurrent.ConcurrentBag<string>();

        Parallel.For(0, 5_000, _ => ids.Add(Ulid.NewUlid(_instant.AddYears(3))));

        ids.Should().OnlyHaveUniqueItems();
    }

    [Fact]
    public void NewUlid_TimestampBeforeEpoch_Throws()
    {
        var act = () => Ulid.NewUlid(DateTimeOffset.UnixEpoch.AddSeconds(-1));

        act.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Theory]
    [InlineData("")]
    [InlineData("01J8Z")]
    [InlineData("81J8ZQ4M9T3N7K5W2X6Y8V0B1C")] // first char above '7' overflows 48 bits
    [InlineData("01J8ZQ4M9T3N7K5W2X6Y8V0BIL")] // I and L are not in the alphabet
    public void IsValid_MalformedValue_ReturnsFalse(string value)
    {
        Ulid.IsValid(value).Should().BeFalse();
    }
}
