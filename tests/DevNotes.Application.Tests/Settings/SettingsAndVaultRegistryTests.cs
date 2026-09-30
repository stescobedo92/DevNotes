using DevNotes.Application.Search;
using DevNotes.Application.Settings;
using DevNotes.Application.Tests.Fakes;
using DevNotes.Application.Vaults;
using DevNotes.Domain.Vaults;
using Microsoft.Extensions.Time.Testing;

namespace DevNotes.Application.Tests.Settings;

public sealed class SettingsAndVaultRegistryTests : IAsyncDisposable
{
    private readonly InMemorySettingsStore _store = new();
    private readonly SettingsService _settings;
    private readonly RecordingIndexFactory _indexes = new();
    private readonly VaultRegistry _registry;
    private readonly string _tempRoot = Directory.CreateTempSubdirectory("devnotes-registry-").FullName;

    public SettingsAndVaultRegistryTests()
    {
        _settings = new SettingsService(_store);
        _registry = new VaultRegistry(_settings, _indexes, new FakeTimeProvider(new DateTimeOffset(2026, 9, 30, 0, 0, 0, TimeSpan.Zero)));
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public async ValueTask DisposeAsync()
    {
        await _settings.DisposeAsync();
        Directory.Delete(_tempRoot, recursive: true);
    }

    [Fact]
    public void Defaults_MatchTheProductSpecification()
    {
        var defaults = new AppSettings();

        defaults.Theme.Should().Be(AppTheme.Dark, "dark is the default theme");
        defaults.FontSize.Should().Be(14);
        defaults.ViewMode.Should().Be(EditorViewMode.Split);
        defaults.SortOrder.Should().Be(NoteSortOrder.UpdatedDescending);
        defaults.Vaults.Should().BeEmpty();
        defaults.ActiveVaultId.Should().BeNull();
        defaults.Normalize().Should().BeEquivalentTo(defaults);
    }

    [Fact]
    public void Normalize_HandEditedGarbage_IsForcedIntoValidRanges()
    {
        var broken = new AppSettings
        {
            SchemaVersion = 99,
            Vaults = null!,
            Theme = (AppTheme)42,
            Density = (UiDensity)(-1),
            ViewMode = (EditorViewMode)7,
            SortOrder = (NoteSortOrder)9,
            FontSize = double.NaN,
            Layout = new LayoutSettings
            {
                SidebarWidth = -50,
                InspectorWidth = 1e9,
                NoteListHeight = double.PositiveInfinity,
                WindowWidth = 1,
                WindowHeight = double.NaN,
            },
        };

        var normalized = broken.Normalize();

        normalized.SchemaVersion.Should().Be(AppSettings.CurrentSchemaVersion);
        normalized.Vaults.Should().BeEmpty();
        normalized.Theme.Should().Be(AppTheme.Dark);
        normalized.Density.Should().Be(UiDensity.Comfortable);
        normalized.ViewMode.Should().Be(EditorViewMode.Split);
        normalized.SortOrder.Should().Be(NoteSortOrder.UpdatedDescending);
        normalized.FontSize.Should().Be(AppSettings.DefaultFontSize);
        normalized.Layout.SidebarWidth.Should().Be(LayoutSettings.MinPanelWidth);
        normalized.Layout.InspectorWidth.Should().Be(LayoutSettings.MaxPanelWidth);
        normalized.Layout.NoteListHeight.Should().Be(new LayoutSettings().NoteListHeight);
        normalized.Layout.WindowWidth.Should().Be(720);
        normalized.Layout.WindowHeight.Should().Be(new LayoutSettings().WindowHeight);
        (broken with { Layout = null! }).Normalize().Layout.Should().Be(new LayoutSettings());
    }

    [Theory]
    [InlineData(4, AppSettings.MinFontSize)]
    [InlineData(18, 18)]
    [InlineData(400, AppSettings.MaxFontSize)]
    public void Normalize_FontSize_IsClamped(double input, double expected)
    {
        (new AppSettings { FontSize = input }).Normalize().FontSize.Should().Be(expected);
    }

    [Fact]
    public async Task LoadAsync_ReadsAndNormalizesStoredSettings()
    {
        _store.Stored = new AppSettings { Theme = AppTheme.Light, FontSize = 1000 };
        AppSettings? notified = null;
        _settings.Changed += (_, settings) => notified = settings;

        await _settings.LoadAsync(Ct);

        _settings.Current.Theme.Should().Be(AppTheme.Light);
        _settings.Current.FontSize.Should().Be(AppSettings.MaxFontSize);
        notified.Should().BeSameAs(_settings.Current);
    }

    [Fact]
    public async Task UpdateAsync_PersistsAndNotifies()
    {
        var notifications = 0;
        _settings.Changed += (_, _) => notifications++;

        var updated = await _settings.UpdateAsync(current => current with { Theme = AppTheme.System }, Ct);

        updated.Theme.Should().Be(AppTheme.System);
        _settings.Current.Should().BeSameAs(updated);
        _store.Stored.Theme.Should().Be(AppTheme.System);
        _store.SaveCount.Should().Be(1);
        notifications.Should().Be(1);
    }

    [Fact]
    public async Task UpdateAsync_NoEffectiveChange_DoesNotWrite()
    {
        var notifications = 0;
        _settings.Changed += (_, _) => notifications++;

        await _settings.UpdateAsync(current => current with { Theme = current.Theme }, Ct);

        _store.SaveCount.Should().Be(0);
        notifications.Should().Be(0);
    }

    [Fact]
    public async Task UpdateAsync_StoreFailure_LeavesMemoryUntouched()
    {
        _store.FailOnSave = new IOException("disk full");

        var act = () => _settings.UpdateAsync(current => current with { Theme = AppTheme.Light }, Ct);

        await act.Should().ThrowAsync<IOException>();
        _settings.Current.Theme.Should().Be(AppTheme.Dark, "memory must not claim a state that never reached the disk");
    }

    [Fact]
    public async Task UpdateAsync_ConcurrentUpdates_AreSerializedWithoutLosingAny()
    {
        var updates = Enumerable.Range(0, 50).Select(_ =>
            Task.Run(() => _settings.UpdateAsync(current => current with { FontSize = current.FontSize + 0.1 }, Ct), Ct));

        await Task.WhenAll(updates);

        _settings.Current.FontSize.Should().BeApproximately(AppSettings.DefaultFontSize + 5, 0.0001);
        _store.SaveCount.Should().Be(50);
    }

    [Fact]
    public async Task DisposeAsync_LetsUpdatesInFlightReachTheStore_AndRejectsLaterOnes()
    {
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _store.HoldSaves = release.Task;
        var first = _settings.UpdateAsync(current => current with { Theme = AppTheme.Light }, Ct);
        var second = _settings.UpdateAsync(current => current with { FontSize = 18 }, Ct);

        var disposal = _settings.DisposeAsync().AsTask();
        disposal.IsCompleted.Should().BeFalse("two updates have not been written yet");
        release.SetResult();
        await disposal;

        (await first).Theme.Should().Be(AppTheme.Light);
        (await second).FontSize.Should().Be(18);
        _store.Stored.Should().BeEquivalentTo(new AppSettings { Theme = AppTheme.Light, FontSize = 18 });

        var late = () => _settings.UpdateAsync(current => current with { FontSize = 20 }, Ct);
        await late.Should().ThrowAsync<ObjectDisposedException>();
        var reload = () => _settings.LoadAsync(Ct);
        await reload.Should().ThrowAsync<ObjectDisposedException>();
        _store.SaveCount.Should().Be(2);
    }

    [Fact]
    public async Task AddAsync_RegistersFolderAndMakesItActive()
    {
        var folder = Directory.CreateDirectory(Path.Combine(_tempRoot, "work-notes")).FullName;

        var vault = await _registry.AddAsync(folder + Path.DirectorySeparatorChar, Ct);

        vault.Name.Should().Be("work-notes");
        vault.RootPath.Should().Be(folder);
        _registry.Vaults.Should().ContainSingle().Which.Should().Be(vault);
        _registry.ActiveVault.Should().Be(vault);
        _store.Stored.ActiveVaultId.Should().Be(vault.Id.Value);
    }

    [Fact]
    public async Task AddAsync_SameFolderTwice_ReturnsTheExistingVault()
    {
        var folder = Directory.CreateDirectory(Path.Combine(_tempRoot, "notes")).FullName;
        var other = Directory.CreateDirectory(Path.Combine(_tempRoot, "other")).FullName;

        var first = await _registry.AddAsync(folder, Ct);
        await _registry.AddAsync(other, Ct);
        var again = await _registry.AddAsync(Path.Combine(folder, "..", "notes"), Ct);

        again.Should().Be(first);
        _registry.Vaults.Should().HaveCount(2);
        _registry.ActiveVault.Should().Be(first, "adding an existing vault activates it");
    }

    [Fact]
    public async Task AddAsync_MissingFolder_Throws()
    {
        var act = () => _registry.AddAsync(Path.Combine(_tempRoot, "does-not-exist"), Ct);

        (await act.Should().ThrowAsync<VaultFolderNotFoundException>()).Which.FolderPath.Should().EndWith("does-not-exist");
        _registry.Vaults.Should().BeEmpty();
    }

    [Fact]
    public async Task SetActiveAsync_SwitchesVault_AndRejectsUnknownIds()
    {
        var first = await _registry.AddAsync(Directory.CreateDirectory(Path.Combine(_tempRoot, "a")).FullName, Ct);
        var second = await _registry.AddAsync(Directory.CreateDirectory(Path.Combine(_tempRoot, "b")).FullName, Ct);
        _registry.ActiveVault.Should().Be(second);

        await _registry.SetActiveAsync(first.Id, Ct);

        _registry.ActiveVault.Should().Be(first);
        var act = () => _registry.SetActiveAsync(VaultId.Parse("unknown"), Ct);
        await act.Should().ThrowAsync<InvalidOperationException>();
    }

    [Fact]
    public async Task RemoveAsync_ForgetsVaultDeletesIndexAndKeepsFiles()
    {
        var folder = Directory.CreateDirectory(Path.Combine(_tempRoot, "a")).FullName;
        await File.WriteAllTextAsync(Path.Combine(folder, "note.md"), "# keep me", Ct);
        var first = await _registry.AddAsync(folder, Ct);
        var second = await _registry.AddAsync(Directory.CreateDirectory(Path.Combine(_tempRoot, "b")).FullName, Ct);
        await _registry.SetActiveAsync(first.Id, Ct);

        await _registry.RemoveAsync(first.Id, Ct);

        _registry.Vaults.Should().ContainSingle().Which.Should().Be(second);
        _registry.ActiveVault.Should().Be(second);
        _indexes.Deleted.Should().Equal(first.Id);
        File.Exists(Path.Combine(folder, "note.md")).Should().BeTrue("removing a vault never deletes notes");
    }

    [Fact]
    public async Task Vaults_MalformedEntries_AreIgnored()
    {
        var valid = Directory.CreateDirectory(Path.Combine(_tempRoot, "valid")).FullName;
        _store.Stored = new AppSettings
        {
            ActiveVaultId = "missing",
            Vaults =
            [
                new VaultSettings(@"..\..\evil", "Traversal id", valid),
                new VaultSettings("ok-1", " ", valid),
                new VaultSettings("ok-2", "Relative", "relative/path"),
                null!,
                new VaultSettings("ok-3", "Valid", valid),
            ],
        };

        await _settings.LoadAsync(Ct);

        _registry.Vaults.Should().ContainSingle().Which.Id.Value.Should().Be("ok-3");
        _registry.ActiveVault!.Id.Value.Should().Be("ok-3", "an unknown active id falls back to the first valid vault");
    }

    [Fact]
    public void ActiveVault_NoVaults_IsNull()
    {
        _registry.ActiveVault.Should().BeNull();
    }
}
