using DevNotes.Application.Abstractions;
using Microsoft.Extensions.Logging;

namespace DevNotes.Infrastructure.Storage;

/// <summary>
/// Reports external changes inside a vault folder using <see cref="FileSystemWatcher"/>.
/// Events are raised on thread-pool threads and are not debounced here; buffer overflows and
/// watcher failures are surfaced as <see cref="VaultFileEventKind.Overflow"/> so that the
/// consumer falls back to a full scan instead of silently missing changes.
/// </summary>
public sealed partial class FileSystemVaultWatcher : IVaultWatcher
{
    private const int BufferSizeBytes = 64 * 1024;

    private readonly string _root;
    private readonly FileSystemWatcher _watcher;
    private readonly ILogger _logger;
    private int _disposed;

    public FileSystemVaultWatcher(string rootPath, ILogger<FileSystemVaultWatcher> logger)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rootPath);
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(rootPath));

        _watcher = new FileSystemWatcher(_root)
        {
            IncludeSubdirectories = true,
            InternalBufferSize = BufferSizeBytes,
            NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.LastWrite | NotifyFilters.Size,
        };
        _watcher.Created += OnCreated;
        _watcher.Changed += OnChanged;
        _watcher.Deleted += OnDeleted;
        _watcher.Renamed += OnRenamed;
        _watcher.Error += OnError;
    }

    public event EventHandler<VaultFileEvent>? Changed;

    public void Start()
    {
        ObjectDisposedException.ThrowIf(_disposed != 0, this);
        _watcher.EnableRaisingEvents = true;
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        _watcher.EnableRaisingEvents = false;
        _watcher.Created -= OnCreated;
        _watcher.Changed -= OnChanged;
        _watcher.Deleted -= OnDeleted;
        _watcher.Renamed -= OnRenamed;
        _watcher.Error -= OnError;
        _watcher.Dispose();
    }

    private void OnCreated(object sender, FileSystemEventArgs e) => Raise(VaultFileEventKind.Created, e.FullPath);

    private void OnChanged(object sender, FileSystemEventArgs e) => Raise(VaultFileEventKind.Changed, e.FullPath);

    private void OnDeleted(object sender, FileSystemEventArgs e) => Raise(VaultFileEventKind.Deleted, e.FullPath);

    private void OnRenamed(object sender, RenamedEventArgs e)
    {
        var oldPath = ToRelative(e.OldFullPath);
        var newPath = ToRelative(e.FullPath);
        var oldHidden = oldPath is null || IsHidden(oldPath);
        var newHidden = newPath is null || IsHidden(newPath);

        if (oldHidden && newHidden)
        {
            return;
        }

        if (newHidden)
        {
            // Renamed into a hidden name: for the vault the file is gone.
            Publish(new VaultFileEvent(VaultFileEventKind.Deleted, oldPath!));
        }
        else if (oldHidden)
        {
            // Typical atomic save of other editors: write a temporary file, then rename it over the note.
            Publish(new VaultFileEvent(VaultFileEventKind.Created, newPath!));
        }
        else
        {
            Publish(new VaultFileEvent(VaultFileEventKind.Renamed, newPath!, oldPath));
        }
    }

    private void OnError(object sender, ErrorEventArgs e)
    {
        LogWatcherError(e.GetException(), _root);
        Publish(new VaultFileEvent(VaultFileEventKind.Overflow, string.Empty));
    }

    private void Raise(VaultFileEventKind kind, string fullPath)
    {
        if (ToRelative(fullPath) is { } relative && !IsHidden(relative))
        {
            Publish(new VaultFileEvent(kind, relative));
        }
    }

    private void Publish(VaultFileEvent vaultEvent)
    {
        if (_disposed == 0)
        {
            Changed?.Invoke(this, vaultEvent);
        }
    }

    private string? ToRelative(string fullPath)
    {
        if (fullPath.Length <= _root.Length + 1 || !fullPath.StartsWith(_root, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        return fullPath[(_root.Length + 1)..].Replace('\\', '/');
    }

    /// <summary>Hidden files and tool folders (.git, .obsidian, .devnotes, temporary files) are filtered at the source.</summary>
    private static bool IsHidden(string relativePath) =>
        relativePath[0] == '.' || relativePath.Contains("/.", StringComparison.Ordinal);

    [LoggerMessage(EventId = 400, Level = LogLevel.Warning, Message = "File watcher for '{Root}' reported an error; a full scan will be requested")]
    private partial void LogWatcherError(Exception exception, string root);
}

public sealed class FileSystemVaultWatcherFactory(ILoggerFactory loggerFactory) : IVaultWatcherFactory
{
    public IVaultWatcher Create(string vaultRootPath) =>
        new FileSystemVaultWatcher(vaultRootPath, loggerFactory.CreateLogger<FileSystemVaultWatcher>());
}
