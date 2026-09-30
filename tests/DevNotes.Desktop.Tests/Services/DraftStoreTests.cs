using DevNotes.Desktop.Services;
using DevNotes.Desktop.Tests.Support;
using DevNotes.Infrastructure;
using Microsoft.Extensions.Logging.Abstractions;

namespace DevNotes.Desktop.Tests.Services;

public sealed class DraftStoreTests : IDisposable
{
    private readonly TempDirectory _data = new("devnotes-draft-");

    public void Dispose() => _data.Dispose();

    [Fact]
    public void SaveLoadClear_RoundTrip_ThroughTheDataFolder()
    {
        var store = new FileQuickCaptureDraftStore(new AppPaths(_data.Combine("app")), NullLogger<FileQuickCaptureDraftStore>.Instance);
        var draft = new CaptureDraft("Título", "Línea 1\r\nLínea 2 con ñ", "cslinq");

        store.Load().Should().BeNull("nothing stored yet");
        store.Save(draft);

        File.Exists(_data.Combine("app", FileQuickCaptureDraftStore.FileName)).Should().BeTrue();
        Directory.GetFiles(_data.Combine("app")).Should().ContainSingle("no temporary file is left behind");
        new FileQuickCaptureDraftStore(new AppPaths(_data.Combine("app")), NullLogger<FileQuickCaptureDraftStore>.Instance).Load().Should().Be(draft);

        store.Save(new CaptureDraft(string.Empty, "  ", "p"));
        store.Load().Should().BeNull("an empty draft removes the file");

        store.Save(draft);
        store.Clear();
        store.Load().Should().BeNull();
        File.Exists(_data.Combine("app", FileQuickCaptureDraftStore.FileName)).Should().BeFalse();
    }

    [Fact]
    public void Load_UnreadableFile_YieldsNoDraft()
    {
        Directory.CreateDirectory(_data.Combine("app"));
        File.WriteAllText(_data.Combine("app", FileQuickCaptureDraftStore.FileName), "{ not json");
        var store = new FileQuickCaptureDraftStore(new AppPaths(_data.Combine("app")), NullLogger<FileQuickCaptureDraftStore>.Instance);

        store.Load().Should().BeNull();
    }
}
