using DevNotes.Application.Abstractions;
using DevNotes.Domain.Notes;

namespace DevNotes.Infrastructure.Tests;

/// <summary>Unique temporary folder removed when the test finishes.</summary>
public sealed class TempDirectory : IDisposable
{
    public TempDirectory(string prefix = "devnotes-test-")
    {
        Path = Directory.CreateTempSubdirectory(prefix).FullName;
    }

    public string Path { get; }

    public string Combine(params string[] parts) => System.IO.Path.Combine([Path, .. parts]);

    /// <summary>Writes a file below the folder (creating parent folders) and returns its full path.</summary>
    public string Write(string relativePath, string content)
    {
        var fullPath = Combine(relativePath.Split('/'));
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(fullPath)!);
        File.WriteAllText(fullPath, content);
        return fullPath;
    }

    public void Dispose()
    {
        // Antivirus or a slow watcher shutdown can hold a handle for a moment on Windows.
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                if (Directory.Exists(Path))
                {
                    Directory.Delete(Path, recursive: true);
                }

                return;
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException && attempt < 5)
            {
                Thread.Sleep(100 * attempt);
            }
        }
    }
}

public static class TestNotes
{
    private static readonly DateTimeOffset _baseTime = new(2026, 9, 1, 0, 0, 0, TimeSpan.Zero);

    public static IndexedNote Create(
        string path,
        string id,
        string title,
        string body = "",
        string[]? tags = null,
        DateOnly? updated = null,
        DateOnly? created = null,
        string? project = null,
        NoteType type = NoteType.Note,
        NoteLinks? links = null,
        int minutesAfterBase = 0)
    {
        var metadata = new NoteMetadata
        {
            Id = NoteId.Parse(id),
            Title = title,
            Project = project,
            Tags = Tag.NormalizeMany(tags ?? []),
            Type = type,
            Created = created,
            Updated = updated,
            Links = links ?? NoteLinks.Empty,
        };

        return new IndexedNote(
            NoteId.Parse(id),
            NotePath.Create(path),
            metadata,
            body,
            ContentHash.Compute(title + "\n" + body),
            body.Length,
            _baseTime.AddMinutes(minutesAfterBase));
    }
}
