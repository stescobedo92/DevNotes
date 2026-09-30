using System.Collections.Concurrent;
using DevNotes.Application.Abstractions;
using DevNotes.Domain.Notes;

namespace DevNotes.Application.Tests.Fakes;

/// <summary>Thread-safe in-memory vault used to test application logic without touching the disk.</summary>
public sealed class InMemoryNoteFileStore : INoteFileStore
{
    private readonly ConcurrentDictionary<NotePath, Entry> _files = new();
    private readonly ConcurrentDictionary<string, (TrashEntry Entry, string Text)> _trash = new();
    private readonly ConcurrentDictionary<NotePath, byte> _unreadable = new();
    private long _clock = new DateTimeOffset(2026, 9, 12, 8, 0, 0, TimeSpan.Zero).UtcTicks;
    private int _trashSequence;

    public string RootPath => Path.Combine(Path.GetTempPath(), "in-memory-vault");

    public int ReadCount => _readCount;

    public int WriteCount => _writeCount;

    private int _readCount;
    private int _writeCount;

    /// <summary>Simulates a change made by another program (new content and modification time).</summary>
    public NoteFileInfo SetExternal(string path, string text, NoteTextEncoding encoding = NoteTextEncoding.Utf8)
    {
        var notePath = NotePath.Create(path);
        var entry = new Entry(text, NextTimestamp(), encoding);
        _files[notePath] = entry;
        return entry.ToInfo(notePath);
    }

    /// <summary>Simulates a "touch": same content, newer modification time.</summary>
    public void Touch(string path)
    {
        var notePath = NotePath.Create(path);
        _files[notePath] = _files[notePath] with { LastWrite = NextTimestamp() };
    }

    public void DeleteExternal(string path) => _files.TryRemove(NotePath.Create(path), out _);

    public void MakeUnreadable(string path) => _unreadable[NotePath.Create(path)] = 0;

    public void MakeReadable(string path) => _unreadable.TryRemove(NotePath.Create(path), out _);

    public string? TextOf(string path) => _files.TryGetValue(NotePath.Create(path), out var entry) ? entry.Text : null;

    public bool Exists(string path) => _files.ContainsKey(NotePath.Create(path));

    public IReadOnlyCollection<NotePath> Paths => [.. _files.Keys];

    public Task<IReadOnlyList<NoteFileInfo>> ListNotesAsync(CancellationToken cancellationToken)
    {
        IReadOnlyList<NoteFileInfo> result = [.. _files
            .Where(pair => !pair.Key.IsHidden)
            .OrderBy(pair => pair.Key)
            .Select(pair => pair.Value.ToInfo(pair.Key))];
        return Task.FromResult(result);
    }

    public Task<IReadOnlyList<string>> ListFoldersAsync(CancellationToken cancellationToken)
    {
        IReadOnlyList<string> folders = [.. _files.Keys
            .Select(path => path.Directory)
            .Where(directory => directory.Length > 0)
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)];
        return Task.FromResult(folders);
    }

    public Task<NoteFileInfo?> GetInfoAsync(NotePath path, CancellationToken cancellationToken) =>
        Task.FromResult<NoteFileInfo?>(_files.TryGetValue(path, out var entry) ? entry.ToInfo(path) : null);

    public Task<NoteFile?> ReadAsync(NotePath path, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Interlocked.Increment(ref _readCount);
        if (_unreadable.ContainsKey(path))
        {
            throw new IOException($"Simulated read failure for '{path}'.");
        }

        return Task.FromResult(_files.TryGetValue(path, out var entry)
            ? new NoteFile(entry.ToInfo(path), entry.Text, ContentHash.Compute(entry.Text), entry.Encoding)
            : null);
    }

    /// <summary>Runs right before a write is applied: lets a test simulate another program saving in that instant.</summary>
    public Action<NotePath>? BeforeWrite { get; set; }

    public NoteWriteOptions LastWriteOptions { get; private set; }

    public Task<NoteFile> WriteAsync(NotePath path, string text, NoteWriteMode mode, NoteWriteOptions options, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        LastWriteOptions = options;
        BeforeWrite?.Invoke(path);
        if (options.ExpectedOnDisk is { } expected
            && (!_files.TryGetValue(path, out var current) || current.ToInfo(path) != expected))
        {
            throw new NoteChangedOnDiskException(path);
        }

        var entry = new Entry(text, NextTimestamp(), options.Encoding);
        if (mode == NoteWriteMode.CreateNew)
        {
            if (!_files.TryAdd(path, entry))
            {
                throw new NoteAlreadyExistsException(path);
            }
        }
        else
        {
            _files[path] = entry;
        }

        Interlocked.Increment(ref _writeCount);
        return Task.FromResult(new NoteFile(entry.ToInfo(path), text, ContentHash.Compute(text), options.Encoding));
    }

    public Task MoveAsync(NotePath source, NotePath destination, CancellationToken cancellationToken)
    {
        if (!_files.TryGetValue(source, out var entry))
        {
            throw new NoteNotFoundException(source);
        }

        if (!_files.TryAdd(destination, entry))
        {
            throw new NoteAlreadyExistsException(destination);
        }

        _files.TryRemove(source, out _);
        return Task.CompletedTask;
    }

    public Task<TrashEntry> MoveToTrashAsync(NotePath path, CancellationToken cancellationToken)
    {
        if (!_files.TryRemove(path, out var entry))
        {
            throw new NoteNotFoundException(path);
        }

        var id = $"trash-{Interlocked.Increment(ref _trashSequence)}";
        var trashEntry = new TrashEntry(id, path, NextTimestamp());
        _trash[id] = (trashEntry, entry.Text);
        return Task.FromResult(trashEntry);
    }

    public Task<IReadOnlyList<TrashEntry>> ListTrashAsync(CancellationToken cancellationToken)
    {
        IReadOnlyList<TrashEntry> entries = [.. _trash.Values.Select(item => item.Entry).OrderByDescending(entry => entry.DeletedAtUtc)];
        return Task.FromResult(entries);
    }

    public Task<NotePath> RestoreFromTrashAsync(string trashId, CancellationToken cancellationToken)
    {
        if (!_trash.TryRemove(trashId, out var item))
        {
            throw new TrashEntryNotFoundException(trashId);
        }

        var path = item.Entry.OriginalPath;
        var suffix = 1;
        while (!_files.TryAdd(path, new Entry(item.Text, NextTimestamp())))
        {
            path = item.Entry.OriginalPath.WithFileName($"{item.Entry.OriginalPath.FileNameWithoutExtension}-restored-{suffix++}");
        }

        return Task.FromResult(path);
    }

    public Task DeleteFromTrashAsync(string trashId, CancellationToken cancellationToken)
    {
        if (!_trash.TryRemove(trashId, out _))
        {
            throw new TrashEntryNotFoundException(trashId);
        }

        return Task.CompletedTask;
    }

    public Task EmptyTrashAsync(CancellationToken cancellationToken)
    {
        _trash.Clear();
        return Task.CompletedTask;
    }

    private DateTimeOffset NextTimestamp() =>
        new(Interlocked.Add(ref _clock, TimeSpan.TicksPerSecond), TimeSpan.Zero);

    private sealed record Entry(string Text, DateTimeOffset LastWrite, NoteTextEncoding Encoding = NoteTextEncoding.Utf8)
    {
        public NoteFileInfo ToInfo(NotePath path) => new(path, System.Text.Encoding.UTF8.GetByteCount(Text), LastWrite);
    }
}
