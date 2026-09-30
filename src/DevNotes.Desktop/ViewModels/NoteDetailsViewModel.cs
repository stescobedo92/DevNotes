using System.Globalization;
using DevNotes.Desktop.Resources;
using DevNotes.Domain.Notes;

namespace DevNotes.Desktop.ViewModels;

/// <summary>Read-only metadata of the open note, formatted for the details panel.</summary>
public sealed class NoteDetailsViewModel
{
    public NoteDetailsViewModel(NoteMetadata metadata, NotePath path)
    {
        ArgumentNullException.ThrowIfNull(metadata);

        TypeLabel = NoteTypeLabels.Get(metadata.Type);
        Project = string.IsNullOrEmpty(metadata.Project) ? Strings.Field_None : metadata.Project;
        HasProject = !string.IsNullOrEmpty(metadata.Project);
        Tags = [.. metadata.Tags.Select(tag => tag.Value)];
        Created = FormatDate(metadata.Created);
        Updated = FormatDate(metadata.Updated);
        Id = metadata.Id?.Value ?? Strings.Field_None;
        Path = path.Value;
    }

    public string TypeLabel { get; }

    public string Project { get; }

    public bool HasProject { get; }

    public IReadOnlyList<string> Tags { get; }

    public bool HasTags => Tags.Count > 0;

    public string Created { get; }

    public string Updated { get; }

    public string Id { get; }

    public string Path { get; }

    private static string FormatDate(DateOnly? date) =>
        date is { } value ? value.ToString("d", CultureInfo.CurrentCulture) : Strings.Field_None;
}
