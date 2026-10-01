using DevNotes.Domain.Common;
using DevNotes.Domain.Notes;

namespace DevNotes.Application.Search;

/// <summary>One end of a date range.</summary>
public readonly record struct DateBound(DateOnly Date, bool Inclusive);

/// <summary>Inclusive or exclusive bounds on a note date; either end may be open.</summary>
public sealed record DateRange(DateBound? From, DateBound? To)
{
    public static DateRange Empty { get; } = new(null, null);

    public bool IsEmpty => From is null && To is null;

    /// <summary>True when no date can satisfy both bounds.</summary>
    public bool IsUnsatisfiable =>
        From is { } from && To is { } to
        && (from.Date > to.Date || (from.Date == to.Date && !(from.Inclusive && to.Inclusive)));

    public static DateRange Exactly(DateOnly date) => new(new DateBound(date, Inclusive: true), new DateBound(date, Inclusive: true));

    public static DateRange Between(DateOnly first, DateOnly last) =>
        new(new DateBound(first, Inclusive: true), new DateBound(last, Inclusive: true));

    /// <summary>The dates that satisfy both ranges.</summary>
    public DateRange Intersect(DateRange other)
    {
        ArgumentNullException.ThrowIfNull(other);
        return new DateRange(Tighter(From, other.From, later: true), Tighter(To, other.To, later: false));
    }

    public bool Contains(DateOnly date) =>
        (From is not { } from || date > from.Date || (date == from.Date && from.Inclusive))
        && (To is not { } to || date < to.Date || (date == to.Date && to.Inclusive));

    private static DateBound? Tighter(DateBound? a, DateBound? b, bool later)
    {
        if (a is not { } first)
        {
            return b;
        }

        if (b is not { } second)
        {
            return a;
        }

        if (first.Date == second.Date)
        {
            return new DateBound(first.Date, first.Inclusive && second.Inclusive);
        }

        return (first.Date > second.Date) == later ? first : second;
    }
}

/// <summary>
/// Structured constraints on the notes returned by a query, combined with AND. Values of one
/// multi-valued field are alternatives (a note in project A or B), except tags, which all have to
/// be present. Projects, tickets and commits are compared without regard to case or diacritics.
/// </summary>
public sealed record NoteFilter
{
    public static NoteFilter Empty { get; } = new();

    public IReadOnlyList<string> Projects { get; init; } = [];

    public IReadOnlyList<Tag> Tags { get; init; } = [];

    public IReadOnlyList<NoteType> Types { get; init; } = [];

    /// <summary>Commit hashes; a note matches when one of its commits starts with the value or vice versa.</summary>
    public IReadOnlyList<string> Commits { get; init; } = [];

    public IReadOnlyList<string> Tickets { get; init; } = [];

    public DateRange Created { get; init; } = DateRange.Empty;

    public DateRange Updated { get; init; } = DateRange.Empty;

    /// <summary>
    /// The filter demands values no note can have at once (two different projects, a type that does
    /// not exist, an empty date range): the result is known to be empty without asking the index.
    /// </summary>
    public bool IsUnsatisfiable { get; init; }

    public bool IsEmpty =>
        !IsUnsatisfiable
        && Projects.Count == 0
        && Tags.Count == 0
        && Types.Count == 0
        && Commits.Count == 0
        && Tickets.Count == 0
        && Created.IsEmpty
        && Updated.IsEmpty;

    /// <summary>Both filters at once (AND). Alternatives of one field are intersected; tags accumulate.</summary>
    public NoteFilter Merge(NoteFilter other)
    {
        ArgumentNullException.ThrowIfNull(other);
        if (other.IsEmpty)
        {
            return this;
        }

        if (IsEmpty)
        {
            return other;
        }

        var unsatisfiable = IsUnsatisfiable || other.IsUnsatisfiable;
        var projects = IntersectText(Projects, other.Projects, ref unsatisfiable);
        var commits = IntersectText(Commits, other.Commits, ref unsatisfiable);
        var tickets = IntersectText(Tickets, other.Tickets, ref unsatisfiable);
        var types = Intersect(Types, other.Types, ref unsatisfiable);
        var created = Created.Intersect(other.Created);
        var updated = Updated.Intersect(other.Updated);
        unsatisfiable |= created.IsUnsatisfiable || updated.IsUnsatisfiable;

        return new NoteFilter
        {
            Projects = projects,
            Tags = [.. Tags.Union(other.Tags)],
            Types = types,
            Commits = commits,
            Tickets = tickets,
            Created = created,
            Updated = updated,
            IsUnsatisfiable = unsatisfiable,
        };
    }

    private static IReadOnlyList<string> IntersectText(IReadOnlyList<string> first, IReadOnlyList<string> second, ref bool unsatisfiable)
    {
        if (first.Count == 0)
        {
            return second;
        }

        if (second.Count == 0)
        {
            return first;
        }

        var keys = second.Select(TextKey.Of).ToHashSet(StringComparer.Ordinal);
        var common = first.Where(value => keys.Contains(TextKey.Of(value))).ToList();
        unsatisfiable |= common.Count == 0;
        return common;
    }

    private static IReadOnlyList<T> Intersect<T>(IReadOnlyList<T> first, IReadOnlyList<T> second, ref bool unsatisfiable)
        where T : struct
    {
        if (first.Count == 0)
        {
            return second;
        }

        if (second.Count == 0)
        {
            return first;
        }

        var common = first.Intersect(second).ToList();
        unsatisfiable |= common.Count == 0;
        return common;
    }
}
