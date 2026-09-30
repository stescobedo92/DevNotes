using System.Text;
using DevNotes.Domain.Notes;

namespace DevNotes.Application.Notes;

public static class NoteTemplates
{
    /// <summary>Content of a brand-new note: complete frontmatter plus a level-1 heading.</summary>
    public static string CreateDefault(NoteId id, string title, NoteType type, string? project, DateOnly today)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(title);

        var date = YamlScalar.FormatDate(today);
        var builder = new StringBuilder(256)
            .Append("---\n")
            .Append("id: ").Append(YamlScalar.Format(id.Value)).Append('\n')
            .Append("title: ").Append(YamlScalar.Format(title)).Append('\n');

        if (!string.IsNullOrWhiteSpace(project))
        {
            builder.Append("project: ").Append(YamlScalar.Format(project.Trim())).Append('\n');
        }

        return builder
            .Append("tags: []\n")
            .Append("type: ").Append(type.ToKey()).Append('\n')
            .Append("created: ").Append(date).Append('\n')
            .Append("updated: ").Append(date).Append('\n')
            .Append("---\n\n")
            .Append("# ").Append(title).Append("\n\n")
            .ToString();
    }
}
