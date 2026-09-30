using DevNotes.Application.Abstractions;
using DevNotes.Application.Settings;
using DevNotes.Domain.Notes;
using DevNotes.Domain.Vaults;

namespace DevNotes.Application.Tests.Fakes;

/// <summary>Deterministic ids: 01TEST000…0001, 0002, …</summary>
public sealed class SequentialNoteIdGenerator : INoteIdGenerator
{
    private int _next;

    public NoteId NewId() => NoteId.Parse($"01TEST{Interlocked.Increment(ref _next):D20}");
}

public sealed class ManualVaultWatcher : IVaultWatcher
{
    public event EventHandler<VaultFileEvent>? Changed;

    public bool IsStarted { get; private set; }

    public bool IsDisposed { get; private set; }

    public Exception? FailOnStart { get; set; }

    public int SubscriberCount => Changed?.GetInvocationList().Length ?? 0;

    public void Start()
    {
        if (FailOnStart is { } failure)
        {
            throw failure;
        }

        IsStarted = true;
    }

    public void Raise(VaultFileEventKind kind, string path, string? oldPath = null) =>
        Changed?.Invoke(this, new VaultFileEvent(kind, path, oldPath));

    public void Dispose() => IsDisposed = true;
}

public sealed class InMemorySettingsStore : ISettingsStore
{
    public AppSettings Stored { get; set; } = new();

    public int SaveCount { get; private set; }

    public Exception? FailOnSave { get; set; }

    /// <summary>When set, every save waits for this task first (a slow disk).</summary>
    public Task? HoldSaves { get; set; }

    public Task<AppSettings> LoadAsync(CancellationToken cancellationToken) => Task.FromResult(Stored);

    public async Task SaveAsync(AppSettings settings, CancellationToken cancellationToken)
    {
        if (FailOnSave is { } failure)
        {
            throw failure;
        }

        if (HoldSaves is { } hold)
        {
            await hold.WaitAsync(cancellationToken);
        }

        Stored = settings;
        SaveCount++;
    }
}

public sealed class RecordingIndexFactory : INoteIndexFactory
{
    public List<VaultId> Deleted { get; } = [];

    public Dictionary<VaultId, InMemoryNoteIndex> Opened { get; } = [];

    public INoteIndex Open(VaultId vaultId)
    {
        var index = new InMemoryNoteIndex();
        Opened[vaultId] = index;
        return index;
    }

    public Task DeleteAsync(VaultId vaultId, CancellationToken cancellationToken)
    {
        Deleted.Add(vaultId);
        return Task.CompletedTask;
    }
}

public static class NoteText
{
    /// <summary>Builds the text of a note with the given frontmatter lines and body.</summary>
    public static string WithFrontmatter(string frontmatter, string body = "Body text.\n") =>
        $"---\n{frontmatter.Trim('\n')}\n---\n\n{body}";

    public static string Simple(string id, string title, string body = "Body text.\n") =>
        WithFrontmatter($"id: {id}\ntitle: {title}", body);
}
