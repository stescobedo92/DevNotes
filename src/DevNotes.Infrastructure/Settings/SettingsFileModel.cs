using System.Text.Json;
using System.Text.Json.Serialization;
using DevNotes.Application.Search;
using DevNotes.Application.Settings;

namespace DevNotes.Infrastructure.Settings;

/// <summary>
/// Shape of <c>settings.json</c>. It is a separate, mutable model on purpose: the JSON source
/// generator only keeps property defaults for keys missing from the file when it can call a
/// parameterless constructor and plain setters, which immutable <c>init</c> records do not allow.
/// A key that is absent (older file, hand-edited file) therefore keeps its default value.
/// </summary>
internal sealed class SettingsFileModel
{
    public int SchemaVersion { get; set; } = AppSettings.CurrentSchemaVersion;

    public List<VaultFileModel?>? Vaults { get; set; }

    public string? ActiveVaultId { get; set; }

    public AppTheme Theme { get; set; } = AppTheme.Dark;

    public string? Language { get; set; }

    public double FontSize { get; set; } = AppSettings.DefaultFontSize;

    public UiDensity Density { get; set; } = UiDensity.Comfortable;

    public bool? ReduceMotion { get; set; }

    public EditorViewMode ViewMode { get; set; } = EditorViewMode.Split;

    public NoteSortOrder SortOrder { get; set; } = NoteSortOrder.UpdatedDescending;

    public LayoutFileModel? Layout { get; set; }

    public List<ProjectFileModel?>? Projects { get; set; }

    public QuickCaptureFileModel? QuickCapture { get; set; }

    public static SettingsFileModel From(AppSettings settings) => new()
    {
        SchemaVersion = settings.SchemaVersion,
        Vaults = [.. settings.Vaults.Select(vault => new VaultFileModel { Id = vault.Id, Name = vault.Name, Path = vault.Path })],
        ActiveVaultId = settings.ActiveVaultId,
        Theme = settings.Theme,
        Language = settings.Language,
        FontSize = settings.FontSize,
        Density = settings.Density,
        ReduceMotion = settings.ReduceMotion,
        ViewMode = settings.ViewMode,
        SortOrder = settings.SortOrder,
        Layout = LayoutFileModel.From(settings.Layout),
        Projects = [.. settings.Projects.Select(project => new ProjectFileModel { Name = project.Name, RepositoryPath = project.RepositoryPath })],
        QuickCapture = new QuickCaptureFileModel
        {
            GlobalHotkeyEnabled = settings.QuickCapture.GlobalHotkeyEnabled,
            Hotkey = settings.QuickCapture.Hotkey,
        },
    };

    public AppSettings ToSettings() => new()
    {
        SchemaVersion = SchemaVersion,
        Vaults =
        [
            .. (Vaults ?? [])
                .Where(vault => vault is { Id: not null, Name: not null, Path: not null })
                .Select(vault => new VaultSettings(vault!.Id!, vault.Name!, vault.Path!)),
        ],
        ActiveVaultId = ActiveVaultId,
        Theme = Theme,
        Language = Language,
        FontSize = FontSize,
        Density = Density,
        ReduceMotion = ReduceMotion,
        ViewMode = ViewMode,
        SortOrder = SortOrder,
        Layout = (Layout ?? new LayoutFileModel()).ToLayout(),
        Projects =
        [
            .. (Projects ?? [])
                .Where(project => project is { Name: not null })
                .Select(project => new ProjectSettings(project!.Name!, project.RepositoryPath)),
        ],
        QuickCapture = new QuickCaptureSettings
        {
            GlobalHotkeyEnabled = QuickCapture?.GlobalHotkeyEnabled ?? true,
            Hotkey = QuickCapture?.Hotkey ?? HotkeyGesture.DefaultText,
        },
    };
}

internal sealed class ProjectFileModel
{
    public string? Name { get; set; }

    public string? RepositoryPath { get; set; }
}

internal sealed class QuickCaptureFileModel
{
    public bool GlobalHotkeyEnabled { get; set; } = true;

    public string? Hotkey { get; set; } = HotkeyGesture.DefaultText;
}

internal sealed class VaultFileModel
{
    public string? Id { get; set; }

    public string? Name { get; set; }

    public string? Path { get; set; }
}

internal sealed class LayoutFileModel
{
    private static readonly LayoutSettings _defaults = new();

    public double SidebarWidth { get; set; } = _defaults.SidebarWidth;

    public double InspectorWidth { get; set; } = _defaults.InspectorWidth;

    public double NoteListHeight { get; set; } = _defaults.NoteListHeight;

    public bool IsSidebarCollapsed { get; set; }

    public bool IsInspectorCollapsed { get; set; }

    public double WindowWidth { get; set; } = _defaults.WindowWidth;

    public double WindowHeight { get; set; } = _defaults.WindowHeight;

    public bool IsWindowMaximized { get; set; }

    public static LayoutFileModel From(LayoutSettings layout) => new()
    {
        SidebarWidth = layout.SidebarWidth,
        InspectorWidth = layout.InspectorWidth,
        NoteListHeight = layout.NoteListHeight,
        IsSidebarCollapsed = layout.IsSidebarCollapsed,
        IsInspectorCollapsed = layout.IsInspectorCollapsed,
        WindowWidth = layout.WindowWidth,
        WindowHeight = layout.WindowHeight,
        IsWindowMaximized = layout.IsWindowMaximized,
    };

    public LayoutSettings ToLayout() => new()
    {
        SidebarWidth = SidebarWidth,
        InspectorWidth = InspectorWidth,
        NoteListHeight = NoteListHeight,
        IsSidebarCollapsed = IsSidebarCollapsed,
        IsInspectorCollapsed = IsInspectorCollapsed,
        WindowWidth = WindowWidth,
        WindowHeight = WindowHeight,
        IsWindowMaximized = IsWindowMaximized,
    };
}

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    WriteIndented = true,
    UseStringEnumConverter = true,
    ReadCommentHandling = JsonCommentHandling.Skip,
    AllowTrailingCommas = true,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(SettingsFileModel))]
internal sealed partial class SettingsJsonContext : JsonSerializerContext;
