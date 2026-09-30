namespace DevNotes.Domain.Notes;

public interface INoteIdGenerator
{
    NoteId NewId();
}

/// <summary>Generates time-ordered ULID identifiers using the supplied clock.</summary>
public sealed class UlidNoteIdGenerator(TimeProvider timeProvider) : INoteIdGenerator
{
    private readonly TimeProvider _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));

    public NoteId NewId() => NoteId.NewId(_timeProvider.GetUtcNow());
}
