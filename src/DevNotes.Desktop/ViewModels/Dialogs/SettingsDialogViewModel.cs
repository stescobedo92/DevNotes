using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DevNotes.Application.Settings;
using DevNotes.Desktop.Resources;
using DevNotes.Desktop.Services;

namespace DevNotes.Desktop.ViewModels.Dialogs;

/// <summary>A labelled value of a settings combo box (the non-generic base lets XAML bind the label).</summary>
public abstract record SettingChoice(string Label);

public sealed record SettingChoice<T>(T Value, string Label) : SettingChoice(Label);

/// <summary>One project row of the settings: the name and its repository folder.</summary>
public sealed partial class ProjectSettingViewModel(string name, string? repositoryPath) : ObservableObject
{
    public string Name { get; } = name;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasRepositoryPath))]
    public partial string? RepositoryPath { get; set; } = repositoryPath;

    public bool HasRepositoryPath => !string.IsNullOrEmpty(RepositoryPath);
}

/// <summary>
/// Appearance, quick capture and project settings. Every change is applied and persisted at once
/// (the rest of the app works the same way), so there is no Save button to forget.
/// </summary>
public sealed partial class SettingsDialogViewModel : DialogViewModel
{
    private readonly ISettingsService _settings;
    private readonly IThemeService _theme;
    private readonly IGlobalHotkeyService _hotkey;
    private readonly IFolderPicker _folders;
    private readonly INotificationService _notifications;
    private readonly IUiDispatcher _dispatcher;
    private readonly bool _loading;

    public SettingsDialogViewModel(
        ISettingsService settings,
        IThemeService theme,
        IGlobalHotkeyService hotkey,
        IFolderPicker folders,
        INotificationService notifications,
        IUiDispatcher dispatcher,
        IReadOnlyList<string> projectNames,
        string inAppCaptureShortcut,
        Action? reindex)
        : base(Strings.Dialog_Settings_Title)
    {
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _theme = theme ?? throw new ArgumentNullException(nameof(theme));
        _hotkey = hotkey ?? throw new ArgumentNullException(nameof(hotkey));
        _folders = folders ?? throw new ArgumentNullException(nameof(folders));
        _notifications = notifications ?? throw new ArgumentNullException(nameof(notifications));
        _dispatcher = dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));
        ArgumentNullException.ThrowIfNull(projectNames);
        InAppCaptureShortcut = inAppCaptureShortcut;
        Reindex = reindex;

        ThemeChoices =
        [
            new SettingChoice<AppTheme>(AppTheme.Dark, Strings.Settings_ThemeDark),
            new SettingChoice<AppTheme>(AppTheme.Light, Strings.Settings_ThemeLight),
            new SettingChoice<AppTheme>(AppTheme.System, Strings.Settings_ThemeSystem),
        ];
        LanguageChoices =
        [
            new SettingChoice<string?>(null, Strings.Settings_LanguageSystem),
            new SettingChoice<string?>("en", Strings.Settings_LanguageEnglish),
            new SettingChoice<string?>("es", Strings.Settings_LanguageSpanish),
        ];
        DensityChoices =
        [
            new SettingChoice<UiDensity>(UiDensity.Comfortable, Strings.Settings_DensityComfortable),
            new SettingChoice<UiDensity>(UiDensity.Compact, Strings.Settings_DensityCompact),
        ];

        var current = settings.Current;
        _loading = true; // Property setters below must not persist what they are being initialized with.
        Theme = ThemeChoices.First(choice => choice.Value == current.Theme);
        Language = LanguageChoices.FirstOrDefault(choice => choice.Value == current.Language) ?? LanguageChoices[0];
        Density = DensityChoices.First(choice => choice.Value == current.Density);
        FontSize = current.FontSize;
        ReduceMotion = current.ReduceMotion ?? false;
        GlobalHotkeyEnabled = current.QuickCapture.GlobalHotkeyEnabled;
        Hotkey = current.QuickCapture.Hotkey;
        HotkeyStatusText = string.Empty;

        // Every project in use, whether or not it has a repository configured yet.
        var names = projectNames
            .Concat(current.Projects.Select(project => project.Name))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(name => name, StringComparer.CurrentCultureIgnoreCase);
        Projects = [.. names.Select(name => new ProjectSettingViewModel(name, current.FindProject(name)?.RepositoryPath))];

        _hotkey.StateChanged += OnHotkeyStateChanged;
        UpdateHotkeyStatus();
        _loading = false;
    }

    public IReadOnlyList<SettingChoice<AppTheme>> ThemeChoices { get; }

    public IReadOnlyList<SettingChoice<string?>> LanguageChoices { get; }

    public IReadOnlyList<SettingChoice<UiDensity>> DensityChoices { get; }

    public IReadOnlyList<ProjectSettingViewModel> Projects { get; }

    public bool HasProjects => Projects.Count > 0;

    public override bool IsWide => true;

    /// <summary>The in-app shortcut, shown when the system-wide one is unavailable.</summary>
    public string InAppCaptureShortcut { get; }

    /// <summary>Rebuilds the index of the open vault; null when no vault is open.</summary>
    public Action? Reindex { get; }

    public bool CanReindex => Reindex is not null;

    [ObservableProperty]
    public partial SettingChoice<AppTheme> Theme { get; set; }

    [ObservableProperty]
    public partial SettingChoice<string?> Language { get; set; }

    [ObservableProperty]
    public partial SettingChoice<UiDensity> Density { get; set; }

    [ObservableProperty]
    public partial double FontSize { get; set; }

    [ObservableProperty]
    public partial bool ReduceMotion { get; set; }

    [ObservableProperty]
    public partial bool GlobalHotkeyEnabled { get; set; }

    [ObservableProperty]
    public partial string Hotkey { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasHotkeyError))]
    public partial string? HotkeyError { get; private set; }

    [ObservableProperty]
    public partial string HotkeyStatusText { get; private set; }

    [ObservableProperty]
    public partial bool CanRequestPermission { get; private set; }

    public bool HasHotkeyError => !string.IsNullOrEmpty(HotkeyError);

    public string RestartHint => Strings.Settings_RestartHint;

    partial void OnThemeChanged(SettingChoice<AppTheme> value) => Persist(settings => settings with { Theme = value.Value }, applyAppearance: true);

    partial void OnLanguageChanged(SettingChoice<string?> value) => Persist(settings => settings with { Language = value.Value }, applyAppearance: false);

    partial void OnDensityChanged(SettingChoice<UiDensity> value) => Persist(settings => settings with { Density = value.Value }, applyAppearance: true);

    partial void OnFontSizeChanged(double value) =>
        Persist(settings => settings with { FontSize = Math.Clamp(value, AppSettings.MinFontSize, AppSettings.MaxFontSize) }, applyAppearance: true);

    partial void OnReduceMotionChanged(bool value) => Persist(settings => settings with { ReduceMotion = value }, applyAppearance: true);

    partial void OnGlobalHotkeyEnabledChanged(bool value) =>
        Persist(settings => settings with { QuickCapture = settings.QuickCapture with { GlobalHotkeyEnabled = value } }, applyAppearance: false);

    partial void OnHotkeyChanged(string value) => HotkeyError = null;

    /// <summary>Validates and stores the shortcut typed by the user (Enter or focus loss).</summary>
    [RelayCommand]
    private void ApplyHotkey()
    {
        if (!HotkeyGesture.TryParse(Hotkey, out var gesture))
        {
            HotkeyError = Strings.Error_Hotkey;
            return;
        }

        HotkeyError = null;
        Hotkey = gesture.ToString();
        Persist(settings => settings with { QuickCapture = settings.QuickCapture with { Hotkey = gesture.ToString() } }, applyAppearance: false);
    }

    [RelayCommand]
    private void RequestPermission() => _hotkey.RequestPermission();

    [RelayCommand]
    private async Task BrowseRepositoryAsync(ProjectSettingViewModel? project)
    {
        if (project is null)
        {
            return;
        }

        var folder = await _folders.PickFolderAsync(Strings.Settings_RepositoryPath);
        if (folder is null)
        {
            return;
        }

        project.RepositoryPath = folder;
        Persist(settings => settings.WithProjectRepository(project.Name, folder), applyAppearance: false);
    }

    [RelayCommand]
    private void ClearRepository(ProjectSettingViewModel? project)
    {
        if (project is null)
        {
            return;
        }

        project.RepositoryPath = null;
        Persist(settings => settings.WithProjectRepository(project.Name, null), applyAppearance: false);
    }

    [RelayCommand(CanExecute = nameof(CanReindex))]
    private void RunReindex() => Reindex?.Invoke();

    protected override void OnCancelled() => _hotkey.StateChanged -= OnHotkeyStateChanged;

    // The hook of macOS and Linux reports its state from its own thread.
    private void OnHotkeyStateChanged(object? sender, EventArgs e) => _dispatcher.Post(UpdateHotkeyStatus);

    private void UpdateHotkeyStatus()
    {
        var state = _hotkey.State;
        HotkeyStatusText = state.Status switch
        {
            HotkeyStatus.Active => Strings.Settings_HotkeyStatus_Active,
            HotkeyStatus.Unsupported => ErrorMessages.Format(Strings.Settings_HotkeyStatus_Unavailable, state.Reason),
            HotkeyStatus.Failed => ErrorMessages.Format(Strings.Settings_HotkeyStatus_Failed, state.Reason),
            _ => Strings.Settings_HotkeyStatus_Disabled,
        };
        CanRequestPermission = _hotkey.CanRequestPermission;
    }

    private void Persist(Func<AppSettings, AppSettings> update, bool applyAppearance)
    {
        if (_loading)
        {
            return;
        }

        _ = PersistAsync(update, applyAppearance);
    }

    private async Task PersistAsync(Func<AppSettings, AppSettings> update, bool applyAppearance)
    {
        try
        {
            await _settings.UpdateAsync(update, CancellationToken.None);
            if (applyAppearance)
            {
                _theme.Apply(_settings.Current);
            }
        }
        catch (Exception exception) when (ErrorMessages.IsExpected(exception))
        {
            _notifications.Show(ErrorMessages.Describe(exception), NotificationKind.Error);
        }
    }
}
