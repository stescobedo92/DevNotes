namespace DevNotes.Infrastructure.Indexing;

/// <summary>
/// SQL of the index database. The schema version is stored in <c>PRAGMA user_version</c>; because
/// the index is disposable, a database with any other version is simply dropped and rebuilt.
/// </summary>
internal static class IndexSchema
{
    public const int Version = 1;

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
            body         TEXT NOT NULL,
            tags         TEXT NOT NULL,
            title_sort   TEXT NOT NULL,
            sort_date    TEXT NOT NULL,
            file_size    INTEGER NOT NULL,
            file_mtime   INTEGER NOT NULL
        ) STRICT;

        CREATE INDEX ix_notes_recent ON notes (sort_date DESC, file_mtime DESC);
        CREATE INDEX ix_notes_title ON notes (title_sort);
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

    private const string OrderByRelevance = "ORDER BY score, n.sort_date DESC";
    private const string OrderByRecent = "ORDER BY n.sort_date DESC, n.file_mtime DESC, score";
    private const string OrderByTitle = "ORDER BY n.title_sort, n.path";

    private const string WordSearch = $"""
        SELECT {SummaryColumns},
               snippet({WordIndex}, 1, $start, $end, $ellipsis, 18),
               highlight({WordIndex}, 0, $start, $end),
               bm25({WordIndex}, {Bm25Weights}) AS score
        FROM {WordIndex}
        JOIN notes AS n ON n.rowid = {WordIndex}.rowid
        WHERE {WordIndex} MATCH $match
        """;

    private const string TrigramSearch = $"""
        SELECT {SummaryColumns},
               snippet({TrigramIndex}, 1, $start, $end, $ellipsis, 18),
               highlight({TrigramIndex}, 0, $start, $end),
               bm25({TrigramIndex}, {Bm25Weights}) AS score
        FROM {TrigramIndex}
        JOIN notes AS n ON n.rowid = {TrigramIndex}.rowid
        WHERE {TrigramIndex} MATCH $match
        """;

    public const string WordSearchByRelevance = $"{WordSearch}\n{OrderByRelevance} LIMIT $limit";
    public const string WordSearchByRecent = $"{WordSearch}\n{OrderByRecent} LIMIT $limit";
    public const string WordSearchByTitle = $"{WordSearch}\n{OrderByTitle} LIMIT $limit";
    public const string TrigramSearchByRelevance = $"{TrigramSearch}\n{OrderByRelevance} LIMIT $limit";
    public const string TrigramSearchByRecent = $"{TrigramSearch}\n{OrderByRecent} LIMIT $limit";
    public const string TrigramSearchByTitle = $"{TrigramSearch}\n{OrderByTitle} LIMIT $limit";
}
