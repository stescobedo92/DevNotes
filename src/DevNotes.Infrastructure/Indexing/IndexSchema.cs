using System.Globalization;

namespace DevNotes.Infrastructure.Indexing;

/// <summary>
/// SQL of the index database. The schema version is stored in <c>PRAGMA user_version</c>; because
/// the index is disposable, a database with any other version is simply dropped and rebuilt.
/// </summary>
internal static class IndexSchema
{
    public const int Version = 2;

    public const string WordIndex = "notes_fts";
    public const string TrigramIndex = "notes_trigram";

    /// <summary>BM25 column weights (title, body, tags): a hit in the title outranks one in the body.</summary>
    public const string Bm25Weights = "10.0, 1.0, 5.0";

    public const string Drop = """
        DROP TRIGGER IF EXISTS notes_after_insert;
        DROP TRIGGER IF EXISTS notes_after_delete;
        DROP TRIGGER IF EXISTS notes_after_update;
        DROP TABLE IF EXISTS notes_fts;
        DROP TABLE IF EXISTS notes_trigram;
        DROP TABLE IF EXISTS links;
        DROP TABLE IF EXISTS tags;
        DROP TABLE IF EXISTS notes;
        """;

    public const string Create = """
        CREATE TABLE notes (
            rowid        INTEGER PRIMARY KEY,
            id           TEXT NOT NULL UNIQUE,
            path         TEXT NOT NULL UNIQUE,
            title        TEXT NOT NULL,
            project      TEXT,
            type         TEXT NOT NULL,
            created      TEXT,
            updated      TEXT,
            content_hash TEXT NOT NULL,
            tags         TEXT NOT NULL,
            title_sort   TEXT NOT NULL,
            sort_date    TEXT NOT NULL,
            file_size    INTEGER NOT NULL,
            file_mtime   INTEGER NOT NULL,
            -- Last on purpose: SQLite stores columns in declaration order and a long text spills into
            -- overflow pages, which would have to be followed to reach any column declared after it.
            body         TEXT NOT NULL
        ) STRICT;

        CREATE INDEX ix_notes_recent ON notes (sort_date DESC, file_mtime DESC, path);
        CREATE INDEX ix_notes_title ON notes (title_sort, path);
        CREATE INDEX ix_notes_project ON notes (project) WHERE project IS NOT NULL;
        CREATE INDEX ix_notes_type ON notes (type);

        CREATE TABLE tags (
            note_id TEXT NOT NULL REFERENCES notes (id) ON DELETE CASCADE ON UPDATE CASCADE,
            tag     TEXT NOT NULL,
            PRIMARY KEY (note_id, tag)
        ) STRICT, WITHOUT ROWID;

        CREATE INDEX ix_tags_tag ON tags (tag);

        CREATE TABLE links (
            note_id TEXT NOT NULL REFERENCES notes (id) ON DELETE CASCADE ON UPDATE CASCADE,
            kind    TEXT NOT NULL CHECK (kind IN ('commit', 'ticket', 'note')),
            target  TEXT NOT NULL,
            PRIMARY KEY (note_id, kind, target)
        ) STRICT, WITHOUT ROWID;

        CREATE INDEX ix_links_target ON links (kind, target);

        -- External-content FTS tables: the text lives only in `notes`; triggers keep both indexes in sync.
        CREATE VIRTUAL TABLE notes_fts USING fts5 (
            title, body, tags,
            content = 'notes', content_rowid = 'rowid',
            tokenize = "unicode61 remove_diacritics 2",
            prefix = '2 3'
        );

        CREATE VIRTUAL TABLE notes_trigram USING fts5 (
            title, body, tags,
            content = 'notes', content_rowid = 'rowid',
            tokenize = "trigram remove_diacritics 1"
        );

        CREATE TRIGGER notes_after_insert AFTER INSERT ON notes BEGIN
            INSERT INTO notes_fts (rowid, title, body, tags) VALUES (new.rowid, new.title, new.body, new.tags);
            INSERT INTO notes_trigram (rowid, title, body, tags) VALUES (new.rowid, new.title, new.body, new.tags);
        END;

        CREATE TRIGGER notes_after_delete AFTER DELETE ON notes BEGIN
            INSERT INTO notes_fts (notes_fts, rowid, title, body, tags) VALUES ('delete', old.rowid, old.title, old.body, old.tags);
            INSERT INTO notes_trigram (notes_trigram, rowid, title, body, tags) VALUES ('delete', old.rowid, old.title, old.body, old.tags);
        END;

        -- Only text changes touch the full-text indexes; path, id or timestamp updates do not.
        CREATE TRIGGER notes_after_update AFTER UPDATE OF title, body, tags ON notes BEGIN
            INSERT INTO notes_fts (notes_fts, rowid, title, body, tags) VALUES ('delete', old.rowid, old.title, old.body, old.tags);
            INSERT INTO notes_trigram (notes_trigram, rowid, title, body, tags) VALUES ('delete', old.rowid, old.title, old.body, old.tags);
            INSERT INTO notes_fts (rowid, title, body, tags) VALUES (new.rowid, new.title, new.body, new.tags);
            INSERT INTO notes_trigram (rowid, title, body, tags) VALUES (new.rowid, new.title, new.body, new.tags);
        END;
        """;

    private const string SummaryColumns =
        "n.rowid, n.id, n.path, n.title, n.project, n.type, n.tags, n.created, n.updated, n.file_mtime, n.sort_date, n.title_sort";

    public const string ListRecent =
        $"SELECT {SummaryColumns}, substr(n.body, 1, 320) FROM notes AS n ORDER BY n.sort_date DESC, n.file_mtime DESC, n.path LIMIT $limit";

    public const string ListByTitle =
        $"SELECT {SummaryColumns}, substr(n.body, 1, 320) FROM notes AS n ORDER BY n.title_sort, n.path LIMIT $limit";

    public static readonly string WordSearchByRelevance = SearchByRelevance(WordIndex);
    public static readonly string WordSearchByRecent = SearchBySortIndex(WordIndex, RecentOrder);
    public static readonly string WordSearchByTitle = SearchBySortIndex(WordIndex, TitleOrder);
    public static readonly string TrigramSearchByRelevance = SearchByRelevance(TrigramIndex);
    public static readonly string TrigramSearchByRecent = SearchBySortIndex(TrigramIndex, RecentOrder);
    public static readonly string TrigramSearchByTitle = SearchBySortIndex(TrigramIndex, TitleOrder);

    // The same orders as the plain listings, so both can be answered from ix_notes_recent / ix_notes_title.
    // {0} is the alias of the notes table.
    private const string RecentOrder = "{0}.sort_date DESC, {0}.file_mtime DESC, {0}.path";
    private const string TitleOrder = "{0}.title_sort, {0}.path";

    // How a search is shaped (measured with benchmarks/DevNotes.Benchmarks on 5,000 notes):
    //
    // 1. The innermost query picks WHICH notes are returned without touching the wide `notes` rows.
    //    SQLite evaluates result columns before ORDER BY … LIMIT, so a single flat query would run
    //    snippet() and highlight() - which read and tokenize the whole text - and join `notes` for
    //    every match. The first letters the user types match almost every note, so that is
    //    thousands of rows read to keep thirty.
    // 2. The outer query builds the highlighted fragments for the chosen notes only.
    //
    // The unary plus in `+x.rowid IN (…)` is deliberate: it stops the planner from using the list as
    // a rowid lookup (which would restart the full-text query once per row) and keeps it as a cheap
    // membership filter on a single scan.

    /// <summary>Best matches first; among equally relevant notes, the most recent first.</summary>
    private static string SearchByRelevance(string index)
    {
        var score = $"bm25({index}, {Bm25Weights})";
        return $"""
            {SelectHits(index)}
              AND +{index}.rowid IN (
                  SELECT rowid
                  FROM {index}
                  WHERE {index} MATCH $match
                  ORDER BY {score}, rowid
                  LIMIT $limit)
            ORDER BY score, n.sort_date DESC, n.rowid
            LIMIT $limit
            """;
    }

    /// <summary>
    /// Matches in the order of one of the sort indexes: the index is walked in order and each entry is
    /// checked against the set of matching rows, so the scan stops as soon as the limit is reached.
    /// </summary>
    private static string SearchBySortIndex(string index, string order)
    {
        var innerOrder = string.Format(CultureInfo.InvariantCulture, order, "m");
        var outerOrder = string.Format(CultureInfo.InvariantCulture, order, "n");
        return $"""
            {SelectHits(index)}
              AND +{index}.rowid IN (
                  SELECT m.rowid
                  FROM notes AS m
                  WHERE +m.rowid IN (SELECT rowid FROM {index} WHERE {index} MATCH $match)
                  ORDER BY {innerOrder}
                  LIMIT $limit)
            ORDER BY {outerOrder}
            LIMIT $limit
            """;
    }

    private static string SelectHits(string index) => $"""
        SELECT {SummaryColumns},
               snippet({index}, 1, $start, $end, $ellipsis, 18),
               highlight({index}, 0, $start, $end),
               bm25({index}, {Bm25Weights}) AS score
        FROM {index}
        JOIN notes AS n ON n.rowid = {index}.rowid
        WHERE {index} MATCH $match
        """;
}
