using DevNotes.Application.Search;
using DevNotes.Application.Settings;
using DevNotes.Infrastructure.Settings;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;

namespace DevNotes.Infrastructure.Tests.Settings;

public sealed class JsonSettingsStoreTests : IDisposable
{
    private readonly TempDirectory _temp = new();
    private readonly AppPaths _paths;
    private readonly JsonSettingsStore _store;

    public JsonSettingsStoreTests()
    {
        _paths = new AppPaths(_temp.Combine("data"));
        _store = new JsonSettingsStore(
            _paths,
            new FakeTimeProvider(new DateTimeOffset(2026, 9, 30, 10, 20, 30, TimeSpan.Zero)),
            NullLogger<JsonSettingsStore>.Instance);
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public void Dispose() => _temp.Dispose();

    [Fact]
    public async Task LoadAsync_FirstRun_ReturnsDefaultsWithoutCreatingFiles()
    {
        var settings = await _store.LoadAsync(Ct);

        settings.Should().BeEquivalentTo(new AppSettings());
        Directory.Exists(_paths.DataDirectory).Should().BeFalse();
    }

    [Fact]
    public async Task SaveAsync_ThenLoadAsync_RoundTripsEveryValue()
    {
        var settings = new AppSettings
        {
            Vaults = [new VaultSettings("01J8ZQ4M9T3N7K5W2X6Y8V0B1C", "Work", @"C:\notes\work"), new VaultSettings("v2", "Personal", "/home/me/notes")],
            ActiveVaultId = "v2",
            Theme = AppTheme.System,
            Language = "es",
            FontSize = 16,
            Density = UiDensity.Compact,
            ReduceMotion = true,
            ViewMode = EditorViewMode.Preview,
            SortOrder = NoteSortOrder.TitleAscending,
            Layout = new LayoutSettings { SidebarWidth = 300, IsInspectorCollapsed = true, IsWindowMaximized = true, NoteListHeight = 400 },
        };

        await _store.SaveAsync(settings, Ct);
        var loaded = await _store.LoadAsync(Ct);

        loaded.Should().BeEquivalentTo(settings);
    }

    [Fact]
    public async Task SaveAsync_WritesReadableJsonAtomically()
    {
        await _store.SaveAsync(new AppSettings { Theme = AppTheme.Light, Language = null }, Ct);

        var json = await File.ReadAllTextAsync(_paths.SettingsFile, Ct);
        json.Should().Contain("\"theme\": \"Light\"", "enums are stored by name and keys are camelCase");
        json.Should().Contain("\"schemaVersion\": 1").And.NotContain("\"language\"");
        Directory.GetFiles(_paths.DataDirectory).Should().ContainSingle("no temporary file may be left behind");
    }

    [Fact]
    public async Task LoadAsync_HandEditedFileWithCommentsAndTrailingCommas_IsAccepted()
    {
        Directory.CreateDirectory(_paths.DataDirectory);
        await File.WriteAllTextAsync(
            _paths.SettingsFile,
            """
            {
              // my favourite theme
              "theme": "Light",
              "fontSize": 18,
              "unknownFutureSetting": true,
            }
            """,
            Ct);

        var settings = await _store.LoadAsync(Ct);

        settings.Should().BeEquivalentTo(
            new AppSettings { Theme = AppTheme.Light, FontSize = 18 },
            "keys missing from the file keep their default values");
    }

    [Theory]
    [InlineData("{ not json at all")]
    [InlineData("[1, 2, 3]")]
    [InlineData("{\"theme\": \"Purple\"}")]
    [InlineData("{\"fontSize\": \"big\"}")]
    public async Task LoadAsync_UnreadableFile_FallsBackToDefaultsAndKeepsABackup(string content)
    {
        Directory.CreateDirectory(_paths.DataDirectory);
        await File.WriteAllTextAsync(_paths.SettingsFile, content, Ct);

        var settings = await _store.LoadAsync(Ct);

        settings.Should().BeEquivalentTo(new AppSettings());
        File.Exists(_paths.SettingsFile).Should().BeFalse();
        var backup = Path.Combine(_paths.DataDirectory, "settings.unreadable-20260930-102030.json");
        (await File.ReadAllTextAsync(backup, Ct)).Should().Be(content, "nothing the user wrote is ever deleted");
    }

    [Fact]
    public async Task LoadAsync_NullDocument_ReturnsDefaults()
    {
        Directory.CreateDirectory(_paths.DataDirectory);
        await File.WriteAllTextAsync(_paths.SettingsFile, "null", Ct);

        (await _store.LoadAsync(Ct)).Should().BeEquivalentTo(new AppSettings());
    }

    [Fact]
    public async Task SaveAsync_Null_Throws()
    {
        await FluentActions.Invoking(() => _store.SaveAsync(null!, Ct)).Should().ThrowAsync<ArgumentNullException>();
    }

    [Fact]
    public void AppPaths_ExplicitRoot_LaysOutEveryLocationBelowIt()
    {
        _paths.DataDirectory.Should().Be(_temp.Combine("data"));
        _paths.SettingsFile.Should().Be(_temp.Combine("data", "settings.json"));
        _paths.IndexDirectory.Should().Be(_temp.Combine("data", "indexes"));
        _paths.LogDirectory.Should().Be(_temp.Combine("data", "logs"));
    }

    [Fact]
    public void AppPaths_Default_UsesPerUserApplicationData()
    {
        var previous = Environment.GetEnvironmentVariable(AppPaths.DataDirectoryVariable);
        try
        {
            Environment.SetEnvironmentVariable(AppPaths.DataDirectoryVariable, null);
            var defaultDirectory = new AppPaths().DataDirectory;
            defaultDirectory.Should().EndWith("DevNotes");
            Path.IsPathFullyQualified(defaultDirectory).Should().BeTrue();

            Environment.SetEnvironmentVariable(AppPaths.DataDirectoryVariable, _temp.Combine("from-env"));
            new AppPaths().DataDirectory.Should().Be(_temp.Combine("from-env"));
        }
        finally
        {
            Environment.SetEnvironmentVariable(AppPaths.DataDirectoryVariable, previous);
        }
    }
}
