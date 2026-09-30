using System.Collections.Concurrent;
using DevNotes.Application.Abstractions;
using DevNotes.Infrastructure.Storage;
using Microsoft.Extensions.Logging.Abstractions;

namespace DevNotes.Infrastructure.Tests.Storage;

/// <summary>
/// Exercises the real operating-system notifications, so assertions wait (with a generous timeout)
/// for an event instead of assuming ordering or exact event counts, which differ per platform.
/// </summary>
public sealed class FileSystemVaultWatcherTests : IDisposable
{
    private static readonly TimeSpan _timeout = TimeSpan.FromSeconds(15);

    private readonly TempDirectory _vault = new("devnotes-watch-");
    private readonly FileSystemVaultWatcher _watcher;
    private readonly ConcurrentQueue<VaultFileEvent> _events = new();

    public FileSystemVaultWatcherTests()
    {
        _watcher = new FileSystemVaultWatcher(_vault.Path, NullLogger<FileSystemVaultWatcher>.Instance);
        _watcher.Changed += (_, e) => _events.Enqueue(e);
    }

    public void Dispose()
    {
        _watcher.Dispose();
        _vault.Dispose();
    }

    [Fact]
    public async Task CreatedFile_IsReportedWithRelativeForwardSlashPath()
    {
        Directory.CreateDirectory(_vault.Combine("bugs", "2026"));
        _watcher.Start();

        _vault.Write("bugs/2026/deadlock.md", "content");

        var created = await WaitForAsync(e => e.RelativePath == "bugs/2026/deadlock.md");
        created.Kind.Should().BeOneOf(VaultFileEventKind.Created, VaultFileEventKind.Changed);
    }

    [Fact]
    public async Task ModifiedFile_IsReported()
    {
        var path = _vault.Write("note.md", "v1");
        _watcher.Start();

        await File.WriteAllTextAsync(path, "v2 with more content", TestContext.Current.CancellationToken);

        await WaitForAsync(e => e is { Kind: VaultFileEventKind.Changed, RelativePath: "note.md" });
    }

    [Fact]
    public async Task DeletedFile_IsReported()
    {
        var path = _vault.Write("note.md", "v1");
        _watcher.Start();

        File.Delete(path);

        await WaitForAsync(e => e is { Kind: VaultFileEventKind.Deleted, RelativePath: "note.md" });
    }

    [Fact]
    public async Task RenamedFile_IsReportedWithBothPaths()
    {
        var path = _vault.Write("old.md", "v1");
        _watcher.Start();

        File.Move(path, _vault.Combine("new.md"));

        await WaitForAsync(e => e is { Kind: VaultFileEventKind.Renamed, RelativePath: "new.md", OldRelativePath: "old.md" });
    }

    [Fact]
    public async Task AtomicSaveThroughTemporaryFile_IsReportedAsTheNoteAppearing()
    {
        _vault.Write("note.md", "v1");
        _watcher.Start();

        var temporary = _vault.Write(".note.md.tmp", "v2");
        File.Move(temporary, _vault.Combine("note.md"), overwrite: true);

        await WaitForAsync(e => e.RelativePath == "note.md" && e.Kind is VaultFileEventKind.Created or VaultFileEventKind.Changed or VaultFileEventKind.Renamed);
        _events.Should().NotContain(e => e.RelativePath.Contains(".tmp", StringComparison.Ordinal), "hidden temporary files are filtered at the source");
    }

    [Fact]
    public async Task HiddenFoldersAndFiles_AreNeverReported()
    {
        Directory.CreateDirectory(_vault.Combine(".git"));
        _watcher.Start();

        _vault.Write(".git/HEAD.md", "ref");
        _vault.Write(".hidden.md", "x");
        _vault.Write("visible.md", "x"); // sentinel: once it arrives, earlier events would have arrived too

        await WaitForAsync(e => e.RelativePath == "visible.md");
        _events.Should().OnlyContain(e => !e.RelativePath.Contains(".git", StringComparison.Ordinal) && !e.RelativePath.StartsWith('.'));
    }

    [Fact]
    public async Task Dispose_StopsNotifications_AndIsIdempotent()
    {
        _watcher.Start();
        _watcher.Dispose();
        _watcher.Dispose();

        _vault.Write("after-dispose.md", "x");
        await Task.Delay(TimeSpan.FromMilliseconds(500), TestContext.Current.CancellationToken);

        _events.Should().BeEmpty();
        FluentActions.Invoking(_watcher.Start).Should().Throw<ObjectDisposedException>();
    }

    [Fact]
    public async Task ErrorOtherThanOverflow_RequestsAScan_AndKeepsWatching()
    {
        _watcher.Start();

        // What the operating system reports when the folder was briefly unavailable: the underlying
        // watcher stops after it, so without a restart live updates would end for the whole session.
        _watcher.HandleError(new IOException("The network name is no longer available."));

        _events.Should().ContainSingle(e => e.Kind == VaultFileEventKind.Overflow, "changes may have been missed");
        _vault.Write("after-error.md", "x");
        await WaitForAsync(e => e.RelativePath == "after-error.md");
    }

    [Fact]
    public void BufferOverflow_RequestsAScan()
    {
        _watcher.Start();

        _watcher.HandleError(new InternalBufferOverflowException());

        _events.Should().ContainSingle().Which.Kind.Should().Be(VaultFileEventKind.Overflow);
    }

    [Fact]
    public void ErrorAfterDispose_IsIgnored()
    {
        _watcher.Start();
        _watcher.Dispose();

        FluentActions.Invoking(() => _watcher.HandleError(new IOException("late"))).Should().NotThrow();
        _events.Should().BeEmpty();
    }

    [Fact]
    public void ToRelative_UnderstandsVaultsAtTheRootOfADrive()
    {
        var root = Path.GetPathRoot(_vault.Path)!;
        using var atRoot = new FileSystemVaultWatcher(root, NullLogger<FileSystemVaultWatcher>.Instance);

        atRoot.ToRelative(Path.Combine(root, "ab.md")).Should().Be("ab.md");
        atRoot.ToRelative(Path.Combine(root, "bugs", "x.md")).Should().Be("bugs/x.md");
        atRoot.ToRelative(root).Should().BeNull();
        _watcher.ToRelative(Path.Combine(_vault.Path, "bugs", "x.md")).Should().Be("bugs/x.md");
        _watcher.ToRelative(_vault.Path + "-sibling" + Path.DirectorySeparatorChar + "x.md").Should().BeNull("a sibling folder is not inside the vault");
    }

    [Fact]
    public void Constructor_MissingFolder_Throws()
    {
        var act = () => new FileSystemVaultWatcher(_vault.Combine("missing"), NullLogger<FileSystemVaultWatcher>.Instance);

        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Factory_CreatesAWatcher()
    {
        using var watcher = new FileSystemVaultWatcherFactory(NullLoggerFactory.Instance).Create(_vault.Path);

        watcher.Should().BeOfType<FileSystemVaultWatcher>();
    }

    private async Task<VaultFileEvent> WaitForAsync(Func<VaultFileEvent, bool> predicate)
    {
        var deadline = DateTime.UtcNow + _timeout;
        while (DateTime.UtcNow < deadline)
        {
            foreach (var candidate in _events)
            {
                if (predicate(candidate))
                {
                    return candidate;
                }
            }

            await Task.Delay(25, TestContext.Current.CancellationToken);
        }

        throw new Xunit.Sdk.XunitException(
            $"No matching event arrived within {_timeout}. Received: {string.Join(", ", _events.Select(e => $"{e.Kind}:{e.RelativePath}"))}");
    }
}
