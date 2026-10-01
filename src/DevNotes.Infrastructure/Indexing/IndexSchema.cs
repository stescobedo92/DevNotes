using System.Globalization;
using System.Text;

namespace DevNotes.Infrastructure.Indexing;

/// <summary>
/// SQL of the index database. The schema version is stored in <c>PRAGMA user_version</c>; because
/// the index is disposable, a database with any other version is simply dropped and rebuilt.
/// </summary>
internal static class IndexSchema
{
    public const int Version = 3;

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
            -- Case- and accent-folded project, so that filters and facets ignore both.
            project_key  TEXT,
            -- Last on purpose: SQLite stores columns in declaration order and a long text spills into
            -- overflow pages, which would have to be followed to reach any column declared after it.
            body         TEXT NOT NULL
        ) STRICT;

        CREATE INDEX ix_notes_recent ON notes (sort_date DESC, file_mtime DESC, path);
        CREATE INDEX ix_notes_title ON notes (title_sort, path);
        -- Covers the project facet (key, display name and count) without touching the wide rows.
        CREATE INDEX ix_notes_project ON notes (project_key, project) WHERE project_key IS NOT NULL;
        CREATE INDEX ix_notes_type ON notes (type);
        CREATE INDEX ix_notes_created ON notes (created) WHERE created IS NOT NULL;
        CREATE INDEX ix_notes_updated ON notes (updated) WHERE updated IS NOT NULL;

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

    /// <summary>Columns read into a <c>NoteSummary</c>, in the order the reader expects; {0} is the alias of the notes table.</summary>
    public const string SummaryColumns =
        "{0}.rowid, {0}.id, {0}.path, {0}.title, {0}.project, {0}.type, {0}.tags, {0}.created, {0}.updated, {0}.file_mtime, {0}.sort_date, {0}.title_sort";

    public const int SummaryColumnCount = 12;

    /// <summary>Characters of the body shown as excerpt in a listing row.</summary>
    public const int ExcerptLength = 320;

    // Static initializers run in textual order: these formats must exist before the statements below use them.
    private static readonly CompositeFormat _summaryColumns = CompositeFormat.Parse(SummaryColumns);
    private static readonly CompositeFormat _recentOrder = CompositeFormat.Parse(RecentOrder);
    private static readonly CompositeFormat _titleOrder = CompositeFormat.Parse(TitleOrder);

    public static readonly string ListRecent =
        $"SELECT {Columns("n")}, substr(n.body, 1, {ExcerptLength}) FROM notes AS n ORDER BY {Order(RecentOrder, "n")} LIMIT $limit";

    public static readonly string ListByTitle =
        $"SELECT {Columns("n")}, substr(n.body, 1, {ExcerptLength}) FROM notes AS n ORDER BY {Order(TitleOrder, "n")} LIMIT $limit";

    public const string ProjectFacets = """
        SELECT min(project), count(*) AS notes
        FROM notes
        WHERE project_key IS NOT NULL
        GROUP BY project_key
        ORDER BY notes DESC, project_key
        """;

    public const string TagFacets = "SELECT tag, count(*) AS notes FROM tags GROUP BY tag ORDER BY notes DESC, tag";

    public const string TypeFacets = "SELECT type, count(*) AS notes FROM notes GROUP BY type ORDER BY notes DESC, type";

    // The same orders as the plain listings, so both can be answered from ix_notes_recent / ix_notes_title.
    // {0} is the alias of the notes table.
    public const string RecentOrder = "{0}.sort_date DESC, {0}.file_mtime DESC, {0}.path";
    public const string TitleOrder = "{0}.title_sort, {0}.path";

    public static string Columns(string alias) => string.Format(CultureInfo.InvariantCulture, _summaryColumns, alias);

    public static string Order(string order, string alias) =>
        string.Format(CultureInfo.InvariantCulture, order == TitleOrder ? _titleOrder : _recentOrder, alias);
}
