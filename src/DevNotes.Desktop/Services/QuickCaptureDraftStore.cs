using System.Text.Json;
using System.Text.Json.Serialization;
using DevNotes.Infrastructure;
using Microsoft.Extensions.Logging;

namespace DevNotes.Desktop.Services;

/// <summary>What the user typed in the quick capture and has not saved as a note yet.</summary>
public sealed record CaptureDraft(string Title, string Text, string Project)
{
    public bool IsEmpty => string.IsNullOrWhiteSpace(Title) && string.IsNullOrWhiteSpace(Text);
}

/// <summary>
/// Keeps the capture draft outside the process, so hiding the window, quitting the app or a crash
/// never loses what was typed. Implementations never throw: a draft that cannot be stored stays
/// in memory, a file that cannot be read yields no draft.
/// </summary>
public interface IQuickCaptureDraftStore
{
    CaptureDraft? Load();

    void Save(CaptureDraft draft);

    void Clear();
}

/// <summary>A small JSON file next to the settings, replaced atomically.</summary>
public sealed partial class FileQuickCaptureDraftStore : IQuickCaptureDraftStore
{
    public const string FileName = "capture-draft.json";

    private readonly Lock _gate = new();
    private readonly string _path;
    private readonly ILogger<FileQuickCaptureDraftStore> _logger;

    public FileQuickCaptureDraftStore(IAppPaths paths, ILogger<FileQuickCaptureDraftStore> logger)
    {
        ArgumentNullException.ThrowIfNull(paths);
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _path = Path.Combine(paths.DataDirectory, FileName);
    }

    public CaptureDraft? Load()
    {
        lock (_gate)
        {
            return LoadCore();
        }
    }

    private CaptureDraft? LoadCore()
    {
        try
        {
            if (!File.Exists(_path))
            {
                return null;
            }

            var draft = JsonSerializer.Deserialize(File.ReadAllBytes(_path), DraftJsonContext.Default.CaptureDraft);
            return draft is { IsEmpty: false } ? draft : null;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
        {
            LogDraftUnreadable(exception);
            return null;
        }
    }

    public void Save(CaptureDraft draft)
    {
        ArgumentNullException.ThrowIfNull(draft);
        if (draft.IsEmpty)
        {
            Clear();
            return;
        }

        // The window calls from the UI thread; the gate keeps the file consistent for any other caller.
        lock (_gate)
        {
            var temporary = _path + ".tmp";
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
                File.WriteAllBytes(temporary, JsonSerializer.SerializeToUtf8Bytes(draft, DraftJsonContext.Default.CaptureDraft));
                File.Move(temporary, _path, overwrite: true);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                // The draft is still in memory; the next change tries again.
                LogDraftNotSaved(exception);
            }
        }
    }

    public void Clear()
    {
        lock (_gate)
        {
            try
            {
                File.Delete(_path);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                LogDraftNotSaved(exception);
            }
        }
    }

    // The content of the draft is user text and is never logged.
    [LoggerMessage(EventId = 1010, Level = LogLevel.Warning, Message = "The quick-capture draft could not be stored")]
    private partial void LogDraftNotSaved(Exception exception);

    [LoggerMessage(EventId = 1011, Level = LogLevel.Warning, Message = "The stored quick-capture draft could not be read")]
    private partial void LogDraftUnreadable(Exception exception);
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, WriteIndented = true)]
[JsonSerializable(typeof(CaptureDraft))]
internal sealed partial class DraftJsonContext : JsonSerializerContext;
