using System.Collections.Concurrent;
using System.Globalization;
using System.Text;
using DevNotes.Application.Search;

namespace DevNotes.Infrastructure.Indexing;

/// <summary>One end of a date filter as it appears in the SQL.</summary>
internal enum Bound
{
    None,
    Exclusive,
    Inclusive,
}

/// <summary>Which parts a search statement needs; every distinct shape is built once and cached.</summary>
internal readonly record struct SearchShape(
    bool HasMatch,
    bool Trigram,
    NoteSortOrder Sort,
    int Projects,
    int Tags,
    int Types,
    int Commits,
    int Tickets,
    Bound CreatedFrom,
    Bound CreatedTo,
    Bound UpdatedFrom,
    Bound UpdatedTo,
    bool Exclude)
{
    public static SearchShape Of(SearchQuery query, bool trigram) => new(
        query.Query.Match is not null,
        trigram,
        query.Sort,
        query.Filter.Projects.Count,
        query.Filter.Tags.Count,
        query.Filter.Types.Count,
        query.Filter.Commits.Count,
        query.Filter.Tickets.Count,
        BoundOf(query.Filter.Created.From),
        BoundOf(query.Filter.Created.To),
        BoundOf(query.Filter.Updated.From),
        BoundOf(query.Filter.Updated.To),
        query.Query.Exclude is not null);

    /// <summary>Whether anything restricts the notes besides the text.</summary>
    public bool HasRestriction =>
        Projects + Tags + Types + Commits + Tickets > 0
        || CreatedFrom != Bound.None || CreatedTo != Bound.None || UpdatedFrom != Bound.None || UpdatedTo != Bound.None
        || Exclude;

    private static Bound BoundOf(DateBound? bound) => bound switch
    {
        null => Bound.None,
        { Inclusive: true } => Bound.Inclusive,
        _ => Bound.Exclusive,
    };
}

/// <summary>
/// Builds the search statements. Parameter names are fixed by convention so the index binds them
/// without looking at the SQL: <c>$match</c>, <c>$exclude</c>, <c>$limit</c>, the snippet markers,
/// <c>$p0…</c> project keys, <c>$t0…</c> tags, <c>$ty0…</c> types, <c>$c0…</c> commits, <c>$tk0…</c>
/// tickets and <c>$cf</c>/<c>$ct</c>/<c>$uf</c>/<c>$ut</c> date bounds.
/// </summary>
internal static class SearchSql
{
    // How a search is shaped (measured with benchmarks/DevNotes.Benchmarks on 5,000 notes):
    //
    // 1. The innermost query picks WHICH notes are returned without touching the wide `notes` rows.
    //    SQLite evaluates result columns before ORDER BY … LIMIT, so a single flat query would run
    //    snippet() and highlight() - which read and tokenize the whole text - and join `notes` for
    //    every match. The first letters the user types match almost every note, so that is
    //    thousands of rows read to keep thirty.
    // 2. The outer query builds the highlighted fragments for the chosen notes only.
    //
    // Filters are resolved the same way: a subquery yields the rowids of the notes that satisfy them
    // from the small indexes (project_key, type, dates, tags, links) and the full-text scan only
    // checks membership in that set. Joining `notes` inside the full-text loop instead costs one
    // wide-row read per match (measured: twice the time of the unfiltered search).
    //
    // The unary plus in `+x.rowid IN (…)` is deliberate: it stops the planner from using the list as
    // a rowid lookup (which would restart the full-text query once per row) and keeps it as a cheap
    // membership filter on a single scan.

    private static readonly ConcurrentDictionary<SearchShape, string> _cache = new();

    public static string For(SearchShape shape) => _cache.GetOrAdd(shape, Build);

    private static string Build(SearchShape shape)
    {
        var index = shape.Trigram ? IndexSchema.TrigramIndex : IndexSchema.WordIndex;
        var restriction = shape.HasRestriction ? RowIdsSatisfying(shape) : null;

        if (!shape.HasMatch)
        {
            // A listing restricted by filters and exclusions: the excerpt replaces the highlighted fragment.
            // Without the unary plus the planner looks the (few) selected rows up by rowid and sorts them.
            var order = shape.Sort == NoteSortOrder.TitleAscending ? IndexSchema.TitleOrder : IndexSchema.RecentOrder;
            var where = restriction is null ? string.Empty : $"\nWHERE n.rowid IN ({restriction})";
            return $"""
                SELECT {IndexSchema.Columns("n")}, substr(n.body, 1, {IndexSchema.ExcerptLength})
                FROM notes AS n{where}
                ORDER BY {IndexSchema.Order(order, "n")}
                LIMIT $limit
                """;
        }

        var score = $"bm25({index}, {IndexSchema.Bm25Weights})";
        var hits = $"""
            SELECT {IndexSchema.Columns("n")},
                   snippet({index}, 1, $start, $end, $ellipsis, 18),
                   highlight({index}, 0, $start, $end),
                   {score} AS score
            FROM {index}
            JOIN notes AS n ON n.rowid = {index}.rowid
            WHERE {index} MATCH $match
            """;

        if (shape.Sort == NoteSortOrder.Relevance)
        {
            // Best matches first; among equally relevant notes, the most recent first.
            var membership = restriction is null ? string.Empty : $"\n            AND +{index}.rowid IN ({restriction})";
            return $"""
                {hits}
                  AND +{index}.rowid IN (
                      SELECT {index}.rowid
                      FROM {index}
                      WHERE {index} MATCH $match{membership}
                      ORDER BY {score}, {index}.rowid
                      LIMIT $limit)
                ORDER BY score, n.sort_date DESC, n.rowid
                LIMIT $limit
                """;
        }

        // Matches in the order of one of the sort indexes: the index is walked in order and each entry
        // is checked against the set of matching rows, so the scan stops as soon as the limit is reached.
        var sortOrder = shape.Sort == NoteSortOrder.TitleAscending ? IndexSchema.TitleOrder : IndexSchema.RecentOrder;
        var restricted = restriction is null ? string.Empty : $"\n            AND +m.rowid IN ({restriction})";
        return $"""
            {hits}
              AND +{index}.rowid IN (
                  SELECT m.rowid
                  FROM notes AS m
                  WHERE +m.rowid IN (SELECT rowid FROM {index} WHERE {index} MATCH $match){restricted}
                  ORDER BY {IndexSchema.Order(sortOrder, "m")}
                  LIMIT $limit)
            ORDER BY {IndexSchema.Order(sortOrder, "n")}
            LIMIT $limit
            """;
    }

    /// <summary>The rowids of the notes that satisfy every filter and no exclusion, answered from the small indexes.</summary>
    private static string RowIdsSatisfying(SearchShape shape)
    {
        var sql = new StringBuilder("SELECT x.rowid FROM notes AS x WHERE 1 = 1");
        if (shape.Projects > 0)
        {
            sql.Append(" AND x.project_key IN (").Append(Parameters("$p", shape.Projects)).Append(')');
        }

        if (shape.Types > 0)
        {
            sql.Append(" AND x.type IN (").Append(Parameters("$ty", shape.Types)).Append(')');
        }

        for (var i = 0; i < shape.Tags; i++)
        {
            // One probe of the tag index per tag; the id index is covering, so no note row is read.
            sql.Append(" AND x.id IN (SELECT note_id FROM tags WHERE tag = $t").Append(i.ToString(CultureInfo.InvariantCulture)).Append(')');
        }

        if (shape.Commits > 0)
        {
            // A short hash matches a full one and vice versa: whichever is shorter must be a prefix of the other.
            sql.Append(" AND x.id IN (SELECT note_id FROM links WHERE kind = 'commit' AND (");
            for (var i = 0; i < shape.Commits; i++)
            {
                var parameter = "$c" + i.ToString(CultureInfo.InvariantCulture);
                if (i > 0)
                {
                    sql.Append(" OR ");
                }

                sql.Append("substr(target, 1, length(").Append(parameter).Append(")) = ").Append(parameter).Append(" COLLATE NOCASE")
                    .Append(" OR substr(").Append(parameter).Append(", 1, length(target)) = target COLLATE NOCASE");
            }

            sql.Append("))");
        }

        if (shape.Tickets > 0)
        {
            sql.Append(" AND x.id IN (SELECT note_id FROM links WHERE kind = 'ticket' AND target COLLATE NOCASE IN (")
                .Append(Parameters("$tk", shape.Tickets)).Append("))");
        }

        Date(sql, "created", "$cf", shape.CreatedFrom, lower: true);
        Date(sql, "created", "$ct", shape.CreatedTo, lower: false);
        Date(sql, "updated", "$uf", shape.UpdatedFrom, lower: true);
        Date(sql, "updated", "$ut", shape.UpdatedTo, lower: false);

        if (shape.Exclude)
        {
            sql.Append(" AND x.rowid NOT IN (SELECT rowid FROM ").Append(IndexSchema.WordIndex)
                .Append(" WHERE ").Append(IndexSchema.WordIndex).Append(" MATCH $exclude)");
        }

        return sql.ToString();
    }

    /// <summary>Dates are stored as yyyy-MM-dd text, so ordinal comparison is chronological; a NULL date never matches.</summary>
    private static void Date(StringBuilder sql, string column, string parameter, Bound bound, bool lower)
    {
        if (bound == Bound.None)
        {
            return;
        }

        var comparison = (lower, bound) switch
        {
            (true, Bound.Inclusive) => ">=",
            (true, _) => ">",
            (false, Bound.Inclusive) => "<=",
            (false, _) => "<",
        };
        sql.Append(" AND x.").Append(column).Append(' ').Append(comparison).Append(' ').Append(parameter);
    }

    private static string Parameters(string prefix, int count) =>
        string.Join(", ", Enumerable.Range(0, count).Select(i => prefix + i.ToString(CultureInfo.InvariantCulture)));
}
