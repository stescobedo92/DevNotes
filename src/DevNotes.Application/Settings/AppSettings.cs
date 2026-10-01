using DevNotes.Application.Search;
using DevNotes.Domain.Common;
using DevNotes.Domain.Notes;

namespace DevNotes.Application.Settings;

public enum AppTheme
{
    Dark = 0,
    Light,
    System,
}

public enum EditorViewMode
{
    Split = 0,
    Editor,
    Preview,
}

public enum UiDensity
{
    Comfortable = 0,
    Compact,
}

/// <summary>A registered vault as persisted in the settings file.</summary>
public sealed record VaultSettings(string Id, string Name, string Path);

/// <summary>What the user configured for one project (the <c>project:</c> value of the notes).</summary>
/// <param name="Name">Project name as written in the notes.</param>
/// <param name="RepositoryPath">Absolute path of the Git repository of the project, used to detect the active project.</param>
public sealed record ProjectSettings(string Name, string? RepositoryPath);

public sealed record QuickCaptureSettings
{
    /// <summary>Whether the system-wide shortcut is registered at start-up.</summary>
    public bool GlobalHotkeyEnabled { get; init; } = true;

    /// <summary>The shortcut, in the text form <see cref="HotkeyGesture"/> parses.</summary>
    public string Hotkey { get; init; } = HotkeyGesture.DefaultText;

    public HotkeyGesture Gesture => HotkeyGesture.TryParse(Hotkey, out var gesture) ? gesture : HotkeyGesture.Default;
}

public sealed record LayoutSettings
{
    public const double MinPanelWidth = 180;
    public const double MaxPanelWidth = 640;

    public double SidebarWidth { get; init; } = 240;

    public double InspectorWidth { get; init; } = 280;

    public double NoteListHeight { get; init; } = 260;

    public bool IsSidebarCollapsed { get; init; }

    public bool IsInspectorCollapsed { get; init; }

    public double WindowWidth { get; init; } = 1280;

    public double WindowHeight { get; init; } = 800;

    public bool IsWindowMaximized { get; init; }
}

/// <summary>
/// User preferences. Immutable: changes are applied by replacing the instance through
/// <see cref="ISettingsService.UpdateAsync"/>. No secrets are ever stored here.
/// </summary>
public sealed record AppSettings
{
    public const int CurrentSchemaVersion = 1;
    public const double DefaultFontSize = 14;
    public const double MinFontSize = 10;
    public const double MaxFontSize = 28;

    public int SchemaVersion { get; init; } = CurrentSchemaVersion;

    public IReadOnlyList<VaultSettings> Vaults { get; init; } = [];

    public string? ActiveVaultId { get; init; }

    public AppTheme Theme { get; init; } = AppTheme.Dark;

    /// <summary>UI culture name ("es", "en"); null follows the operating system.</summary>
    public string? Language { get; init; }

    public double FontSize { get; init; } = DefaultFontSize;

    public UiDensity Density { get; init; } = UiDensity.Comfortable;

    /// <summary>Null follows the operating system preference when it can be detected.</summary>
    public bool? ReduceMotion { get; init; }

    public EditorViewMode ViewMode { get; init; } = EditorViewMode.Split;

    public NoteSortOrder SortOrder { get; init; } = NoteSortOrder.UpdatedDescending;

    public LayoutSettings Layout { get; init; } = new();

    public IReadOnlyList<ProjectSettings> Projects { get; init; } = [];

    public QuickCaptureSettings QuickCapture { get; init; } = new();

    /// <summary>Returns a copy with every value forced into its valid range (settings files can be hand-edited).</summary>
    public AppSettings Normalize() => this with
    {
        SchemaVersion = CurrentSchemaVersion,
        Vaults = Vaults ?? [],
        Theme = Enum.IsDefined(Theme) ? Theme : AppTheme.Dark,
        Density = Enum.IsDefined(Density) ? Density : UiDensity.Comfortable,
        ViewMode = Enum.IsDefined(ViewMode) ? ViewMode : EditorViewMode.Split,
        SortOrder = Enum.IsDefined(SortOrder) ? SortOrder : NoteSortOrder.UpdatedDescending,
        FontSize = ClampOrDefault(FontSize, MinFontSize, MaxFontSize, DefaultFontSize),
        Layout = NormalizeLayout(Layout ?? new LayoutSettings()),
        Projects = NormalizeProjects(Projects ?? []),
        QuickCapture = NormalizeQuickCapture(QuickCapture ?? new QuickCaptureSettings()),
    };

    /// <summary>The settings of a project, by name (case and accents ignored), or null.</summary>
    public ProjectSettings? FindProject(string? name) =>
        string.IsNullOrWhiteSpace(name)
            ? null
            : Projects.FirstOrDefault(project => TextKey.Of(project.Name) == TextKey.Of(name.Trim()));

    /// <summary>Returns a copy where the project has the given repository path (added when new, removed when the path is null).</summary>
    public AppSettings WithProjectRepository(string name, string? repositoryPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        var key = TextKey.Of(name.Trim());
        var remaining = Projects.Where(project => TextKey.Of(project.Name) != key).ToList();
        if (!string.IsNullOrWhiteSpace(repositoryPath))
        {
            remaining.Add(new ProjectSettings(name.Trim(), repositoryPath.Trim()));
        }

        return this with { Projects = remaining };
    }

    private static IReadOnlyList<ProjectSettings> NormalizeProjects(IReadOnlyList<ProjectSettings> projects)
    {
        var result = new List<ProjectSettings>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var project in projects)
        {
            if (project is null || !NoteTitle.TryNormalize(project.Name, out var name) || !seen.Add(TextKey.Of(name)))
            {
                continue;
            }

            var path = project.RepositoryPath?.Trim();
            result.Add(new ProjectSettings(name, string.IsNullOrEmpty(path) || !System.IO.Path.IsPathFullyQualified(path) ? null : path));
        }

        // Unchanged content keeps the same instance: "no effective change" is detected by record equality.
        return result.SequenceEqual(projects) ? projects : result;
    }

    private static QuickCaptureSettings NormalizeQuickCapture(QuickCaptureSettings quickCapture) =>
        quickCapture with
        {
            Hotkey = HotkeyGesture.TryParse(quickCapture.Hotkey, out var gesture) ? gesture.ToString() : HotkeyGesture.DefaultText,
        };

    private static LayoutSettings NormalizeLayout(LayoutSettings layout)
    {
        var defaults = new LayoutSettings();
        return layout with
        {
            SidebarWidth = ClampOrDefault(layout.SidebarWidth, LayoutSettings.MinPanelWidth, LayoutSettings.MaxPanelWidth, defaults.SidebarWidth),
            InspectorWidth = ClampOrDefault(layout.InspectorWidth, LayoutSettings.MinPanelWidth, LayoutSettings.MaxPanelWidth, defaults.InspectorWidth),
            NoteListHeight = ClampOrDefault(layout.NoteListHeight, 96, 2000, defaults.NoteListHeight),
            WindowWidth = ClampOrDefault(layout.WindowWidth, 720, 10_000, defaults.WindowWidth),
            WindowHeight = ClampOrDefault(layout.WindowHeight, 480, 10_000, defaults.WindowHeight),
        };
    }

    private static double ClampOrDefault(double value, double min, double max, double fallback) =>
        double.IsFinite(value) ? Math.Clamp(value, min, max) : fallback;
}
