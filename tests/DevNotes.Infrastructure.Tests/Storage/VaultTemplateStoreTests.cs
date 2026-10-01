using System.Text;
using DevNotes.Application.Notes;
using DevNotes.Infrastructure.Storage;

namespace DevNotes.Infrastructure.Tests.Storage;

public sealed class VaultTemplateStoreTests : IDisposable
{
    private readonly TempDirectory _vault = new("devnotes-templates-");
    private readonly VaultTemplateStore _store;

    public VaultTemplateStoreTests()
    {
        _store = new VaultTemplateStore(_vault.Path);
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public void Dispose() => _vault.Dispose();

    [Fact]
    public async Task LoadAllAsync_WithoutFolder_IsEmpty()
    {
        (await _store.LoadAllAsync(Ct)).Should().BeEmpty();
        Directory.Exists(_vault.Combine(".devnotes")).Should().BeFalse("reading creates nothing");
    }

    [Fact]
    public async Task SaveAsync_CreatesTheFolder_WritesUtf8_AndKeepsTheTrashIgnored()
    {
        await _store.SaveAsync("bug", "# {{title}}\n\nSíntoma\n", Ct);

        var file = _vault.Combine(".devnotes", "templates", "bug.md");
        File.ReadAllBytes(file).Should().Equal(Encoding.UTF8.GetBytes("# {{title}}\n\nSíntoma\n"), "UTF-8 without BOM");
        File.ReadAllText(_vault.Combine(".devnotes", ".gitignore")).Should().Be(InternalFolder.GitIgnore);
        Directory.GetFiles(_vault.Combine(".devnotes", "templates")).Should().ContainSingle("no temporary file is left behind");
        (await _store.LoadAllAsync(Ct)).Should().BeEquivalentTo(new Dictionary<string, string> { ["bug"] = "# {{title}}\n\nSíntoma\n" });
    }

    [Fact]
    public async Task SaveAsync_Overwrites_AndDeleteRemoves()
    {
        await _store.SaveAsync("bug", "first", Ct);
        await _store.SaveAsync("bug", "second", Ct);

        (await _store.LoadAllAsync(Ct))["bug"].Should().Be("second");

        await _store.DeleteAsync("bug", Ct);
        await _store.DeleteAsync("bug", Ct);

        (await _store.LoadAllAsync(Ct)).Should().BeEmpty();
    }

    [Fact]
    public async Task LoadAllAsync_IgnoresFilesThatAreNotTemplates()
    {
        var folder = Directory.CreateDirectory(_vault.Combine(".devnotes", "templates")).FullName;
        File.WriteAllText(Path.Combine(folder, "Bad Name.md"), "x");
        File.WriteAllText(Path.Combine(folder, "notes.txt"), "x");
        File.WriteAllText(Path.Combine(folder, "huge.md"), new string('x', VaultTemplateStore.MaxFileBytes + 1));
        File.WriteAllText(Path.Combine(folder, "long.md"), new string('x', TemplateService.MaxTemplateLength + 1));
        File.WriteAllText(Path.Combine(folder, "ok.md"), "fine");
        Directory.CreateDirectory(Path.Combine(folder, "nested.md"));

        var templates = await _store.LoadAllAsync(Ct);

        templates.Keys.Should().Equal("ok");
    }

    [Fact]
    public async Task LoadAllAsync_ReadsLegacyEncodedFilesWithoutFailing()
    {
        var folder = Directory.CreateDirectory(_vault.Combine(".devnotes", "templates")).FullName;
        File.WriteAllBytes(Path.Combine(folder, "legacy.md"), [0x53, 0xED, 0x6E, 0x74, 0x6F, 0x6D, 0x61]); // "Síntoma" in Windows-1252

        (await _store.LoadAllAsync(Ct))["legacy"].Should().Be("Síntoma");
    }

    [Theory]
    [InlineData("Bug")]
    [InlineData("../bug")]
    [InlineData("")]
    [InlineData("a b")]
    public async Task SaveAndDelete_RejectInvalidKeys(string key)
    {
        await FluentActions.Invoking(() => _store.SaveAsync(key, "x", Ct)).Should().ThrowAsync<ArgumentException>();
        await FluentActions.Invoking(() => _store.DeleteAsync(key, Ct)).Should().ThrowAsync<ArgumentException>();
        Directory.Exists(_vault.Combine(".devnotes")).Should().BeFalse();
    }

    [Fact]
    public async Task SaveAsync_RefusesToWriteThroughALinkedInternalFolder()
    {
        using var elsewhere = new TempDirectory("devnotes-elsewhere-");
        if (!TestLinks.TryCreateDirectoryLink(_vault.Combine(".devnotes"), elsewhere.Path))
        {
            return; // Links cannot be created in this environment.
        }

        await FluentActions.Invoking(() => _store.SaveAsync("bug", "x", Ct)).Should().ThrowAsync<UnauthorizedAccessException>();
        Directory.GetFileSystemEntries(elsewhere.Path).Should().BeEmpty("nothing may be written outside the vault");
    }
}
