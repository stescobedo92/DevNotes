using DevNotes.Application;
using DevNotes.Application.Indexing;
using DevNotes.Application.Notes;
using DevNotes.Application.Search;
using DevNotes.Application.Settings;
using DevNotes.Application.Vaults;
using DevNotes.Domain.Notes;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace DevNotes.Infrastructure.Tests;

/// <summary>
/// End-to-end tests of the real composition: Markdown files on disk, the file watcher, the
/// incremental indexer and the SQLite FTS5 index, wired exactly as the desktop app wires them.
/// </summary>
public sealed class VaultIntegrationTests : IAsyncLifetime
{
    private readonly TempDirectory _vaultFolder = new("devnotes-e2e-vault-");
    private readonly TempDirectory _dataFolder = new("devnotes-e2e-data-");
    private ServiceProvider _services = null!;
    private IVaultSession _session = null!;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync()
    {
        _services = new ServiceCollection()
            .AddLogging(builder => builder.SetMinimumLevel(LogLevel.Debug))
            .AddSingleton(Options.Create(new IndexingOptions { WatcherDebounceMilliseconds = 50 }))
            .AddDevNotesInfrastructure(_dataFolder.Path)
            .AddDevNotesApplication()
            .BuildServiceProvider();

        await _services.GetRequiredService<ISettingsService>().LoadAsync(Ct);
    }

    public async ValueTask DisposeAsync()
    {
        await _services.DisposeAsync();
        _vaultFolder.Dispose();
        _dataFolder.Dispose();
    }

    [Fact]
    public async Task OpeningAVault_IndexesExistingMarkdownWithoutModifyingIt()
    {
        var original = "# Notas de Obsidian\n\nTexto con [[wikilink]] y `código`.\n";
        _vaultFolder.Write("imported/obsidian-note.md", original);
        _vaultFolder.Write("bugs/deadlock.md", SpecNote);
        _vaultFolder.Write(".obsidian/workspace.md", "tool state, must be ignored");

        await OpenVaultAsync();

        (await _session.Queries.CountAsync(Ct)).Should().Be(2);
        File.ReadAllText(_vaultFolder.Combine("imported", "obsidian-note.md")).Should().Be(original, "indexing must never modify the user's files");

        var hits = await QueryAsync("deadlock inventario");
        var hit = hits.Entries.Should().ContainSingle().Subject;
        hit.Note.Path.Value.Should().Be("bugs/deadlock.md");
        hit.Note.Project.Should().Be("azure-microservices");
        hit.Note.Tags.Should().Equal("sql-server", "deadlock", "performance");
        hit.Note.Type.Should().Be(NoteType.Bug);
        hit.Title.Should().Contain(new SnippetSegment("Deadlock", true));
    }

    [Fact]
    public async Task CreateEditRenameDeleteRestore_KeepFilesAndIndexInSync()
    {
        await OpenVaultAsync();
        var notes = _session.Notes;

        var created = await notes.CreateAsync(new NewNoteRequest("Índice FTS5 lento", "adr"), Ct);
        created.Path.Value.Should().Be("adr/indice-fts5-lento.md");
        File.Exists(_vaultFolder.Combine("adr", "indice-fts5-lento.md")).Should().BeTrue();

        var edited = created.Text + "La causa era el tokenizer trigram.\n";
        var saved = (await notes.SaveAsync(created.Path, edited, created.Hash, SaveMode.DetectConflicts, Ct)).Should().BeOfType<SaveResult.Saved>().Subject.Note;
        (await QueryAsync("tokenizer")).Entries.Should().ContainSingle();

        var renamed = await notes.RenameAsync(saved.Path, "Tokenizer trigram en FTS5", Ct);
        renamed.Value.Should().Be("adr/tokenizer-trigram-en-fts5.md");
        (await QueryAsync("tokenizer")).Entries.Should().ContainSingle().Which.Note.Should().Match<NoteSummary>(note =>
            note.Path == renamed && note.Title == "Tokenizer trigram en FTS5" && note.Id == created.Document.Metadata.Id);

        var moved = await notes.MoveAsync(renamed, "archive", Ct);
        (await QueryAsync("tokenizer")).Entries.Should().ContainSingle().Which.Note.Path.Should().Be(moved);

        var trashed = await notes.DeleteAsync(moved, Ct);
        (await QueryAsync("tokenizer")).Entries.Should().BeEmpty();
        File.Exists(_vaultFolder.Combine("archive", "tokenizer-trigram-en-fts5.md")).Should().BeFalse();

        var restored = await notes.RestoreAsync(trashed.Id, Ct);
        restored.Should().Be(moved);
        (await QueryAsync("tokenizer")).Entries.Should().ContainSingle();
    }

    [Fact]
    public async Task ExternalEdit_IsDetectedAsConflict_AndNeverOverwrittenSilently()
    {
        await OpenVaultAsync();
        var created = await _session.Notes.CreateAsync(new NewNoteRequest("Runbook de despliegue"), Ct);
        var fullPath = _vaultFolder.Combine("runbook-de-despliegue.md");

        // Another editor (e.g. VS Code) changes the file while the note is open in the app.
        var external = created.Text + "Paso añadido desde VS Code.\n";
        await File.WriteAllTextAsync(fullPath, external, Ct);

        var result = await _session.Notes.SaveAsync(created.Path, created.Text + "Mi edición.\n", created.Hash, SaveMode.DetectConflicts, Ct);

        var conflict = result.Should().BeOfType<SaveResult.Conflict>().Subject;
        conflict.Kind.Should().Be(SaveConflictKind.ModifiedOnDisk);
        conflict.Disk!.Text.Should().Be(external);
        (await File.ReadAllTextAsync(fullPath, Ct)).Should().Be(external);
    }

    [Fact]
    public async Task ExternalChanges_ArePickedUpByTheWatcher()
    {
        await OpenVaultAsync();
        var changes = new List<NotesChangedEventArgs>();
        _session.Events.NotesChanged += (_, e) =>
        {
            lock (changes)
            {
                changes.Add(e);
            }
        };

        _vaultFolder.Write("from-another-editor.md", "---\ntitle: Escrita fuera\n---\nContenido externo único.\n");

        await EventuallyAsync(async () => (await QueryAsync("externo")).Entries.Count == 1);
        (await QueryAsync("externo")).Entries[0].Note.Title.Should().Be("Escrita fuera");

        File.Delete(_vaultFolder.Combine("from-another-editor.md"));

        await EventuallyAsync(async () => (await QueryAsync("externo")).Entries.Count == 0);
        lock (changes)
        {
            changes.Should().Contain(change => change.Source == NotesChangeSource.External);
        }
    }

    [Fact]
    public async Task ReindexAll_RebuildsTheIndexFromTheFiles()
    {
        _vaultFolder.Write("a.md", "# Alpha\nprimera nota");
        _vaultFolder.Write("b.md", "# Beta\nsegunda nota");
        await OpenVaultAsync();

        _session.RequestRebuild();
        await _session.WhenIdleAsync(Ct);

        (await _session.Queries.CountAsync(Ct)).Should().Be(2);
        (await QueryAsync("segunda")).Entries.Should().ContainSingle();
    }

    [Fact]
    public async Task ReopeningAVault_IsIncremental_AndTheIndexLivesOutsideTheVault()
    {
        for (var i = 0; i < 300; i++)
        {
            _vaultFolder.Write($"bulk/note-{i:D3}.md", $"---\ntitle: Nota {i}\ntags: [bulk]\n---\nContenido marca{i:D3}fin de la nota.\n");
        }

        await OpenVaultAsync();
        (await _session.Queries.CountAsync(Ct)).Should().Be(300);
        var vault = _session.Vault;
        await _services.GetRequiredService<IVaultSessionManager>().CloseAsync();

        _vaultFolder.Write("bulk/note-000.md", "---\ntitle: Nota cambiada\n---\nnuevo contenido\n");
        File.Delete(_vaultFolder.Combine("bulk", "note-001.md"));

        _session = await _services.GetRequiredService<IVaultSessionManager>().OpenAsync(vault, Ct);
        await _session.WhenIdleAsync(Ct);

        (await _session.Queries.CountAsync(Ct)).Should().Be(299);
        (await QueryAsync("marca001fin")).Entries.Should().BeEmpty("the deleted note left the index");
        (await QueryAsync("marca002fin")).Entries.Should().ContainSingle("untouched notes stay indexed");
        (await QueryAsync("cambiada")).Entries.Should().ContainSingle();
        Directory.GetFiles(_vaultFolder.Path, "*.db*", SearchOption.AllDirectories).Should().BeEmpty("the index is stored in the app data folder");
        Directory.GetFiles(Path.Combine(_dataFolder.Path, "indexes"), "*.db").Should().ContainSingle();
    }

    [Fact]
    public async Task RemovingAVault_DeletesItsIndexAndKeepsTheNotes()
    {
        _vaultFolder.Write("keep.md", "# Keep me");
        await OpenVaultAsync();
        var vault = _session.Vault;
        await _services.GetRequiredService<IVaultSessionManager>().CloseAsync();

        await _services.GetRequiredService<IVaultRegistry>().RemoveAsync(vault.Id, Ct);

        Directory.GetFiles(Path.Combine(_dataFolder.Path, "indexes")).Should().BeEmpty();
        File.Exists(_vaultFolder.Combine("keep.md")).Should().BeTrue();
        _services.GetRequiredService<IVaultRegistry>().Vaults.Should().BeEmpty();
    }

    private const string SpecNote = """
        ---
        id: 01J8ZQ4M9T3N7K5W2X6Y8V0B1C
        title: Deadlock en actualización de inventario
        project: azure-microservices
        tags: [sql-server, deadlock, performance]
        type: bug
        created: 2026-09-12
        updated: 2026-09-14
        links:
          commits: [a3f9c21, 7be04d8]
          tickets: [MS-482]
          notes: ["[[benchmark-where-select-2026-08]]"]
        ---

        El proceso nocturno de inventario se bloqueaba.
        """;

    private async Task OpenVaultAsync()
    {
        var vault = await _services.GetRequiredService<IVaultRegistry>().AddAsync(_vaultFolder.Path, Ct);
        _session = await _services.GetRequiredService<IVaultSessionManager>().OpenAsync(vault, Ct);
        await _session.WhenIdleAsync(Ct);
    }

    private Task<NoteQueryResult> QueryAsync(string text) =>
        _session.Queries.QueryAsync(text, NoteFilter.Empty, NoteSortOrder.Relevance, 50, Ct);

    private static async Task EventuallyAsync(Func<Task<bool>> condition)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(20);
        while (DateTime.UtcNow < deadline)
        {
            if (await condition())
            {
                return;
            }

            await Task.Delay(50, Ct);
        }

        throw new Xunit.Sdk.XunitException("The expected state was not reached in time.");
    }
}
