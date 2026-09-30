using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Text;
using DevNotes.Application.Abstractions;
using DevNotes.Application.Search;
using DevNotes.Domain.Notes;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;

namespace DevNotes.Infrastructure.Indexing;

/// <summary>
/// SQLite + FTS5 implementation of the note index.
/// <para>
/// SQLite has no asynchronous I/O (the async ADO.NET methods of Microsoft.Data.Sqlite run
/// synchronously), so every operation uses the synchronous API on a thread-pool thread. Each
/// operation opens its own pooled connection; WAL lets readers run while a writer is active and
/// writers are serialized in-process.
/// </para>
/// </summary>
public sealed partial class SqliteNoteIndex : INoteIndex
{
    private const string DateFormat = "yyyy-MM-dd";
    private const int SqliteGenericError = 1;
    private const int SqliteCorrupt = 11;
    private const int SqliteNotADatabase = 26;

    private readonly string _connectionString;
    private readonly string? _databasePath;
    private readonly ILogger _logger;
    [SuppressMessage(
        "Usage",
        "CA2213:Disposable fields should be disposed",
        Justification = "Writers queued behind a disposal must still be able to take and release it; no wait handle is ever allocated.")]
    private readonly SemaphoreSlim _writeGate = new(1, 1);
    private SqliteConnection? _keepAlive;
    private int _disposed;

    private SqliteNoteIndex(string connectionString, string? databasePath, ILogger<SqliteNoteIndex> logger)
    {
        _connectionString = connectionString;
        _databasePath = databasePath;
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <summary>Index stored in a database file (created on first use).</summary>
    public static SqliteNoteIndex ForFile(string databasePath, ILogger<SqliteNoteIndex> logger)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(databasePath);
        var fullPath = Path.GetFullPath(databasePath);
        return new SqliteNoteIndex(BuildFileConnectionString(fullPath), fullPath, logger);
    }

    /// <summary>Private in-memory index that lives until the instance is disposed.</summary>
    public static SqliteNoteIndex InMemory(ILogger<SqliteNoteIndex> logger)
    {
        var connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = $"devnotes-{Guid.NewGuid():N}",
            Mode = SqliteOpenMode.Memory,
            Cache = SqliteCacheMode.Shared,
            ForeignKeys = true,
        }.ToString();
        return new SqliteNoteIndex(connectionString, databasePath: null, logger);
    }

    public async Task InitializeAsync(CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed != 0, this);
        await _writeGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed != 0, this);
            await Task.Run(() => EnsureSchema(allowRecreate: true), cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _writeGate.Release();
        }
    }

    public Task<IReadOnlyDictionary<NotePath, IndexedFileState>> GetFileStatesAsync(CancellationToken cancellationToken) =>
        ReadAsync<IReadOnlyDictionary<NotePath, IndexedFileState>>(
            connection =>
            {
                var states = new Dictionary<NotePath, IndexedFileState>();
                using var command = connection.CreateCommand();
                command.CommandText = "SELECT path, id, content_hash, file_size, file_mtime FROM notes";
                using var reader = command.ExecuteReader();
                while (reader.Read())
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (NotePath.TryCreate(reader.GetString(0), out var path) && NoteId.TryParse(reader.GetString(1), out var id))
                    {
                        states[path] = new IndexedFileState(
                            id,
                            ContentHash.FromHex(reader.GetString(2)),
                            reader.GetInt64(3),
                            new DateTimeOffset(reader.GetInt64(4), TimeSpan.Zero));
                    }
                }

                return states;
            },
            cancellationToken);

    public Task UpsertAsync(IReadOnlyCollection<IndexedNote> notes, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(notes);
        return notes.Count == 0
            ? Task.CompletedTask
            : WriteAsync(
                (connection, transaction) =>
                {
                    using var commands = new UpsertCommands(connection, transaction);
                    foreach (var note in notes)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        commands.Upsert(note);
                    }
                },
                cancellationToken);
    }

    public Task TouchAsync(IReadOnlyCollection<NoteFileInfo> files, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(files);
        return files.Count == 0
            ? Task.CompletedTask
            : WriteAsync(
                (connection, transaction) =>
                {
                    using var command = connection.CreateCommand();
                    command.Transaction = transaction;
                    command.CommandText = "UPDATE notes SET file_size = $size, file_mtime = $mtime WHERE path = $path";
                    var size = command.Parameters.Add("$size", SqliteType.Integer);
                    var mtime = command.Parameters.Add("$mtime", SqliteType.Integer);
                    var path = command.Parameters.Add("$path", SqliteType.Text);
                    foreach (var file in files)
                    {
                        size.Value = file.Size;
                        mtime.Value = file.LastWriteTimeUtc.UtcTicks;
                        path.Value = file.Path.Value;
                        command.ExecuteNonQuery();
                    }
                },
                cancellationToken);
    }

    public Task RemoveAsync(IReadOnlyCollection<NotePath> paths, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(paths);
        return paths.Count == 0
            ? Task.CompletedTask
            : WriteAsync(
                (connection, transaction) =>
                {
                    using var command = connection.CreateCommand();
                    command.Transaction = transaction;
                    command.CommandText = "DELETE FROM notes WHERE path = $path";
                    var path = command.Parameters.Add("$path", SqliteType.Text);
                    foreach (var notePath in paths)
                    {
                        path.Value = notePath.Value;
                        command.ExecuteNonQuery();
                    }
                },
                cancellationToken);
    }

    public Task ClearAsync(CancellationToken cancellationToken) =>
        WriteAsync(
            (connection, transaction) =>
            {
                // Dropping and recreating is far cheaper than deleting row by row through the FTS triggers.
                Execute(connection, transaction, IndexSchema.Drop);
                Execute(connection, transaction, IndexSchema.Create);
            },
            cancellationToken);

    public Task<NotePath?> FindPathByIdAsync(NoteId id, CancellationToken cancellationToken) =>
        ReadAsync<NotePath?>(
            connection =>
            {
                using var command = connection.CreateCommand();
                command.CommandText = "SELECT path FROM notes WHERE id = $id";
                command.Parameters.AddWithValue("$id", id.Value);
                return command.ExecuteScalar() is string text && NotePath.TryCreate(text, out var path) ? path : null;
            },
            cancellationToken);

    public Task<int> CountAsync(CancellationToken cancellationToken) =>
        ReadAsync(
            connection =>
            {
                using var command = connection.CreateCommand();
                command.CommandText = "SELECT COUNT(*) FROM notes";
                return Convert.ToInt32(command.ExecuteScalar(), CultureInfo.InvariantCulture);
            },
            cancellationToken);

    public Task<IReadOnlyList<NoteSummary>> ListAsync(NoteListQuery query, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);
        return ReadAsync<IReadOnlyList<NoteSummary>>(
            connection =>
            {
                var notes = new List<NoteSummary>();
                using var command = connection.CreateCommand();
                command.CommandText = query.Sort == NoteSortOrder.TitleAscending ? IndexSchema.ListByTitle : IndexSchema.ListRecent;
                command.Parameters.AddWithValue("$limit", Math.Max(query.Limit, 0));
                using var reader = command.ExecuteReader();
                while (reader.Read())
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (ReadSummary(reader, excerpt: reader.GetString(SummaryColumnCount)) is { } summary)
                    {
                        notes.Add(summary.Note);
                    }
                }

                return notes;
            },
            cancellationToken);
    }

    public Task<IReadOnlyList<SearchHit>> SearchAsync(SearchQuery query, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);
        return ReadAsync<IReadOnlyList<SearchHit>>(
            connection =>
            {
                var rows = new List<SearchRow>();
                if (query.Query.Match is not { } match || query.Limit <= 0)
                {
                    return [];
                }

                var seen = new HashSet<long>();
                RunSearch(connection, GetSearchSql(trigram: false, query.Sort), match, query.Limit, rows, seen, cancellationToken);

                // Substring matches (e.g. "Inventory" inside "UpdateInventoryAsync") complete the word matches.
                if (rows.Count < query.Limit && query.Query.TrigramMatch is { } trigramMatch)
                {
                    RunSearch(connection, GetSearchSql(trigram: true, query.Sort), trigramMatch, query.Limit, rows, seen, cancellationToken);
                }

                IEnumerable<SearchRow> ordered = query.Sort switch
                {
                    NoteSortOrder.UpdatedDescending => rows
                        .OrderByDescending(row => row.SortDate, StringComparer.Ordinal)
                        .ThenByDescending(row => row.FileTicks)
                        .ThenBy(row => row.Hit.Note.Path),
                    NoteSortOrder.TitleAscending => rows
                        .OrderBy(row => row.TitleSort, StringComparer.Ordinal)
                        .ThenBy(row => row.Hit.Note.Path),
                    _ => rows, // Relevance: word matches first (by BM25), then substring matches.
                };

                return [.. ordered.Take(query.Limit).Select(row => row.Hit)];
            },
            cancellationToken);
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        // Wait for a writer in flight so the database is never closed mid-transaction. Writers still
        // queued behind it see the disposed flag as soon as they get the gate and give up.
        await _writeGate.WaitAsync().ConfigureAwait(false);
        try
        {
            _keepAlive?.Dispose();
            _keepAlive = null;
            ReleasePooledConnections();
        }
        finally
        {
            _writeGate.Release();
        }
    }

    /// <summary>Deletes the database file together with its WAL and shared-memory companions.</summary>
    internal static void DeleteDatabaseFiles(string databasePath)
    {
        using (var connection = new SqliteConnection(BuildFileConnectionString(databasePath)))
        {
            SqliteConnection.ClearPool(connection);
        }

        foreach (var suffix in (ReadOnlySpan<string>)[string.Empty, "-wal", "-shm"])
        {
            File.Delete(databasePath + suffix);
        }
    }

    internal static string ToSortKey(string title)
    {
        var decomposed = title.Normalize(NormalizationForm.FormD);
        var builder = new StringBuilder(decomposed.Length);
        foreach (var c in decomposed)
        {
            if (CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.NonSpacingMark)
            {
                builder.Append(char.ToUpperInvariant(c));
            }
        }

        return builder.ToString();
    }

    private const int SummaryColumnCount = 12;

    private static string BuildFileConnectionString(string databasePath) =>
        new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Default,
            ForeignKeys = true,
            Pooling = true,
        }.ToString();

    private static string GetSearchSql(bool trigram, NoteSortOrder sort) => (trigram, sort) switch
    {
        (false, NoteSortOrder.UpdatedDescending) => IndexSchema.WordSearchByRecent,
        (false, NoteSortOrder.TitleAscending) => IndexSchema.WordSearchByTitle,
        (false, _) => IndexSchema.WordSearchByRelevance,
        (true, NoteSortOrder.UpdatedDescending) => IndexSchema.TrigramSearchByRecent,
        (true, NoteSortOrder.TitleAscending) => IndexSchema.TrigramSearchByTitle,
        (true, _) => IndexSchema.TrigramSearchByRelevance,
    };

    private void RunSearch(
        SqliteConnection connection,
        string sql,
        string match,
        int limit,
        List<SearchRow> rows,
        HashSet<long> seen,
        CancellationToken cancellationToken)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.Parameters.AddWithValue("$match", match);
        command.Parameters.AddWithValue("$start", SnippetParser.MatchStart.ToString());
        command.Parameters.AddWithValue("$end", SnippetParser.MatchEnd.ToString());
        command.Parameters.AddWithValue("$ellipsis", SnippetParser.Ellipsis);
        command.Parameters.AddWithValue("$limit", limit);

        try
        {
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!seen.Add(reader.GetInt64(0)))
                {
                    continue;
                }

                var fragment = reader.IsDBNull(SummaryColumnCount) ? string.Empty : reader.GetString(SummaryColumnCount);
                if (ReadSummary(reader, excerpt: string.Empty) is not { } summary)
                {
                    continue;
                }

                var title = reader.IsDBNull(SummaryColumnCount + 1) ? summary.Note.Title : reader.GetString(SummaryColumnCount + 1);
                rows.Add(new SearchRow(
                    new SearchHit(summary.Note, SnippetParser.Parse(title), SnippetParser.Parse(fragment), reader.GetDouble(SummaryColumnCount + 2)),
                    summary.SortDate,
                    summary.FileTicks,
                    summary.TitleSort));
            }
        }
        catch (SqliteException exception) when (IsRejectedSearchExpression(exception))
        {
            // The query builder only emits quoted terms, so this is not expected. If FTS5 still rejects an
            // expression the search yields no hits for that index instead of failing the whole UI.
            LogSearchRejected(exception.SqliteExtendedErrorCode);
        }
    }

    /// <summary>
    /// True only for the generic SQLite error raised by the FTS5 query parser. Any other error with
    /// the same code (a missing table, a mistake in the statement) is a defect and must surface
    /// instead of looking like "no results".
    /// </summary>
    private static bool IsRejectedSearchExpression(SqliteException exception) =>
        exception.SqliteErrorCode == SqliteGenericError && exception.Message.Contains("fts5:", StringComparison.OrdinalIgnoreCase);

    private static SummaryRow? ReadSummary(SqliteDataReader reader, string excerpt)
    {
        if (!NoteId.TryParse(reader.GetString(1), out var id) || !NotePath.TryCreate(reader.GetString(2), out var path))
        {
            return null;
        }

        var tags = reader.GetString(6);
        var fileTicks = reader.GetInt64(9);
        var note = new NoteSummary(
            id,
            path,
            reader.GetString(3),
            reader.IsDBNull(4) ? null : reader.GetString(4),
            NoteTypes.ParseOrDefault(reader.GetString(5)),
            tags.Length == 0 ? [] : tags.Split(' ', StringSplitOptions.RemoveEmptyEntries),
            ReadDate(reader, 7),
            ReadDate(reader, 8),
            new DateTimeOffset(fileTicks, TimeSpan.Zero),
            excerpt);
        return new SummaryRow(note, reader.GetString(10), fileTicks, reader.GetString(11));
    }

    private static DateOnly? ReadDate(SqliteDataReader reader, int ordinal) =>
        !reader.IsDBNull(ordinal)
        && DateOnly.TryParseExact(reader.GetString(ordinal), DateFormat, CultureInfo.InvariantCulture, DateTimeStyles.None, out var date)
            ? date
            : null;

    private Task<T> ReadAsync<T>(Func<SqliteConnection, T> query, CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed != 0, this);
        return Task.Run(
            () =>
            {
                using var connection = Open();
                return query(connection);
            },
            cancellationToken);
    }

    private async Task WriteAsync(Action<SqliteConnection, SqliteTransaction> work, CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed != 0, this);
        await _writeGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            // The index may have been closed while this writer waited for its turn.
            ObjectDisposedException.ThrowIf(_disposed != 0, this);
            await Task.Run(
                () =>
                {
                    using var connection = Open();

                    // IMMEDIATE: take the write lock up front instead of failing on a later lock upgrade.
                    using var transaction = connection.BeginTransaction(deferred: false);
                    work(connection, transaction);
                    transaction.Commit();
                },
                cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _writeGate.Release();
        }
    }

    private SqliteConnection Open()
    {
        var connection = new SqliteConnection(_connectionString);
        try
        {
            connection.Open();

            // NORMAL is safe with WAL (no corruption on crash; at worst the last commits of a disposable index are lost).
            using var pragma = connection.CreateCommand();
            pragma.CommandText = "PRAGMA synchronous = NORMAL;";
            pragma.ExecuteNonQuery();
            return connection;
        }
        catch
        {
            connection.Dispose();
            throw;
        }
    }

    private void EnsureSchema(bool allowRecreate)
    {
        try
        {
            if (_databasePath is null)
            {
                // A shared in-memory database exists only while at least one connection stays open.
                _keepAlive ??= Open();
            }
            else
            {
                Directory.CreateDirectory(Path.GetDirectoryName(_databasePath)!);
            }

            using var connection = Open();
            if (_databasePath is not null)
            {
                using var wal = connection.CreateCommand();
                wal.CommandText = "PRAGMA journal_mode = WAL;";
                wal.ExecuteScalar();
            }

            // The version is read under the write lock: another process opening the same fresh database
            // must not see "no schema yet" as well and wipe what this one is about to create.
            using var transaction = connection.BeginTransaction(deferred: false);
            using var versionCommand = connection.CreateCommand();
            versionCommand.Transaction = transaction;
            versionCommand.CommandText = "PRAGMA user_version;";
            var version = Convert.ToInt32(versionCommand.ExecuteScalar(), CultureInfo.InvariantCulture);
            if (version == IndexSchema.Version)
            {
                return;
            }

            if (version != 0)
            {
                LogSchemaRebuilt(version, IndexSchema.Version);
            }

            Execute(connection, transaction, IndexSchema.Drop);
            Execute(connection, transaction, IndexSchema.Create);
            Execute(connection, transaction, $"PRAGMA user_version = {IndexSchema.Version.ToString(CultureInfo.InvariantCulture)};");
            transaction.Commit();
        }
        catch (SqliteException exception) when (allowRecreate
            && _databasePath is not null
            && exception.SqliteErrorCode is SqliteCorrupt or SqliteNotADatabase)
        {
            // The index is disposable by design: a damaged file is replaced and rebuilt from the notes.
            LogCorruptIndexReplaced(exception, _databasePath);
            DeleteDatabaseFiles(_databasePath);
            EnsureSchema(allowRecreate: false);
        }
    }

    private void ReleasePooledConnections()
    {
        using var connection = new SqliteConnection(_connectionString);
        if (_databasePath is not null)
        {
            try
            {
                connection.Open();
                using var checkpoint = connection.CreateCommand();
                checkpoint.CommandText = "PRAGMA wal_checkpoint(TRUNCATE);";
                checkpoint.ExecuteNonQuery();
                connection.Close();
            }
            catch (SqliteException exception)
            {
                // Tidying the WAL is optional; the data is already durable.
                LogCheckpointFailed(exception);
            }
        }

        // Pooled connections keep the file locked (notably on Windows) until the pool is cleared.
        SqliteConnection.ClearPool(connection);
    }

    private static void Execute(SqliteConnection connection, SqliteTransaction transaction, string sql)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    private readonly record struct SummaryRow(NoteSummary Note, string SortDate, long FileTicks, string TitleSort);

    private readonly record struct SearchRow(SearchHit Hit, string SortDate, long FileTicks, string TitleSort);

    [LoggerMessage(EventId = 500, Level = LogLevel.Warning, Message = "The index at '{Path}' was damaged and has been replaced; it will be rebuilt from the notes")]
    private partial void LogCorruptIndexReplaced(Exception exception, string path);

    [LoggerMessage(EventId = 501, Level = LogLevel.Information, Message = "Index schema version {Found} replaced by version {Expected}; the index will be rebuilt")]
    private partial void LogSchemaRebuilt(int found, int expected);

    // The exception is not logged on purpose: its message quotes the search text, which is user content.
    [LoggerMessage(EventId = 502, Level = LogLevel.Warning, Message = "FTS5 rejected a search expression (SQLite extended code {Code})")]
    private partial void LogSearchRejected(int code);

    [LoggerMessage(EventId = 503, Level = LogLevel.Debug, Message = "WAL checkpoint on close failed")]
    private partial void LogCheckpointFailed(Exception exception);

    /// <summary>Prepared statements reused for every note of a batch.</summary>
    private sealed class UpsertCommands : IDisposable
    {
        private readonly SqliteCommand _findByPath;
        private readonly SqliteCommand _findById;
        private readonly SqliteCommand _deleteRow;
        private readonly SqliteCommand _insert;
        private readonly SqliteCommand _update;
        private readonly SqliteCommand _deleteTags;
        private readonly SqliteCommand _insertTag;
        private readonly SqliteCommand _deleteLinks;
        private readonly SqliteCommand _insertLink;

        public UpsertCommands(SqliteConnection connection, SqliteTransaction transaction)
        {
            _findByPath = Create(connection, transaction, "SELECT rowid FROM notes WHERE path = $path", "$path");
            _findById = Create(connection, transaction, "SELECT rowid FROM notes WHERE id = $id", "$id");
            _deleteRow = Create(connection, transaction, "DELETE FROM notes WHERE rowid = $rowid", "$rowid");
            _insert = Create(
                connection,
                transaction,
                """
                INSERT INTO notes (id, path, title, project, type, created, updated, content_hash, body, tags, title_sort, sort_date, file_size, file_mtime)
                VALUES ($id, $path, $title, $project, $type, $created, $updated, $hash, $body, $tags, $title_sort, $sort_date, $size, $mtime)
                """,
                NoteParameters);
            _update = Create(
                connection,
                transaction,
                """
                UPDATE notes
                SET id = $id, path = $path, title = $title, project = $project, type = $type, created = $created,
                    updated = $updated, content_hash = $hash, body = $body, tags = $tags, title_sort = $title_sort,
                    sort_date = $sort_date, file_size = $size, file_mtime = $mtime
                WHERE rowid = $rowid
                """,
                [.. NoteParameters, "$rowid"]);

            _deleteTags = Create(connection, transaction, "DELETE FROM tags WHERE note_id = $id", "$id");
            _insertTag = Create(connection, transaction, "INSERT OR IGNORE INTO tags (note_id, tag) VALUES ($id, $tag)", "$id", "$tag");
            _deleteLinks = Create(connection, transaction, "DELETE FROM links WHERE note_id = $id", "$id");
            _insertLink = Create(
                connection,
                transaction,
                "INSERT OR IGNORE INTO links (note_id, kind, target) VALUES ($id, $kind, $target)",
                "$id",
                "$kind",
                "$target");
        }

        private static string[] NoteParameters { get; } =
        [
            "$id", "$path", "$title", "$project", "$type", "$created", "$updated", "$hash", "$body", "$tags",
            "$title_sort", "$sort_date", "$size", "$mtime",
        ];

        public void Upsert(IndexedNote note)
        {
            var id = note.Id.Value;
            var rowAtPath = FindRow(_findByPath, "$path", note.Path.Value);
            var rowWithId = FindRow(_findById, "$id", id);

            if (rowAtPath is { } occupant && rowWithId is { } moved && occupant != moved)
            {
                // The note moved onto a path that another indexed note used to occupy.
                _deleteRow.Parameters["$rowid"].Value = occupant;
                _deleteRow.ExecuteNonQuery();
                rowAtPath = null;
            }

            var target = rowWithId ?? rowAtPath;
            var command = target is null ? _insert : _update;
            Bind(command, note);
            if (target is { } rowid)
            {
                command.Parameters["$rowid"].Value = rowid;
            }

            command.ExecuteNonQuery();

            _deleteTags.Parameters["$id"].Value = id;
            _deleteTags.ExecuteNonQuery();
            foreach (var tag in note.Metadata.Tags)
            {
                _insertTag.Parameters["$id"].Value = id;
                _insertTag.Parameters["$tag"].Value = tag.Value;
                _insertTag.ExecuteNonQuery();
            }

            _deleteLinks.Parameters["$id"].Value = id;
            _deleteLinks.ExecuteNonQuery();
            foreach (var (kind, linkTarget) in note.Metadata.Links.Enumerate())
            {
                _insertLink.Parameters["$id"].Value = id;
                _insertLink.Parameters["$kind"].Value = kind.ToKey();
                _insertLink.Parameters["$target"].Value = linkTarget;
                _insertLink.ExecuteNonQuery();
            }
        }

        public void Dispose()
        {
            _findByPath.Dispose();
            _findById.Dispose();
            _deleteRow.Dispose();
            _insert.Dispose();
            _update.Dispose();
            _deleteTags.Dispose();
            _insertTag.Dispose();
            _deleteLinks.Dispose();
            _insertLink.Dispose();
        }

        private static void Bind(SqliteCommand command, IndexedNote note)
        {
            var metadata = note.Metadata;
            var parameters = command.Parameters;
            parameters["$id"].Value = note.Id.Value;
            parameters["$path"].Value = note.Path.Value;
            parameters["$title"].Value = Sanitize(metadata.Title);
            parameters["$project"].Value = (object?)metadata.Project ?? DBNull.Value;
            parameters["$type"].Value = metadata.Type.ToKey();
            parameters["$created"].Value = FormatDate(metadata.Created);
            parameters["$updated"].Value = FormatDate(metadata.Updated);
            parameters["$hash"].Value = note.Hash.Hex;
            parameters["$body"].Value = Sanitize(note.Body);
            parameters["$tags"].Value = Sanitize(string.Join(' ', metadata.Tags.Select(tag => tag.Value)));
            parameters["$title_sort"].Value = ToSortKey(metadata.Title);
            parameters["$sort_date"].Value =
                (metadata.Updated ?? DateOnly.FromDateTime(note.FileLastWriteUtc.UtcDateTime)).ToString(DateFormat, CultureInfo.InvariantCulture);
            parameters["$size"].Value = note.FileSize;
            parameters["$mtime"].Value = note.FileLastWriteUtc.UtcTicks;
        }

        private static object FormatDate(DateOnly? date) =>
            date is { } value ? value.ToString(DateFormat, CultureInfo.InvariantCulture) : DBNull.Value;

        /// <summary>
        /// Removes the characters reserved as highlight delimiters and NUL (which truncates text in
        /// SQLite string functions) so that stored text can never forge or break a search fragment.
        /// </summary>
        private static string Sanitize(string text) =>
            text.AsSpan().ContainsAny(SnippetParser.MatchStart, SnippetParser.MatchEnd, '\0')
                ? string.Create(text.Length, text, static (destination, source) =>
                {
                    for (var i = 0; i < destination.Length; i++)
                    {
                        var c = source[i];
                        destination[i] = c is SnippetParser.MatchStart or SnippetParser.MatchEnd or '\0' ? ' ' : c;
                    }
                })
                : text;

        private static long? FindRow(SqliteCommand command, string parameter, string value)
        {
            command.Parameters[parameter].Value = value;
            return command.ExecuteScalar() is long rowid ? rowid : null;
        }

        private static SqliteCommand Create(SqliteConnection connection, SqliteTransaction transaction, string sql, params string[] parameters)
        {
            var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = sql;
            foreach (var parameter in parameters)
            {
                command.Parameters.Add(new SqliteParameter { ParameterName = parameter });
            }

            return command;
        }
    }
}
