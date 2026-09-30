using DevNotes.Infrastructure.Logging;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Time.Testing;

namespace DevNotes.Infrastructure.Tests.Logging;

public sealed class FileLoggerProviderTests : IDisposable
{
    private readonly TempDirectory _temp = new();
    private readonly AppPaths _paths;
    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 9, 30, 10, 20, 30, 123, TimeSpan.Zero));

    public FileLoggerProviderTests()
    {
        _paths = new AppPaths(_temp.Path);
    }

    public void Dispose() => _temp.Dispose();

    [Fact]
    public async Task Log_WritesOneStructuredLinePerEntryIntoTheDailyFile()
    {
        var provider = new FileLoggerProvider(_paths, _time);
        var logger = provider.CreateLogger("DevNotes.Tests.Category");

        logger.LogInformation(new EventId(42), "Indexed {Count} notes\nin {Elapsed} ms", 12, 34);
        logger.LogError(new EventId(7), new InvalidOperationException("boom"), "Failed to read '{Path}'", "a.md");
        await provider.DisposeAsync();

        var lines = await File.ReadAllLinesAsync(Path.Combine(_paths.LogDirectory, "devnotes-20260930.log"), TestContext.Current.CancellationToken);
        lines[0].Should().Be("2026-09-30T10:20:30.123Z INF DevNotes.Tests.Category[42] Indexed 12 notes in 34 ms");
        lines[1].Should().Be("2026-09-30T10:20:30.123Z ERR DevNotes.Tests.Category[7] Failed to read 'a.md'");
        lines[2].Should().StartWith("    System.InvalidOperationException: boom");
    }

    [Fact]
    public async Task CreateLogger_SameCategory_ReturnsSameInstance_AndNoneLevelIsDisabled()
    {
        await using var provider = new FileLoggerProvider(_paths, _time);

        var logger = provider.CreateLogger("A");

        provider.CreateLogger("A").Should().BeSameAs(logger);
        provider.CreateLogger("B").Should().NotBeSameAs(logger);
        logger.IsEnabled(LogLevel.None).Should().BeFalse();
        logger.IsEnabled(LogLevel.Trace).Should().BeTrue();
        logger.BeginScope("scope").Should().BeNull();
    }

    [Fact]
    public async Task OldLogFiles_ArePrunedOnStart()
    {
        Directory.CreateDirectory(_paths.LogDirectory);
        for (var day = 1; day <= 12; day++)
        {
            await File.WriteAllTextAsync(Path.Combine(_paths.LogDirectory, $"devnotes-202609{day:D2}.log"), "old", TestContext.Current.CancellationToken);
        }

        var provider = new FileLoggerProvider(_paths, _time);
        provider.CreateLogger("A").LogWarning("trigger the writer");
        await provider.DisposeAsync();

        var remaining = Directory.GetFiles(_paths.LogDirectory).Select(Path.GetFileName).Order().ToList();
        remaining.Should().HaveCount(8, "the 7 newest old files plus today's file are kept");
        remaining.Should().NotContain("devnotes-20260901.log").And.Contain("devnotes-20260912.log").And.Contain("devnotes-20260930.log");
    }

    [Fact]
    public async Task Dispose_IsIdempotent_AndLoggingAfterwardsIsIgnored()
    {
        var provider = new FileLoggerProvider(_paths, _time);
        var logger = provider.CreateLogger("A");

        await provider.DisposeAsync();
        provider.Dispose();
        logger.LogInformation("after dispose");

        logger.IsEnabled(LogLevel.Information).Should().BeFalse();
        Directory.Exists(_paths.LogDirectory).Should().BeFalse("nothing was logged, so nothing is created on disk");
    }

    [Fact]
    public async Task UnwritableLogFolder_DoesNotCrashTheApplication()
    {
        // A file where the log directory should be makes every write fail.
        await File.WriteAllTextAsync(_paths.LogDirectory, "not a folder", TestContext.Current.CancellationToken);
        var provider = new FileLoggerProvider(_paths, _time);

        provider.CreateLogger("A").LogError("nobody will read this");
        var dispose = async () => await provider.DisposeAsync();

        await dispose.Should().NotThrowAsync();
    }

    [Fact]
    public async Task LogFileHeldByAnotherProgram_LosesThatBatchOnly_NotTheLogger()
    {
        Directory.CreateDirectory(_paths.LogDirectory);
        var today = Path.Combine(_paths.LogDirectory, "devnotes-20260930.log");
        var provider = new FileLoggerProvider(_paths, _time);
        var logger = provider.CreateLogger("A");

        await using (new FileStream(today, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None))
        {
            logger.LogWarning("written while the file is locked");
            await Task.Delay(TimeSpan.FromMilliseconds(400), TestContext.Current.CancellationToken); // Let the writer hit the lock.
        }

        logger.LogWarning("written after the lock was released");
        await provider.DisposeAsync();

        (await File.ReadAllTextAsync(today, TestContext.Current.CancellationToken)).Should().Contain("written after the lock was released");
    }

    [Fact]
    public async Task OldLogFileThatCannotBeDeleted_DoesNotPreventLogging()
    {
        Directory.CreateDirectory(_paths.LogDirectory);
        for (var day = 1; day <= 9; day++)
        {
            await File.WriteAllTextAsync(Path.Combine(_paths.LogDirectory, $"devnotes-202609{day:D2}.log"), "old", TestContext.Current.CancellationToken);
        }

        // The oldest file is open in an editor that does not allow deleting it.
        await using (new FileStream(Path.Combine(_paths.LogDirectory, "devnotes-20260901.log"), FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            var provider = new FileLoggerProvider(_paths, _time);
            provider.CreateLogger("A").LogWarning("still logging");
            await provider.DisposeAsync();
        }

        (await File.ReadAllTextAsync(Path.Combine(_paths.LogDirectory, "devnotes-20260930.log"), TestContext.Current.CancellationToken))
            .Should().Contain("still logging");
        File.Exists(Path.Combine(_paths.LogDirectory, "devnotes-20260902.log")).Should().BeFalse("the other old files are still pruned");
    }

    [Fact]
    public async Task AddDevNotesFile_RegistersTheProvider_AndInfrastructureRegistersThePorts()
    {
        var services = new ServiceCollection()
            .AddDevNotesInfrastructure(_temp.Path)
            .AddLogging(builder => builder.AddDevNotesFile());

        await using var provider = services.BuildServiceProvider();

        provider.GetServices<ILoggerProvider>().Should().ContainSingle().Which.Should().BeOfType<FileLoggerProvider>();
        provider.GetRequiredService<IAppPaths>().DataDirectory.Should().Be(_temp.Path);
        provider.GetRequiredService<Application.Settings.ISettingsStore>().Should().NotBeNull();
        provider.GetRequiredService<Application.Abstractions.INoteFileStoreFactory>().Should().NotBeNull();
        provider.GetRequiredService<Application.Abstractions.INoteIndexFactory>().Should().NotBeNull();
        provider.GetRequiredService<Application.Abstractions.IVaultWatcherFactory>().Should().NotBeNull();
    }
}
