using System.ComponentModel;
using System.Data.Common;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DevNotes.Application.Indexing;
using DevNotes.Application.Notes;
using DevNotes.Application.Settings;
using DevNotes.Application.Vaults;
using DevNotes.Desktop.Resources;
using DevNotes.Desktop.Services;
using DevNotes.Desktop.ViewModels.Dialogs;
using DevNotes.Domain.Notes;
using DevNotes.Domain.Vaults;

namespace DevNotes.Desktop.ViewModels;

public sealed partial class VaultItemViewModel(Vault vault) : ObservableObject
{
    public Vault Vault { get; } = vault ?? throw new ArgumentNullException(nameof(vault));

    public string Name => Vault.Name;

    public string Path => Vault.RootPath;

    [ObservableProperty]
    public partial bool IsActive { get; set; }
}

/// <summary>Window layout values the view reports back when it closes.</summary>
public sealed record WindowLayout(
    double SidebarWidth,
    double InspectorWidth,
    double NoteListHeight,
    double WindowWidth,
    double WindowHeight,
    bool IsMaximized);

/// <summary>
/// The shell: owns the open vault and coordinates the note list, the editor, the overlays and
/// every command of the app. All members must be used from the UI thread.
/// </summary>
public sealed partial class MainWindowViewModel : ObservableObject
{
    private const double FontSizeStep = 1;

    private readonly IVaultRegistry _registry;
    private readonly IVaultSessionManager _sessions;
    private readonly ISettingsService _settings;
    private readonly IThemeService _theme;
    private readonly IUiDispatcher _dispatcher;
    private readonly IFolderPicker _folderPicker;
    private IVaultSession? _session;

    public MainWindowViewModel(
        IVaultRegistry registry,
        IVaultSessionManager sessions,
        ISettingsService settings,
        IThemeService theme,
        IUiDispatcher dispatcher,
        IFolderPicker folderPicker,
        DialogHostViewModel dialogs,
        NotificationViewModel notification,
        NoteListViewModel noteList,
        NoteEditorViewModel editor,
        QuickOpenViewModel quickOpen)
    {
        _registry = registry ?? throw new ArgumentNullException(nameof(registry));
        _sessions = sessions ?? throw new ArgumentNullException(nameof(sessions));
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _theme = theme ?? throw new ArgumentNullException(nameof(theme));
        _dispatcher = dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));
        _folderPicker = folderPicker ?? throw new ArgumentNullException(nameof(folderPicker));
        Dialogs = dialogs ?? throw new ArgumentNullException(nameof(dialogs));
        Notification = notification ?? throw new ArgumentNullException(nameof(notification));
        NoteList = noteList ?? throw new ArgumentNullException(nameof(noteList));
        Editor = editor ?? throw new ArgumentNullException(nameof(editor));
        QuickOpen = quickOpen ?? throw new ArgumentNullException(nameof(quickOpen));

        Vaults = [];
        VaultName = Strings.Status_NoVault;
        NoteCountText = string.Empty;
        IndexStatusText = string.Empty;
        IsMotionEnabled = theme.IsMotionEnabled;
        Layout = new LayoutSettings();

        NoteList.OpenRequested = OpenNoteAsync;
        QuickOpen.OpenNoteRequested = OpenNoteAndFocusAsync;
        NoteList.PropertyChanged += OnNoteListPropertyChanged;
        Editor.PropertyChanged += OnEditorPropertyChanged;
        Editor.NoteChanged += OnEditorNoteChanged;
        _theme.AppearanceChanged += (_, _) => IsMotionEnabled = _theme.IsMotionEnabled;

        Commands = CreateCommands();
        QuickOpen.SetCommands(Commands);
        NoteList.EmptyVaultHint = EmptyVaultHint;
    }

    public DialogHostViewModel Dialogs { get; }

    public NotificationViewModel Notification { get; }

    public NoteListViewModel NoteList { get; }

    public NoteEditorViewModel Editor { get; }

    public QuickOpenViewModel QuickOpen { get; }

    /// <summary>Every action of the app, with its shortcut. The window registers the key bindings from this list.</summary>
    public IReadOnlyList<AppCommand> Commands { get; }

    /// <summary>Layout restored from the settings; the view applies it once after loading.</summary>
    public LayoutSettings Layout { get; private set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasVaults), nameof(ShowWelcome))]
    public partial IReadOnlyList<VaultItemViewModel> Vaults { get; private set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasVault))]
    public partial VaultItemViewModel? ActiveVault { get; private set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowWelcome))]
    public partial bool IsInitialized { get; private set; }

    [ObservableProperty]
    public partial bool IsSidebarCollapsed { get; set; }

    [ObservableProperty]
    public partial bool IsInspectorCollapsed { get; set; }

    [ObservableProperty]
    public partial bool IsMotionEnabled { get; private set; }

    [ObservableProperty]
    public partial string VaultName { get; private set; }

    [ObservableProperty]
    public partial string NoteCountText { get; private set; }

    [ObservableProperty]
    public partial string IndexStatusText { get; private set; }

    [ObservableProperty]
    public partial bool IsIndexing { get; private set; }

    [ObservableProperty]
    public partial bool HasIndexError { get; private set; }

    [ObservableProperty]
    public partial double IndexProgress { get; private set; }

    [ObservableProperty]
    public partial bool IsIndexProgressIndeterminate { get; private set; }

    /// <summary>A vault is open.</summary>
    public bool HasVault => ActiveVault is not null;

    /// <summary>
    /// At least one vault is registered. The panels are shown as soon as this is true, even when the
    /// active vault could not be opened (an unplugged drive): the sidebar is where the user switches
    /// to another vault or removes the broken one.
    /// </summary>
    public bool HasVaults => Vaults.Count > 0;

    /// <summary>First-run (or no vault registered) empty state.</summary>
    public bool ShowWelcome => IsInitialized && !HasVaults;

    /// <summary>Raised when the view should move keyboard focus to the search box of the note list.</summary>
    public event EventHandler? ListSearchFocusRequested;

    public string NewNoteShortcut => ShortcutOf("note.new");

    public string SearchShortcut => ShortcutOf("search.open");

    public string EmptyVaultHint => ErrorMessages.Format(Strings.Empty_NoNotes_Hint, NewNoteShortcut);

    public string NoNoteOpenHint => ErrorMessages.Format(Strings.Empty_NoNoteOpen_Hint, SearchShortcut);

    /// <summary>
    /// Applies the (already loaded) settings and opens the last vault. The settings are loaded by the
    /// composition root before any window exists because the UI language depends on them.
    /// </summary>
    public async Task InitializeAsync()
    {
        var settings = _settings.Current;
        _theme.Apply(settings);
        Layout = settings.Layout;
        IsSidebarCollapsed = settings.Layout.IsSidebarCollapsed;
        IsInspectorCollapsed = settings.Layout.IsInspectorCollapsed;
        Editor.ViewMode = settings.ViewMode;
        NoteList.SortOrder = settings.SortOrder;

        RefreshVaults();
        if (_registry.ActiveVault is { } vault)
        {
            await OpenVaultAsync(vault);
        }

        IsInitialized = true;
    }

    /// <summary>
    /// Saves what is pending and releases the vault. When the open note cannot be saved the user
    /// decides: stay (returns false) or close anyway discarding those changes.
    /// </summary>
    public async Task<bool> ShutdownAsync(WindowLayout? layout)
    {
        // The window stays on screen while settings are saved and the vault is released: nothing typed
        // after the last save may be accepted, because nobody would save it. Staying re-enables editing.
        using var editing = Editor.SuspendEditing();
        if (!await Editor.FlushAsync())
        {
            var discard = await Dialogs.ConfirmAsync(
                Strings.Dialog_CloseUnsaved_Title,
                Strings.Dialog_CloseUnsaved_Message,
                Strings.Dialog_CloseUnsaved_Confirm,
                isDestructive: true);
            if (!discard)
            {
                return false;
            }

            Editor.CloseWithoutSaving();
        }

        if (layout is not null)
        {
            await PersistAsync(settings => settings with
            {
                Layout = settings.Layout with
                {
                    SidebarWidth = layout.SidebarWidth,
                    InspectorWidth = layout.InspectorWidth,
                    NoteListHeight = layout.NoteListHeight,
                    WindowWidth = layout.WindowWidth,
                    WindowHeight = layout.WindowHeight,
                    IsWindowMaximized = layout.IsMaximized,
                    IsSidebarCollapsed = IsSidebarCollapsed,
                    IsInspectorCollapsed = IsInspectorCollapsed,
                },
            });
        }

        DetachSession();

        // The notes are safe on disk at this point; a failure to release the index must not keep the window open.
        await GuardAsync(_sessions.CloseAsync);
        return true;
    }

    private IReadOnlyList<AppCommand> CreateCommands() =>
    [
        new("note.new", Strings.Command_NewNote, NewNoteCommand, new ShortcutKey("N", Primary: true)),
        new("note.save", Strings.Command_Save, SaveCommand, new ShortcutKey("S", Primary: true)),
        new("search.open", Strings.Command_Search, SearchCommand, new ShortcutKey("K", Primary: true)),
        new("note.switch", Strings.Command_SwitchNote, SwitchNoteCommand, new ShortcutKey("P", Primary: true)),
        new("palette.open", Strings.Command_Palette, OpenCommandPaletteCommand, new ShortcutKey("P", Primary: true, Shift: true)),
        new("view.sidebar", Strings.Command_ToggleSidebar, ToggleSidebarCommand, new ShortcutKey("B", Primary: true)),
        new("view.inspector", Strings.Command_ToggleInspector, ToggleInspectorCommand, new ShortcutKey("I", Primary: true, Shift: true)),
        new("view.mode", Strings.Command_ToggleViewMode, ToggleViewModeCommand, new ShortcutKey("E", Primary: true)),
        new("editor.focus", Strings.Command_FocusEditor, FocusEditorCommand),
        new("list.focus", Strings.Command_FocusList, FocusListCommand, new ShortcutKey("F", Primary: true, Shift: true)),
        new("note.rename", Strings.Command_Rename, RenameCommand, new ShortcutKey("F2")),
        new("note.move", Strings.Command_Move, MoveCommand),
        new("note.delete", Strings.Command_Delete, DeleteCommand),
        new("trash.open", Strings.Command_OpenTrash, OpenTrashCommand),
        new("vault.add", Strings.Command_AddVault, AddVaultCommand),
        new("index.rebuild", Strings.Command_Reindex, ReindexCommand),
        new("theme.dark", Strings.Command_ThemeDark, SetThemeDarkCommand),
        new("theme.light", Strings.Command_ThemeLight, SetThemeLightCommand),
        new("theme.system", Strings.Command_ThemeSystem, SetThemeSystemCommand),
        new("zoom.in", Strings.Command_ZoomIn, ZoomInCommand, new ShortcutKey("OemPlus", Primary: true)),
        new("zoom.out", Strings.Command_ZoomOut, ZoomOutCommand, new ShortcutKey("OemMinus", Primary: true)),
        new("zoom.reset", Strings.Command_ZoomReset, ZoomResetCommand, new ShortcutKey("D0", Primary: true)),
        new("density.compact", Strings.Command_DensityCompact, SetDensityCompactCommand),
        new("density.comfortable", Strings.Command_DensityComfortable, SetDensityComfortableCommand),
    ];

    // ----- Vaults -----------------------------------------------------------------------------

    [RelayCommand]
    private Task AddVaultAsync() => RunAsync(async () =>
    {
        var folder = await _folderPicker.PickFolderAsync(Strings.Welcome_ChooseFolder);
        if (folder is null || !await FlushOrWarnAsync())
        {
            return;
        }

        var vault = await _registry.AddAsync(folder, CancellationToken.None);
        RefreshVaults();
        await OpenVaultAsync(vault);
    });

    [RelayCommand]
    private Task SwitchVaultAsync(VaultItemViewModel? item) => RunAsync(async () =>
    {
        if (item is null || item.Vault.Id == _session?.Vault.Id)
        {
            return;
        }

        if (!await Editor.FlushAsync())
        {
            Notification.Show(Strings.Error_UnsavedChanges, NotificationKind.Error);
            return;
        }

        await _registry.SetActiveAsync(item.Vault.Id, CancellationToken.None);
        await OpenVaultAsync(item.Vault);
    });

    [RelayCommand]
    private Task RemoveVaultAsync(VaultItemViewModel? item) => RunAsync(async () =>
    {
        if (item is null)
        {
            return;
        }

        var confirmed = await Dialogs.ConfirmAsync(
            Strings.Dialog_RemoveVault_Title,
            ErrorMessages.Format(Strings.Dialog_RemoveVault_Message, item.Name),
            Strings.Action_Remove,
            isDestructive: true);
        if (!confirmed)
        {
            return;
        }

        var wasOpen = item.Vault.Id == _session?.Vault.Id;
        if (wasOpen)
        {
            if (!await CloseVaultAsync())
            {
                return;
            }
        }

        await _registry.RemoveAsync(item.Vault.Id, CancellationToken.None);
        RefreshVaults();
        if (wasOpen && _registry.ActiveVault is { } next)
        {
            await OpenVaultAsync(next);
        }
    });

    [RelayCommand(CanExecute = nameof(HasVault))]
    private void Reindex()
    {
        if (_session is { } session)
        {
            session.RequestRebuild();
            Notification.Show(Strings.Notify_Reindexing);
        }
    }

    private async Task OpenVaultAsync(Vault vault)
    {
        if (!await CloseVaultAsync())
        {
            return;
        }

        try
        {
            var session = await _sessions.OpenAsync(vault, CancellationToken.None);
            _session = session;
            session.Events.NotesChanged += OnNotesChanged;
            session.Events.IndexStatusChanged += OnIndexStatusChanged;

            await Editor.SetSessionAsync(session);
            QuickOpen.SetSession(session);
            RefreshVaults();
            VaultName = vault.Name;
            ApplyIndexStatus(session.Events.Status);
            await NoteList.SetSessionAsync(session);
            await UpdateNoteCountAsync();
        }
        catch (Exception exception) when (exception is DbException || ErrorMessages.IsExpected(exception))
        {
            await CloseVaultAsync();
            Notification.Show(ErrorMessages.Format(Strings.Error_OpenVault, ErrorMessages.Describe(exception)), NotificationKind.Error);
        }

        NotifyCommandStates();
    }

    /// <summary>
    /// Closes the open vault. Returns false, leaving everything as it was, when the open note has
    /// changes that cannot be saved: no flow may drop that text without the user deciding.
    /// </summary>
    private async Task<bool> CloseVaultAsync()
    {
        if (!await Editor.SetSessionAsync(null))
        {
            Notification.Show(Strings.Error_UnsavedChanges, NotificationKind.Error);
            return false;
        }

        DetachSession();
        QuickOpen.SetSession(null);
        await NoteList.SetSessionAsync(null);
        await _sessions.CloseAsync();

        VaultName = Strings.Status_NoVault;
        NoteCountText = string.Empty;
        IndexStatusText = string.Empty;
        IsIndexing = false;
        HasIndexError = false;
        RefreshVaults();
        NotifyCommandStates();
        return true;
    }

    private void DetachSession()
    {
        if (_session is { } session)
        {
            session.Events.NotesChanged -= OnNotesChanged;
            session.Events.IndexStatusChanged -= OnIndexStatusChanged;
            _session = null;
        }
    }

    private void RefreshVaults()
    {
        var openId = _session?.Vault.Id;
        var items = _registry.Vaults.Select(vault => new VaultItemViewModel(vault) { IsActive = vault.Id == openId }).ToList();
        Vaults = items;
        ActiveVault = items.FirstOrDefault(item => item.IsActive);
    }

    // ----- Notes ------------------------------------------------------------------------------

    [RelayCommand(CanExecute = nameof(HasVault))]
    private Task NewNoteAsync() => RunAsync(async () =>
    {
        if (_session is not { } session)
        {
            return;
        }

        var title = await Dialogs.PromptAsync(
            Strings.Dialog_NewNote_Title,
            Strings.Dialog_NewNote_Label,
            string.Empty,
            Strings.Action_Create,
            ValidateTitle);
        if (title is null)
        {
            return;
        }

        // New notes are created next to the note that is open, which is where the user is working.
        var created = await session.Notes.CreateAsync(new NewNoteRequest(title, Editor.Path?.Directory), CancellationToken.None);
        if (await Editor.OpenAsync(created.Path))
        {
            Editor.RequestFocus();
        }
    });

    [RelayCommand(CanExecute = nameof(HasOpenNote))]
    private Task SaveAsync() => RunAsync(() => Editor.FlushAsync());

    [RelayCommand(CanExecute = nameof(HasOpenNote))]
    private Task RenameAsync() => RunAsync(async () =>
    {
        if (_session is not { } session || Editor.Path is not { } path)
        {
            return;
        }

        var title = await Dialogs.PromptAsync(
            Strings.Dialog_Rename_Title,
            Strings.Dialog_Rename_Label,
            Editor.Title,
            Strings.Action_Rename,
            ValidateTitle);
        if (title is null)
        {
            return;
        }

        // Read-only until the note is open under its new name: text typed in between would be saved
        // to the old path and resurrect the file that was just renamed.
        using var editing = Editor.SuspendEditing();
        if (!await FlushOrWarnAsync())
        {
            return;
        }

        var renamed = await session.Notes.RenameAsync(path, title, CancellationToken.None);
        await Editor.OpenAsync(renamed, reload: true);
    });

    [RelayCommand(CanExecute = nameof(HasOpenNote))]
    private Task MoveAsync() => RunAsync(async () =>
    {
        if (_session is not { } session || Editor.Path is not { } path)
        {
            return;
        }

        var folders = await session.Notes.ListFoldersAsync(CancellationToken.None);
        var folder = await Dialogs.PromptAsync(
            Strings.Dialog_Move_Title,
            Strings.Dialog_Move_Label,
            path.Directory,
            Strings.Action_Move,
            value => NotePath.TryCombine(value, path.FileNameWithoutExtension, out var target, out _) && !target.IsHidden
                ? null
                : Strings.Error_InvalidFolder,
            folders);
        if (folder is null)
        {
            return;
        }

        using var editing = Editor.SuspendEditing();
        if (!await FlushOrWarnAsync())
        {
            return;
        }

        var moved = await session.Notes.MoveAsync(path, folder, CancellationToken.None);
        await Editor.OpenAsync(moved, reload: true);
    });

    [RelayCommand(CanExecute = nameof(HasOpenNote))]
    private Task DeleteAsync() => RunAsync(async () =>
    {
        if (_session is not { } session || Editor.Path is not { } path)
        {
            return;
        }

        var confirmed = await Dialogs.ConfirmAsync(
            Strings.Dialog_Delete_Title,
            ErrorMessages.Format(Strings.Dialog_Delete_Message, Editor.Title),
            Strings.Action_Delete,
            isDestructive: true);
        if (!confirmed)
        {
            return;
        }

        // Save first so the trash holds the latest text. If that is not possible (a conflict, a failed
        // write) the note is not deleted: the trash would get the version on disk and the text in the
        // editor, which is the one the user sees, could never be restored.
        using var editing = Editor.SuspendEditing();
        if (!await FlushOrWarnAsync())
        {
            return;
        }

        // The editor is only closed once the note really is in the trash.
        await session.Notes.DeleteAsync(path, CancellationToken.None);
        Editor.CloseWithoutSaving();
        Notification.Show(Strings.Notify_MovedToTrash);
    });

    [RelayCommand(CanExecute = nameof(HasVault))]
    private Task OpenTrashAsync() => RunAsync(async () =>
    {
        if (_session is not { } session)
        {
            return;
        }

        var entries = await session.Notes.ListTrashAsync(CancellationToken.None);
        var dialog = new TrashDialogViewModel(
            entries,
            restore: item => GuardAsync(async () =>
            {
                await session.Notes.RestoreAsync(item.Entry.Id, CancellationToken.None);
                Notification.Show(Strings.Notify_Restored);
            }),
            deleteForever: async item =>
            {
                var confirmed = await Dialogs.ConfirmAsync(
                    Strings.Dialog_DeleteForever_Title,
                    ErrorMessages.Format(Strings.Dialog_DeleteForever_Message, item.Name),
                    Strings.Dialog_Trash_DeleteForever,
                    isDestructive: true);
                return confirmed && await GuardAsync(() => session.Notes.DeleteFromTrashAsync(item.Entry.Id, CancellationToken.None));
            },
            emptyAll: async count =>
            {
                var confirmed = await Dialogs.ConfirmAsync(
                    Strings.Dialog_EmptyTrash_Title,
                    ErrorMessages.Format(Strings.Dialog_EmptyTrash_Message, count),
                    Strings.Dialog_Trash_EmptyAll,
                    isDestructive: true);
                return confirmed && await GuardAsync(() => session.Notes.EmptyTrashAsync(CancellationToken.None));
            });

        await Dialogs.ShowAsync(dialog);
    });

    private bool HasOpenNote() => Editor.HasNote;

    private Task<bool> OpenNoteAsync(NotePath path) => Editor.OpenAsync(path);

    private async Task<bool> OpenNoteAndFocusAsync(NotePath path)
    {
        var opened = await Editor.OpenAsync(path);
        if (opened)
        {
            Editor.RequestFocus();
        }

        return opened;
    }

    private async Task<bool> FlushOrWarnAsync()
    {
        if (await Editor.FlushAsync())
        {
            return true;
        }

        Notification.Show(Strings.Error_UnsavedChanges, NotificationKind.Error);
        return false;
    }

    private string ShortcutOf(string commandId) =>
        Commands.FirstOrDefault(command => command.Id == commandId)?.ShortcutText ?? string.Empty;

    private static string? ValidateTitle(string value) =>
        NoteTitle.TryNormalize(value, out _) ? null : ErrorMessages.Format(Strings.Error_TitleRequired, NoteTitle.MaxLength);

    // ----- Navigation and view ----------------------------------------------------------------

    [RelayCommand(CanExecute = nameof(HasVault))]
    private void Search() => QuickOpen.Open(QuickOpenMode.Notes);

    [RelayCommand(CanExecute = nameof(HasVault))]
    private void SwitchNote() => QuickOpen.Open(QuickOpenMode.Notes);

    [RelayCommand]
    private void OpenCommandPalette() => QuickOpen.Open(QuickOpenMode.Commands);

    [RelayCommand(CanExecute = nameof(HasOpenNote))]
    private void FocusEditor() => Editor.RequestFocus();

    [RelayCommand(CanExecute = nameof(HasVault))]
    private void FocusList() => ListSearchFocusRequested?.Invoke(this, EventArgs.Empty);

    // Panel and appearance commands allow concurrent executions: each one waits for the settings to
    // be saved, and a command that is still saving would otherwise be disabled and swallow the next
    // key press (toggling twice quickly, key repeat on zoom). The settings service serializes the writes.
    [RelayCommand(AllowConcurrentExecutions = true)]
    private Task ToggleSidebarAsync()
    {
        IsSidebarCollapsed = !IsSidebarCollapsed;
        return PersistAsync(settings => settings with { Layout = settings.Layout with { IsSidebarCollapsed = IsSidebarCollapsed } });
    }

    [RelayCommand(AllowConcurrentExecutions = true)]
    private Task ToggleInspectorAsync()
    {
        IsInspectorCollapsed = !IsInspectorCollapsed;
        return PersistAsync(settings => settings with { Layout = settings.Layout with { IsInspectorCollapsed = IsInspectorCollapsed } });
    }

    [RelayCommand]
    private void ToggleViewMode() => Editor.CycleViewModeCommand.Execute(null);

    [RelayCommand(AllowConcurrentExecutions = true)]
    private Task SetThemeDarkAsync() => ChangeAppearanceAsync(settings => settings with { Theme = AppTheme.Dark });

    [RelayCommand(AllowConcurrentExecutions = true)]
    private Task SetThemeLightAsync() => ChangeAppearanceAsync(settings => settings with { Theme = AppTheme.Light });

    [RelayCommand(AllowConcurrentExecutions = true)]
    private Task SetThemeSystemAsync() => ChangeAppearanceAsync(settings => settings with { Theme = AppTheme.System });

    [RelayCommand(AllowConcurrentExecutions = true)]
    private Task ZoomInAsync() => ChangeAppearanceAsync(settings => settings with { FontSize = settings.FontSize + FontSizeStep });

    [RelayCommand(AllowConcurrentExecutions = true)]
    private Task ZoomOutAsync() => ChangeAppearanceAsync(settings => settings with { FontSize = settings.FontSize - FontSizeStep });

    [RelayCommand(AllowConcurrentExecutions = true)]
    private Task ZoomResetAsync() => ChangeAppearanceAsync(settings => settings with { FontSize = AppSettings.DefaultFontSize });

    [RelayCommand(AllowConcurrentExecutions = true)]
    private Task SetDensityCompactAsync() => ChangeAppearanceAsync(settings => settings with { Density = UiDensity.Compact });

    [RelayCommand(AllowConcurrentExecutions = true)]
    private Task SetDensityComfortableAsync() => ChangeAppearanceAsync(settings => settings with { Density = UiDensity.Comfortable });

    private async Task ChangeAppearanceAsync(Func<AppSettings, AppSettings> update)
    {
        await PersistAsync(update);
        _theme.Apply(_settings.Current);
    }

    // ----- Reactions --------------------------------------------------------------------------

    private void OnNotesChanged(object? sender, NotesChangedEventArgs e) =>
        _dispatcher.Post(() => _ = HandleNotesChangedAsync(sender, e));

    private async Task HandleNotesChangedAsync(object? sender, NotesChangedEventArgs change)
    {
        if (!ReferenceEquals(sender, _session?.Events))
        {
            return; // Late notification from a vault that has been closed.
        }

        await GuardAsync(async () =>
        {
            await NoteList.RefreshAsync();
            await Editor.HandleNotesChangedAsync(change);
            await UpdateNoteCountAsync();
        });
    }

    private void OnIndexStatusChanged(object? sender, IndexStatus status) =>
        _dispatcher.Post(() =>
        {
            if (ReferenceEquals(sender, _session?.Events))
            {
                ApplyIndexStatus(status);
            }
        });

    private void ApplyIndexStatus(IndexStatus status)
    {
        IsIndexing = status.State == IndexState.Indexing;
        HasIndexError = status.State == IndexState.Failed;
        IsIndexProgressIndeterminate = status.Progress.Total <= 0;
        IndexProgress = status.Progress.Total > 0 ? (double)status.Progress.Processed / status.Progress.Total : 0;
        IndexStatusText = status.State switch
        {
            IndexState.Indexing when status.Progress.Total > 0 =>
                ErrorMessages.Format(Strings.Status_IndexingProgress, status.Progress.Processed, status.Progress.Total),
            IndexState.Indexing => Strings.Status_Indexing,
            IndexState.Failed => ErrorMessages.Format(Strings.Status_IndexFailed, status.Error),
            _ => Strings.Status_IndexReady,
        };
    }

    private async Task UpdateNoteCountAsync()
    {
        if (_session is { } session)
        {
            var count = await session.Queries.CountAsync(CancellationToken.None);
            if (ReferenceEquals(session, _session))
            {
                NoteCountText = ErrorMessages.Format(Strings.Status_Notes, count);
            }
        }
    }

    private void OnNoteListPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(NoteListViewModel.SortOrder) && IsInitialized)
        {
            _ = PersistAsync(settings => settings with { SortOrder = NoteList.SortOrder });
        }
    }

    private void OnEditorPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(NoteEditorViewModel.ViewMode) && IsInitialized)
        {
            _ = PersistAsync(settings => settings with { ViewMode = Editor.ViewMode });
        }
    }

    private void OnEditorNoteChanged(object? sender, EventArgs e)
    {
        NoteList.SetActiveNote(Editor.Path);
        NotifyCommandStates();
    }

    private void NotifyCommandStates()
    {
        NewNoteCommand.NotifyCanExecuteChanged();
        SaveCommand.NotifyCanExecuteChanged();
        RenameCommand.NotifyCanExecuteChanged();
        MoveCommand.NotifyCanExecuteChanged();
        DeleteCommand.NotifyCanExecuteChanged();
        OpenTrashCommand.NotifyCanExecuteChanged();
        ReindexCommand.NotifyCanExecuteChanged();
        SearchCommand.NotifyCanExecuteChanged();
        SwitchNoteCommand.NotifyCanExecuteChanged();
        FocusEditorCommand.NotifyCanExecuteChanged();
        FocusListCommand.NotifyCanExecuteChanged();
    }

    // ----- Helpers ----------------------------------------------------------------------------

    /// <summary>
    /// Runs a user-initiated operation and reports expected failures (I/O, permissions, invalid
    /// input, index errors) as an actionable message. Returns false when it failed. Anything
    /// unexpected is a bug and propagates.
    /// </summary>
    private async Task<bool> GuardAsync(Func<Task> operation)
    {
        try
        {
            await operation();
            return true;
        }
        catch (Exception exception) when (exception is DbException || ErrorMessages.IsExpected(exception))
        {
            Notification.Show(ErrorMessages.Describe(exception), NotificationKind.Error);
            return false;
        }
    }

    /// <summary>Same as <see cref="GuardAsync"/> for callers (commands) that do not need the outcome.</summary>
    private async Task RunAsync(Func<Task> operation) => await GuardAsync(operation);

    private Task PersistAsync(Func<AppSettings, AppSettings> update) =>
        RunAsync(() => _settings.UpdateAsync(update, CancellationToken.None));
}
