using DevNotes.Application.Search;
using DevNotes.Domain.Notes;

namespace DevNotes.Application.Tests.Search;

public sealed class NoteFilterTests
{
    private static readonly DateOnly _sep1 = new(2026, 9, 1);
    private static readonly DateOnly _sep30 = new(2026, 9, 30);

    [Fact]
    public void Empty_IsEmpty_AndMergingItChangesNothing()
    {
        var filter = new NoteFilter { Projects = ["a"] };

        NoteFilter.Empty.IsEmpty.Should().BeTrue();
        filter.Merge(NoteFilter.Empty).Should().BeSameAs(filter);
        NoteFilter.Empty.Merge(filter).Should().BeSameAs(filter);
    }

    [Fact]
    public void Merge_Projects_KeepsTheCommonOnesIgnoringCaseAndAccents()
    {
        var sidebar = new NoteFilter { Projects = ["Azure", "cslinq"] };
        var typed = new NoteFilter { Projects = ["AZURE", "Déploiement"] };

        var merged = sidebar.Merge(typed);

        merged.Projects.Should().Equal("Azure");
        merged.IsUnsatisfiable.Should().BeFalse();
        new NoteFilter { Projects = ["deploiement"] }.Merge(new NoteFilter { Projects = ["Déploiement"] }).Projects.Should().Equal("deploiement");
    }

    [Fact]
    public void Merge_DifferentProjects_IsUnsatisfiable()
    {
        var merged = new NoteFilter { Projects = ["a"] }.Merge(new NoteFilter { Projects = ["b"] });

        merged.IsUnsatisfiable.Should().BeTrue();
        merged.IsEmpty.Should().BeFalse();
    }

    [Fact]
    public void Merge_Tags_Accumulate()
    {
        var merged = new NoteFilter { Tags = [Tag.Create("a"), Tag.Create("b")] }.Merge(new NoteFilter { Tags = [Tag.Create("b"), Tag.Create("c")] });

        merged.Tags.Select(tag => tag.Value).Should().Equal("a", "b", "c");
    }

    [Fact]
    public void Merge_TypesTicketsAndCommits_AreIntersected()
    {
        var first = new NoteFilter { Types = [NoteType.Bug, NoteType.Adr], Tickets = ["MS-1", "MS-2"], Commits = ["abc"] };
        var second = new NoteFilter { Types = [NoteType.Adr], Tickets = ["ms-2"] };

        var merged = first.Merge(second);

        merged.Types.Should().Equal(NoteType.Adr);
        merged.Tickets.Should().Equal("MS-2");
        merged.Commits.Should().Equal(["abc"], "a side without values imposes nothing");
        first.Merge(new NoteFilter { Types = [NoteType.Snippet] }).IsUnsatisfiable.Should().BeTrue();
    }

    [Fact]
    public void Merge_Dates_TightenTheRange()
    {
        var first = new NoteFilter { Created = new DateRange(new DateBound(_sep1, Inclusive: true), null) };
        var second = new NoteFilter { Created = new DateRange(new DateBound(_sep1, Inclusive: false), new DateBound(_sep30, Inclusive: true)) };

        var merged = first.Merge(second);

        merged.Created.Should().Be(new DateRange(new DateBound(_sep1, Inclusive: false), new DateBound(_sep30, Inclusive: true)));
        merged.IsUnsatisfiable.Should().BeFalse();
    }

    [Fact]
    public void Merge_EmptyDateRange_IsUnsatisfiable()
    {
        var after = new NoteFilter { Updated = new DateRange(new DateBound(_sep30, Inclusive: false), null) };
        var before = new NoteFilter { Updated = new DateRange(null, new DateBound(_sep1, Inclusive: true)) };

        after.Merge(before).IsUnsatisfiable.Should().BeTrue();
        after.Merge(after).IsUnsatisfiable.Should().BeFalse();
    }

    [Fact]
    public void DateRange_SameDay_IsOnlySatisfiableWhenBothEndsAreInclusive()
    {
        DateRange.Exactly(_sep1).IsUnsatisfiable.Should().BeFalse();
        new DateRange(new DateBound(_sep1, Inclusive: true), new DateBound(_sep1, Inclusive: false)).IsUnsatisfiable.Should().BeTrue();
    }

    [Fact]
    public void DateRange_Contains_HonoursInclusiveAndExclusiveBounds()
    {
        var range = new DateRange(new DateBound(_sep1, Inclusive: false), new DateBound(_sep30, Inclusive: true));

        range.Contains(_sep1).Should().BeFalse();
        range.Contains(_sep1.AddDays(1)).Should().BeTrue();
        range.Contains(_sep30).Should().BeTrue();
        range.Contains(_sep30.AddDays(1)).Should().BeFalse();
        DateRange.Empty.Contains(_sep1).Should().BeTrue();
    }
}
