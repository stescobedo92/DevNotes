namespace DevNotes.Desktop.Services;

public sealed class EditorOptions
{
    public const string SectionName = "Editor";

    /// <summary>Quiet period after the last keystroke before the note is saved automatically.</summary>
    public int AutosaveDelayMilliseconds { get; set; } = 1_500;

    /// <summary>Quiet period after the last keystroke before the preview and outline are refreshed.</summary>
    public int PreviewDelayMilliseconds { get; set; } = 150;

    public TimeSpan AutosaveDelay => TimeSpan.FromMilliseconds(Math.Clamp(AutosaveDelayMilliseconds, 200, 60_000));

    public TimeSpan PreviewDelay => TimeSpan.FromMilliseconds(Math.Clamp(PreviewDelayMilliseconds, 0, 5_000));
}
