using System.IO.Enumeration;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using DevNotes.Application.Abstractions;
using DevNotes.Domain.Notes;

namespace DevNotes.Infrastructure.Storage;

/// <summary>
/// File-system implementation of a vault. Every path is resolved against the vault root and
/// verified to stay inside it; symbolic links and junctions are never followed.
/// </summary>
public sealed class VaultFileStore : INoteFileStore
{
    /// <summary>Folder inside the vault owned by the app (trash). It ignores itself for Git.</summary>
    public const string InternalFolderName = ".devnotes";

    /// <summary>Files above this size are not treated as notes (protects memory while indexing).</summary>
    public const long MaxNoteSizeBytes = 16 * 1024 * 1024;

    private const string TrashFolderName = "trash";
    private const string TrashedNoteFileName = "note.md";
    private const string TrashMetadataFileName = "entry.json";
    private const int MaxRestoreAttempts = 100;

    private static readonly UTF8Encoding _utf8NoBom = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: false);

    private static readonly StringComparison _pathComparison =
        OperatingSystem.IsLinux() ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;

    private static readonly EnumerationOptions _recursive = new()
    {
        RecurseSubdirectories = true,
        IgnoreInaccessible = true,
        AttributesToSkip = FileAttributes.System | FileAttributes.Device,
        ReturnSpecialDirectories = false,
    };

    private readonly string _root;
    private readonly string _rootPrefix;
    private readonly string _trashRoot;
    private readonly TimeProvider _timeProvider;

    public VaultFileStore(string rootPath, TimeProvider timeProvider)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rootPath);
        if (!Path.IsPathFullyQualified(rootPath))
        {
            throw new ArgumentException("The vault root must be an absolute path.", nameof(rootPath));
        }

        _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
        _root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(rootPath));
        _rootPrefix = _root + Path.DirectorySeparatorChar;
        _trashRoot = Path.Combine(_root, InternalFolderName, TrashFolderName);
    }

    public string RootPath => _root;

    public Task<IReadOnlyList<NoteFileInfo>> ListNotesAsync(CancellationToken cancellationToken) =>
        Task.Run<IReadOnlyList<NoteFileInfo>>(
            () =>
            {
                var notes = new List<NoteFileInfo>();
                if (!Directory.Exists(_root))
                {
                    return notes;
                }

                var enumeration = new FileSystemEnumerable<NoteFileInfo?>(_root, ToNoteFileInfo, _recursive)
                {
                    ShouldIncludePredicate = IsCandidateNote,
                    ShouldRecursePredicate = ShouldEnter,
                };

                foreach (var note in enumeration)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (note is { } info)
                    {
                        notes.Add(info);
                    }
                }

                notes.Sort(static (left, right) => left.Path.CompareTo(right.Path));
                return notes;
            },
            cancellationToken);

    public Task<IReadOnlyList<string>> ListFoldersAsync(CancellationToken cancellationToken) =>
        Task.Run<IReadOnlyList<string>>(
            () =>
            {
                var folders = new List<string>();
                if (!Directory.Exists(_root))
                {
                    return folders;
                }

                var enumeration = new FileSystemEnumerable<string>(
                    _root,
                    (ref FileSystemEntry entry) => ToRelative(entry.ToFullPath()),
                    _recursive)
                {
                    ShouldIncludePredicate = ShouldEnter,
                    ShouldRecursePredicate = ShouldEnter,
                };

                foreach (var folder in enumeration)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    folders.Add(folder);
                }

                folders.Sort(StringComparer.Ordinal);
                return folders;
            },
            cancellationToken);

    public Task<NoteFileInfo?> GetInfoAsync(NotePath path, CancellationToken cancellationToken) =>
        Task.Run<NoteFileInfo?>(
            () =>
            {
                var file = new FileInfo(Resolve(path));
                return file.Exists && !IsLink(file) ? new NoteFileInfo(path, file.Length, file.LastWriteTimeUtc) : null;
            },
            cancellationToken);

    public Task<NoteFile?> ReadAsync(NotePath path, CancellationToken cancellationToken) =>
        Task.Run(
            () =>
            {
                var fullPath = Resolve(path);

                // A linked file is not a note of this vault (same rule as the listing): its target may be anywhere.
                return IsLink(new FileInfo(fullPath))
                    ? Task.FromResult<NoteFile?>(null)
                    : ReadCoreAsync(path, fullPath, cancellationToken);
            },
            cancellationToken);

    public Task<NoteFile> WriteAsync(NotePath path, string text, NoteWriteMode mode, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(text);
        return Task.Run(
            async () =>
            {
                var fullPath = Resolve(path);
                EnsureNotLinkedFile(fullPath, path);
                Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);

                var bytes = _utf8NoBom.GetBytes(text);
                try
                {
                    await AtomicFile.WriteAsync(fullPath, bytes, overwrite: mode == NoteWriteMode.Overwrite, cancellationToken).ConfigureAwait(false);
                }
                catch (IOException) when (mode == NoteWriteMode.CreateNew && File.Exists(fullPath))
                {
                    throw new NoteAlreadyExistsException(path);
                }

                var file = new FileInfo(fullPath);
                return new NoteFile(new NoteFileInfo(path, file.Length, file.LastWriteTimeUtc), text, ContentHash.Compute(bytes));
            },
            cancellationToken);
    }

    public Task MoveAsync(NotePath source, NotePath destination, CancellationToken cancellationToken) =>
        Task.Run(
            () =>
            {
                var sourcePath = Resolve(source);
                var destinationPath = Resolve(destination);
                if (!File.Exists(sourcePath))
                {
                    throw new NoteNotFoundException(source);
                }

                EnsureNotLinkedFile(sourcePath, source);

                Directory.CreateDirectory(Path.GetDirectoryName(destinationPath)!);
                MoveWithoutOverwrite(sourcePath, destinationPath, destination);
            },
            cancellationToken);

    public Task<TrashEntry> MoveToTrashAsync(NotePath path, CancellationToken cancellationToken) =>
        Task.Run(
            async () =>
            {
                var sourcePath = Resolve(path);
                if (!File.Exists(sourcePath))
                {
                    throw new NoteNotFoundException(path);
                }

                var now = _timeProvider.GetUtcNow();
                var entry = new TrashEntry(NoteId.NewId(now).Value, path, now);
                var entryDirectory = Path.Combine(_trashRoot, entry.Id);

                EnsureInternalFolder();
                Directory.CreateDirectory(entryDirectory);
                try
                {
                    // Metadata first: a trashed note without metadata could never be restored to its place.
                    var metadata = JsonSerializer.SerializeToUtf8Bytes(
                        new TrashMetadata(path.Value, now),
                        TrashJsonContext.Default.TrashMetadata);
                    await AtomicFile.WriteAsync(Path.Combine(entryDirectory, TrashMetadataFileName), metadata, overwrite: false, cancellationToken)
                        .ConfigureAwait(false);
                    File.Move(sourcePath, Path.Combine(entryDirectory, TrashedNoteFileName), overwrite: false);
                }
                catch
                {
                    TryDeleteDirectory(entryDirectory);
                    throw;
                }

                return entry;
            },
            cancellationToken);

    public Task<IReadOnlyList<TrashEntry>> ListTrashAsync(CancellationToken cancellationToken) =>
        Task.Run<IReadOnlyList<TrashEntry>>(
            async () =>
            {
                var entries = new List<TrashEntry>();
                if (!Directory.Exists(_trashRoot))
                {
                    return entries;
                }

                foreach (var directory in Directory.EnumerateDirectories(_trashRoot))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (await TryReadTrashEntryAsync(directory, cancellationToken).ConfigureAwait(false) is { } entry)
                    {
                        entries.Add(entry);
                    }
                }

                entries.Sort(static (left, right) => right.DeletedAtUtc.CompareTo(left.DeletedAtUtc));
                return entries;
            },
            cancellationToken);

    public Task<NotePath> RestoreFromTrashAsync(string trashId, CancellationToken cancellationToken) =>
        Task.Run(
            async () =>
            {
                var entryDirectory = ResolveTrashEntry(trashId);
                var entry = await TryReadTrashEntryAsync(entryDirectory, cancellationToken).ConfigureAwait(false)
                    ?? throw new TrashEntryNotFoundException(trashId);

                var trashedFile = Path.Combine(entryDirectory, TrashedNoteFileName);
                var original = entry.OriginalPath;
                for (var attempt = 1; attempt <= MaxRestoreAttempts; attempt++)
                {
                    // The original name may have been reused since the note was deleted.
                    var candidate = attempt == 1
                        ? original
                        : original.WithFileName($"{original.FileNameWithoutExtension}-restored{(attempt == 2 ? string.Empty : $"-{attempt - 1}")}");
                    var candidatePath = Resolve(candidate);
                    Directory.CreateDirectory(Path.GetDirectoryName(candidatePath)!);
                    try
                    {
                        File.Move(trashedFile, candidatePath, overwrite: false);
                        TryDeleteDirectory(entryDirectory);
                        return candidate;
                    }
                    catch (IOException) when (File.Exists(candidatePath))
                    {
                        // Name taken: try the next one.
                    }
                }

                throw new IOException($"Could not find a free name to restore '{original}'.");
            },
            cancellationToken);

    public Task DeleteFromTrashAsync(string trashId, CancellationToken cancellationToken) =>
        Task.Run(
            () =>
            {
                var entryDirectory = ResolveTrashEntry(trashId);
                if (!Directory.Exists(entryDirectory))
                {
                    throw new TrashEntryNotFoundException(trashId);
                }

                Directory.Delete(entryDirectory, recursive: true);
            },
            cancellationToken);

    public Task EmptyTrashAsync(CancellationToken cancellationToken) =>
        Task.Run(
            () =>
            {
                if (!Directory.Exists(_trashRoot))
                {
                    return;
                }

                foreach (var directory in Directory.EnumerateDirectories(_trashRoot))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    Directory.Delete(directory, recursive: true);
                }
            },
            cancellationToken);

    /// <summary>Decodes note bytes: UTF-8 by default, UTF-16 when the file starts with its byte-order mark.</summary>
    internal static string Decode(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length >= 2 && bytes[0] == 0xFF && bytes[1] == 0xFE)
        {
            return Encoding.Unicode.GetString(bytes[2..]);
        }

        if (bytes.Length >= 2 && bytes[0] == 0xFE && bytes[1] == 0xFF)
        {
            return Encoding.BigEndianUnicode.GetString(bytes[2..]);
        }

        if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF)
        {
            bytes = bytes[3..];
        }

        return _utf8NoBom.GetString(bytes);
    }

    private static async Task<NoteFile?> ReadCoreAsync(NotePath path, string fullPath, CancellationToken cancellationToken)
    {
        try
        {
            var stream = new FileStream(
                fullPath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete, // Never block another editor that is saving the note.
                bufferSize: 1,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            await using (stream.ConfigureAwait(false))
            {
                var length = stream.Length;
                if (length > MaxNoteSizeBytes)
                {
                    throw new InvalidDataException($"'{path}' is {length:N0} bytes; notes larger than {MaxNoteSizeBytes:N0} bytes are not supported.");
                }

                var buffer = new byte[length];
                await stream.ReadExactlyAsync(buffer, cancellationToken).ConfigureAwait(false);

                // Metadata comes from the same handle as the bytes, so it describes exactly what was read.
                var info = new NoteFileInfo(path, length, File.GetLastWriteTimeUtc(stream.SafeFileHandle));
                return new NoteFile(info, Decode(buffer), ContentHash.Compute(buffer));
            }
        }
        catch (Exception exception) when (exception is FileNotFoundException or DirectoryNotFoundException)
        {
            return null;
        }
        catch (EndOfStreamException)
        {
            // Truncated by another writer while reading: report it as a transient I/O problem.
            throw new IOException($"'{path}' changed while it was being read.");
        }
    }

    private void MoveWithoutOverwrite(string sourcePath, string destinationPath, NotePath destination)
    {
        var sameFileDifferentCase =
            !string.Equals(sourcePath, destinationPath, StringComparison.Ordinal)
            && string.Equals(sourcePath, destinationPath, StringComparison.OrdinalIgnoreCase)
            && !ExistsWithExactName(destinationPath);

        try
        {
            File.Move(sourcePath, destinationPath, overwrite: false);
        }
        catch (IOException) when (!sameFileDifferentCase && File.Exists(destinationPath))
        {
            throw new NoteAlreadyExistsException(destination);
        }
    }

    /// <summary>True when the directory really contains an entry with this exact (case-sensitive) name.</summary>
    private static bool ExistsWithExactName(string fullPath)
    {
        var directory = Path.GetDirectoryName(fullPath);
        var name = Path.GetFileName(fullPath);
        return directory is not null
            && Directory.Exists(directory)
            && Directory.EnumerateFiles(directory, name).Any(file => string.Equals(Path.GetFileName(file), name, StringComparison.Ordinal));
    }

    private string Resolve(NotePath path)
    {
        if (path.IsEmpty)
        {
            throw new ArgumentException("The note path is empty.", nameof(path));
        }

        if (path.IsHidden)
        {
            throw new ArgumentException($"'{path}' is inside a hidden folder or is a hidden file; notes cannot live there.", nameof(path));
        }

        var fullPath = Path.GetFullPath(Path.Combine(_root, path.Value.Replace('/', Path.DirectorySeparatorChar)));

        // Defence in depth: NotePath already forbids rooted paths and '..' segments.
        if (!fullPath.StartsWith(_rootPrefix, _pathComparison))
        {
            throw new UnauthorizedAccessException($"'{path}' resolves outside the vault.");
        }

        EnsureNoLinkInPath(fullPath, path);
        return fullPath;
    }

    /// <summary>Rejects paths that go through a symbolic link or junction, which could lead outside the vault.</summary>
    private void EnsureNoLinkInPath(string fullPath, NotePath path)
    {
        var current = Path.GetDirectoryName(fullPath);
        while (current is not null && current.Length > _root.Length)
        {
            var directory = new DirectoryInfo(current);
            if (directory.Exists && IsLink(directory))
            {
                throw new UnauthorizedAccessException($"'{path}' goes through a linked folder; links are not followed.");
            }

            current = Path.GetDirectoryName(current);
        }
    }

    private static void EnsureNotLinkedFile(string fullPath, NotePath path)
    {
        if (IsLink(new FileInfo(fullPath)))
        {
            throw new UnauthorizedAccessException($"'{path}' is a link; links are not followed.");
        }
    }

    private string ResolveTrashEntry(string trashId)
    {
        // Trash ids are generated by the app; anything else is rejected before it touches the file system.
        if (!NoteId.TryParse(trashId, out var id) || id.Value != trashId)
        {
            throw new TrashEntryNotFoundException(trashId ?? string.Empty);
        }

        return Path.Combine(_trashRoot, id.Value);
    }

    private void EnsureInternalFolder()
    {
        Directory.CreateDirectory(_trashRoot);
        var gitignore = Path.Combine(_root, InternalFolderName, ".gitignore");
        if (!File.Exists(gitignore))
        {
            // Keeps the trash out of the user's Git history when the vault is a repository.
            File.WriteAllText(gitignore, "*\n", _utf8NoBom);
        }
    }

    private async Task<TrashEntry?> TryReadTrashEntryAsync(string entryDirectory, CancellationToken cancellationToken)
    {
        var id = Path.GetFileName(entryDirectory);
        var metadataPath = Path.Combine(entryDirectory, TrashMetadataFileName);
        if (!File.Exists(metadataPath) || !File.Exists(Path.Combine(entryDirectory, TrashedNoteFileName)))
        {
            return null;
        }

        try
        {
            var bytes = await File.ReadAllBytesAsync(metadataPath, cancellationToken).ConfigureAwait(false);
            var metadata = JsonSerializer.Deserialize(bytes, TrashJsonContext.Default.TrashMetadata);
            return metadata is not null
                && NotePath.TryCreate(metadata.OriginalPath, out var originalPath)
                && !originalPath.IsHidden
                ? new TrashEntry(id, originalPath, metadata.DeletedAtUtc)
                : null;
        }
        catch (JsonException)
        {
            return null; // A damaged entry is not restorable; "empty trash" still removes it.
        }
    }

    private NoteFileInfo? ToNoteFileInfo(ref FileSystemEntry entry)
    {
        var relative = ToRelative(entry.ToFullPath());
        return NotePath.TryCreate(relative, out var path) && entry.Length <= MaxNoteSizeBytes
            ? new NoteFileInfo(path, entry.Length, entry.LastWriteTimeUtc)
            : null; // Names that are not portable (e.g. "what?.md" on Linux) cannot be addressed as notes.
    }

    private string ToRelative(string fullPath) =>
        fullPath[_rootPrefix.Length..].Replace(Path.DirectorySeparatorChar, '/');

    private static bool IsCandidateNote(ref FileSystemEntry entry) =>
        !entry.IsDirectory
        && entry.FileName.Length > NotePath.Extension.Length
        && entry.FileName[0] != '.'
        && entry.FileName.EndsWith(NotePath.Extension, StringComparison.OrdinalIgnoreCase)
        && !IsLink(ref entry);

    /// <summary>Hidden folders (.git, .obsidian, .devnotes…) and links are never entered.</summary>
    private static bool ShouldEnter(ref FileSystemEntry entry) =>
        entry.IsDirectory && entry.FileName[0] != '.' && !IsLink(ref entry);

    private static bool IsLink(ref FileSystemEntry entry) =>
        (entry.Attributes & FileAttributes.ReparsePoint) != 0 && entry.ToFileSystemInfo().LinkTarget is not null;

    // Cloud-sync placeholders (OneDrive…) are reparse points too but have no link target; only real links are rejected.
    private static bool IsLink(FileSystemInfo info) =>
        info.Exists && (info.Attributes & FileAttributes.ReparsePoint) != 0 && info.LinkTarget is not null;

    private static void TryDeleteDirectory(string directory)
    {
        try
        {
            Directory.Delete(directory, recursive: true);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // Best effort cleanup of an app-owned trash folder; leftovers are removed by "empty trash".
        }
    }
}

internal sealed record TrashMetadata(string OriginalPath, DateTimeOffset DeletedAtUtc);

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(TrashMetadata))]
internal sealed partial class TrashJsonContext : JsonSerializerContext;

public sealed class VaultFileStoreFactory(TimeProvider timeProvider) : INoteFileStoreFactory
{
    public INoteFileStore Create(string vaultRootPath) => new VaultFileStore(vaultRootPath, timeProvider);
}
