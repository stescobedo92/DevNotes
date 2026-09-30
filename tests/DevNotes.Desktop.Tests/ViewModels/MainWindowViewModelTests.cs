using Avalonia.Headless.XUnit;
using DevNotes.Application.Search;
using DevNotes.Application.Settings;
using DevNotes.Desktop.Resources;
using DevNotes.Desktop.Services;
using DevNotes.Desktop.Tests.Support;
using DevNotes.Desktop.ViewModels;
using DevNotes.Desktop.ViewModels.Dialogs;
using DevNotes.Domain.Notes;
using Microsoft.Extensions.DependencyInjection;

namespace DevNotes.Desktop.Tests.ViewModels;

/// <summary>
/// The shell view model over the real composition (files on disk + SQLite index). Tests run on
/// the headless UI thread so background notifications are marshalled exactly as in the app.
/// </summary>
public sealed class MainWindowViewModelTests
{
    private static readonly NotePath _deadlock = NotePath.Create("bugs/deadlock-inventario.md");
    private static readonly NotePath _plain = NotePath.Create("apuntes.md");

    [AvaloniaFact]
    public async Task FirstRun_ShowsWelcome_AndChoosingAFolderOpensTheVault()
    {
        await using var harness = await DesktopHarness.StartAsync(new AvaloniaUiDispatcher(), openVault: false, seed: SampleVault.Seed);
        var shell = harness.Shell;

        shell.ShowWelcome.Should().BeTrue();
        shell.HasVault.Should().BeFalse();
        shell.VaultName.Should().Be(Strings.Status_NoVault);
        shell.NewNoteCommand.CanExecute(null).Should().BeFalse();
        shell.NoteList.State.Should().Be(NoteListState.NoVault);

        harness.FolderPicker.NextResult = harness.VaultFolder.Path;
        await shell.AddVaultCommand.ExecuteAsync(null);
        await harness.SettleAsync();

        shell.ShowWelcome.Should().BeFalse();
        shell.HasVault.Should().BeTrue();
        shell.ActiveVault!.Path.Should().Be(harness.VaultFolder.Path);
        shell.VaultName.Should().Be(Path.GetFileName(harness.VaultFolder.Path));
        shell.NoteList.Items.Should().HaveCount(3);
        shell.NewNoteCommand.CanExecute(null).Should().BeTrue();
        harness.Settings.Current.Vaults.Should().ContainSingle();
        await UiTest.WaitForAsync(() => shell.NoteCountText == ErrorMessages.Format(Strings.Status_Notes, 3), "the note count");
        shell.IndexStatusText.Should().Be(Strings.Status_IndexReady);
    }

    [AvaloniaFact]
    public async Task AddVault_CancelledPicker_ChangesNothing()
    {
        await using var harness = await DesktopHarness.StartAsync(new AvaloniaUiDispatcher(), openVault: false);
        harness.FolderPicker.NextResult = null;

        await harness.Shell.AddVaultCommand.ExecuteAsync(null);

        harness.FolderPicker.Calls.Should().Be(1);
        harness.Shell.ShowWelcome.Should().BeTrue();
        harness.Settings.Current.Vaults.Should().BeEmpty();
    }

    [AvaloniaFact]
    public async Task AddVault_FolderThatDisappeared_ReportsAnActionableError()
    {
        await using var harness = await DesktopHarness.StartAsync(new AvaloniaUiDispatcher(), openVault: false);
        harness.FolderPicker.NextResult = harness.VaultFolder.Combine("missing");

        await harness.Shell.AddVaultCommand.ExecuteAsync(null);

        harness.Shell.Notification.IsError.Should().BeTrue();
        harness.Shell.Notification.Message.Should().Contain("missing");
        harness.Shell.HasVault.Should().BeFalse();
    }

    [AvaloniaFact]
    public async Task NewNote_ValidatesTheTitle_CreatesTheFile_AndOpensIt()
    {
        await using var harness = await DesktopHarness.StartAsync(new AvaloniaUiDispatcher(), seed: SampleVault.Seed);
        var shell = harness.Shell;

        var command = shell.NewNoteCommand.ExecuteAsync(null);
        var prompt = shell.Dialogs.Current.Should().BeOfType<PromptDialogViewModel>().Subject;
        prompt.Title.Should().Be(Strings.Dialog_NewNote_Title);

        prompt.Value = "   ";
        prompt.ConfirmCommand.Execute(null);
        prompt.HasError.Should().BeTrue();

        prompt.Value = "Índice FTS5 lento";
        prompt.ConfirmCommand.Execute(null);
        await command;

        shell.Editor.Path!.Value.Value.Should().Be("indice-fts5-lento.md");
        shell.Editor.Title.Should().Be("Índice FTS5 lento");
        harness.VaultFolder.Exists("indice-fts5-lento.md").Should().BeTrue();
        await harness.SettleAsync();
        shell.NoteList.Items.Should().HaveCount(4);
        shell.NoteList.SelectedItem!.Path.Should().Be(shell.Editor.Path.Value);
    }

    [AvaloniaFact]
    public async Task NewNote_IsCreatedNextToTheOpenNote_AndCancelCreatesNothing()
    {
        await using var harness = await DesktopHarness.StartAsync(new AvaloniaUiDispatcher(), seed: SampleVault.Seed);
        var shell = harness.Shell;
        await shell.Editor.OpenAsync(_deadlock);

        var cancelled = shell.NewNoteCommand.ExecuteAsync(null);
        shell.Dialogs.CancelCurrent();
        await cancelled;
        shell.Editor.Path.Should().Be(_deadlock);

        var command = shell.NewNoteCommand.ExecuteAsync(null);
        var prompt = (PromptDialogViewModel)shell.Dialogs.Current!;
        prompt.Value = "Otro bug";
        prompt.ConfirmCommand.Execute(null);
        await command;

        shell.Editor.Path!.Value.Value.Should().Be("bugs/otro-bug.md");
    }

    [AvaloniaFact]
    public async Task EditAndSave_WritesTheFile_StampsAnId_AndUpdatesTheIndex()
    {
        await using var harness = await DesktopHarness.StartAsync(new AvaloniaUiDispatcher(), seed: SampleVault.Seed);
        var shell = harness.Shell;
        await shell.Editor.OpenAsync(_plain);

        shell.Editor.Text += "\nPalabraUnica para buscar.\n";
        shell.Editor.State.Should().Be(SaveState.Modified);
        await shell.SaveCommand.ExecuteAsync(null);

        shell.Editor.State.Should().Be(SaveState.Saved);
        var onDisk = harness.VaultFolder.Read("apuntes.md");
        onDisk.Should().Be(shell.Editor.Text).And.Contain("PalabraUnica").And.StartWith("---\nid: ");
        shell.Editor.Details!.Id.Should().NotBe(Strings.Field_None);

        await harness.SettleAsync();
        shell.NoteList.SearchText = "palabraunica";
        await UiTest.WaitForAsync(() => shell.NoteList.Items.Count == 1, "the search result");
        shell.NoteList.Items[0].Path.Should().Be(_plain);
        shell.NoteList.Items[0].Snippet.Should().Contain(segment => segment.IsMatch);
    }

    [AvaloniaFact]
    public async Task Rename_UpdatesTitleFileNameAndTheOpenNote()
    {
        await using var harness = await DesktopHarness.StartAsync(new AvaloniaUiDispatcher(), seed: SampleVault.Seed);
        var shell = harness.Shell;
        await shell.Editor.OpenAsync(_deadlock);
        shell.RenameCommand.CanExecute(null).Should().BeTrue();

        var command = shell.RenameCommand.ExecuteAsync(null);
        var prompt = (PromptDialogViewModel)shell.Dialogs.Current!;
        prompt.Value.Should().Be("Deadlock en actualización de inventario", "the current title is offered for editing");
        prompt.Value = "Interbloqueo en inventario";
        prompt.ConfirmCommand.Execute(null);
        await command;

        shell.Editor.Path!.Value.Value.Should().Be("bugs/interbloqueo-en-inventario.md");
        shell.Editor.Title.Should().Be("Interbloqueo en inventario");
        harness.VaultFolder.Exists("bugs/deadlock-inventario.md").Should().BeFalse();
        harness.VaultFolder.Read("bugs/interbloqueo-en-inventario.md").Should().Contain("id: 01J8ZQ4M9T3N7K5W2X6Y8V0B1C");
    }

    [AvaloniaFact]
    public async Task Move_RejectsUnsafeFolders_ThenMovesTheNote()
    {
        await using var harness = await DesktopHarness.StartAsync(new AvaloniaUiDispatcher(), seed: SampleVault.Seed);
        var shell = harness.Shell;
        await shell.Editor.OpenAsync(_deadlock);

        var command = shell.MoveCommand.ExecuteAsync(null);
        await UiTest.WaitForAsync(() => shell.Dialogs.Current is PromptDialogViewModel, "the move dialog");
        var prompt = (PromptDialogViewModel)shell.Dialogs.Current!;
        prompt.Value.Should().Be("bugs");
        prompt.Suggestions.Should().Contain(["bugs", "runbooks"]);

        foreach (var unsafeFolder in new[] { "../outside", @"C:\Windows", ".git", "a/../../b" })
        {
            prompt.Value = unsafeFolder;
            prompt.ConfirmCommand.Execute(null);
            prompt.Error.Should().Be(Strings.Error_InvalidFolder, $"'{unsafeFolder}' must be rejected");
        }

        prompt.Value = "archivo/2026";
        prompt.ConfirmCommand.Execute(null);
        await command;

        shell.Editor.Path!.Value.Value.Should().Be("archivo/2026/deadlock-inventario.md");
        harness.VaultFolder.Exists("archivo/2026/deadlock-inventario.md").Should().BeTrue();
        harness.VaultFolder.Exists("bugs/deadlock-inventario.md").Should().BeFalse();
    }

    [AvaloniaFact]
    public async Task Delete_AsksFirst_MovesToTrash_AndTheTrashRestores()
    {
        await using var harness = await DesktopHarness.StartAsync(new AvaloniaUiDispatcher(), seed: SampleVault.Seed);
        var shell = harness.Shell;
        await shell.Editor.OpenAsync(_deadlock);

        // Declining the confirmation keeps the note.
        var declined = shell.DeleteCommand.ExecuteAsync(null);
        var confirm = shell.Dialogs.Current.Should().BeOfType<ConfirmDialogViewModel>().Subject;
        confirm.IsDestructive.Should().BeTrue();
        confirm.Message.Should().Contain("Deadlock en actualización de inventario");
        shell.Dialogs.CancelCurrent();
        await declined;
        harness.VaultFolder.Exists("bugs/deadlock-inventario.md").Should().BeTrue();

        var deleted = shell.DeleteCommand.ExecuteAsync(null);
        ((ConfirmDialogViewModel)shell.Dialogs.Current!).ConfirmCommand.Execute(null);
        await deleted;

        shell.Editor.HasNote.Should().BeFalse();
        harness.VaultFolder.Exists("bugs/deadlock-inventario.md").Should().BeFalse();
        shell.Notification.Message.Should().Be(Strings.Notify_MovedToTrash);
        await harness.SettleAsync();
        shell.NoteList.Items.Should().HaveCount(2);

        var trash = shell.OpenTrashCommand.ExecuteAsync(null);
        await UiTest.WaitForAsync(() => shell.Dialogs.Current is TrashDialogViewModel, "the trash dialog");
        var dialog = (TrashDialogViewModel)shell.Dialogs.Current!;
        dialog.Items.Should().ContainSingle().Which.Path.Should().Be("bugs/deadlock-inventario.md");

        await dialog.RestoreCommand.ExecuteAsync(dialog.Items[0]);
        dialog.IsEmpty.Should().BeTrue();
        dialog.CancelCommand.Execute(null);
        await trash;

        harness.VaultFolder.Exists("bugs/deadlock-inventario.md").Should().BeTrue();
        await harness.SettleAsync();
        shell.NoteList.Items.Should().HaveCount(3);
    }

    [AvaloniaFact]
    public async Task Delete_NoteWhoseChangesCannotBeSaved_IsNotDeleted()
    {
        // The trash would receive the version on disk while the text in the editor - the one the user
        // is looking at - would be gone for good.
        await using var harness = await DesktopHarness.StartAsync(new AvaloniaUiDispatcher(), seed: SampleVault.Seed);
        var shell = harness.Shell;
        await shell.Editor.OpenAsync(_plain);
        shell.Editor.Text += "\nMi edición sin guardar.\n";
        harness.VaultFolder.Write("apuntes.md", "# Cambiado fuera\n");
        await UiTest.WaitForAsync(() => shell.Editor.IsConflict, "the conflict");

        var deletion = shell.DeleteCommand.ExecuteAsync(null);
        ((ConfirmDialogViewModel)shell.Dialogs.Current!).ConfirmCommand.Execute(null);
        await deletion;

        harness.VaultFolder.Exists("apuntes.md").Should().BeTrue();
        shell.Editor.Path.Should().Be(_plain);
        shell.Editor.Text.Should().Contain("Mi edición sin guardar.");
        shell.Editor.IsReadOnly.Should().BeFalse();
        shell.Notification.IsError.Should().BeTrue();
        shell.Notification.Message.Should().Be(Strings.Error_UnsavedChanges);
        (await harness.Session.Notes.ListTrashAsync(CancellationToken.None)).Should().BeEmpty();
    }

    [AvaloniaFact]
    public async Task AddVault_WhileTheOpenNoteCannotBeSaved_ChangesNothing()
    {
        await using var harness = await DesktopHarness.StartAsync(new AvaloniaUiDispatcher(), seed: SampleVault.Seed);
        var shell = harness.Shell;
        using var second = new TempDirectory("devnotes-ui-second-");
        await shell.Editor.OpenAsync(_plain);
        shell.Editor.Text += "\nMi edición sin guardar.\n";
        harness.VaultFolder.Write("apuntes.md", "# Cambiado fuera\n");
        await UiTest.WaitForAsync(() => shell.Editor.IsConflict, "the conflict");

        harness.FolderPicker.NextResult = second.Path;
        await shell.AddVaultCommand.ExecuteAsync(null);

        shell.Vaults.Should().ContainSingle("the new vault is not registered behind the user's back");
        shell.ActiveVault!.Path.Should().Be(harness.VaultFolder.Path);
        shell.Editor.Text.Should().Contain("Mi edición sin guardar.", "switching vaults must never drop text silently");
        shell.Notification.Message.Should().Be(Strings.Error_UnsavedChanges);
    }

    [AvaloniaFact]
    public async Task VaultThatCannotBeOpened_KeepsTheVaultListOnScreen_SoTheUserCanSwitchOrRemoveIt()
    {
        await using var harness = await DesktopHarness.StartAsync(new AvaloniaUiDispatcher(), seed: SampleVault.Seed);
        var shell = harness.Shell;
        using var second = new TempDirectory("devnotes-ui-second-");
        second.Write("solo.md", "# Única nota\n");
        harness.FolderPicker.NextResult = second.Path;
        await shell.AddVaultCommand.ExecuteAsync(null);
        await harness.SettleAsync();

        // The drive of the first vault is unplugged: its folder is gone when the user switches back.
        var gone = shell.Vaults.First(vault => vault.Path == harness.VaultFolder.Path);
        Directory.Move(harness.VaultFolder.Path, harness.VaultFolder.Path + "-unplugged");
        try
        {
            await shell.SwitchVaultCommand.ExecuteAsync(gone);

            shell.Notification.IsError.Should().BeTrue();
            shell.HasVault.Should().BeFalse("no vault is open");
            shell.HasVaults.Should().BeTrue();
            shell.ShowWelcome.Should().BeFalse("the welcome screen has no vault list: the user would be stranded");
            shell.NoteList.IsNoVault.Should().BeTrue();

            await shell.SwitchVaultCommand.ExecuteAsync(shell.Vaults.First(vault => vault.Path == second.Path));
            await harness.SettleAsync();

            shell.HasVault.Should().BeTrue();
            shell.NoteList.Items.Should().ContainSingle();
        }
        finally
        {
            Directory.Move(harness.VaultFolder.Path + "-unplugged", harness.VaultFolder.Path);
        }
    }

    [AvaloniaFact]
    public async Task Shutdown_StopsAcceptingTextWhileItSaves_AndResumesIfTheUserStays()
    {
        await using var harness = await DesktopHarness.StartAsync(new AvaloniaUiDispatcher(), seed: SampleVault.Seed);
        var shell = harness.Shell;
        await shell.Editor.OpenAsync(_plain);
        shell.Editor.Text += "\nSin guardar.\n";
        harness.VaultFolder.Write("apuntes.md", "# Cambiado fuera\n");
        await UiTest.WaitForAsync(() => shell.Editor.IsConflict, "the conflict");

        var closing = shell.ShutdownAsync(layout: null);
        await UiTest.WaitForAsync(() => shell.Dialogs.Current is ConfirmDialogViewModel, "the confirmation");

        shell.Editor.IsReadOnly.Should().BeTrue("nothing typed now would be saved by anyone");
        shell.Dialogs.CancelCurrent();
        (await closing).Should().BeFalse();
        shell.Editor.IsReadOnly.Should().BeFalse("the user chose to stay and keeps working");
    }

    [AvaloniaFact]
    public async Task FocusListCommand_AsksTheViewToFocusTheSearchBox()
    {
        await using var harness = await DesktopHarness.StartAsync(new AvaloniaUiDispatcher(), seed: SampleVault.Seed);
        var requests = 0;
        harness.Shell.ListSearchFocusRequested += (_, _) => requests++;

        harness.Shell.FocusListCommand.Execute(null);

        requests.Should().Be(1);
        harness.Shell.Commands.Single(command => command.Id == "list.focus").Shortcut!.Format(macStyle: false).Should().Be("Ctrl+Shift+F");
    }

    [AvaloniaFact]
    public async Task Trash_DeleteForeverAndEmpty_RequireConfirmation()
    {
        await using var harness = await DesktopHarness.StartAsync(new AvaloniaUiDispatcher(), seed: SampleVault.Seed);
        var shell = harness.Shell;
        await harness.Session.Notes.DeleteAsync(_deadlock, CancellationToken.None);
        await harness.Session.Notes.DeleteAsync(_plain, CancellationToken.None);

        var trash = shell.OpenTrashCommand.ExecuteAsync(null);
        await UiTest.WaitForAsync(() => shell.Dialogs.Current is TrashDialogViewModel, "the trash dialog");
        var dialog = (TrashDialogViewModel)shell.Dialogs.Current!;
        dialog.Items.Should().HaveCount(2);

        var purge = dialog.DeleteForeverCommand.ExecuteAsync(dialog.Items[0]);
        shell.Dialogs.Current.Should().BeOfType<ConfirmDialogViewModel>("a second, destructive confirmation sits on top of the trash");
        ((ConfirmDialogViewModel)shell.Dialogs.Current!).ConfirmCommand.Execute(null);
        await purge;
        dialog.Items.Should().ContainSingle();

        var empty = dialog.EmptyAllCommand.ExecuteAsync(null);
        shell.Dialogs.CancelCurrent();
        await empty;
        dialog.Items.Should().ContainSingle("the user backed out");

        empty = dialog.EmptyAllCommand.ExecuteAsync(null);
        ((ConfirmDialogViewModel)shell.Dialogs.Current!).ConfirmCommand.Execute(null);
        await empty;
        dialog.IsEmpty.Should().BeTrue();

        dialog.CancelCommand.Execute(null);
        await trash;
        (await harness.Session.Notes.ListTrashAsync(CancellationToken.None)).Should().BeEmpty();
    }

    [AvaloniaFact]
    public async Task ExternalEdit_WhileTheNoteIsClean_ReloadsIt()
    {
        await using var harness = await DesktopHarness.StartAsync(new AvaloniaUiDispatcher(), seed: SampleVault.Seed);
        var shell = harness.Shell;
        await shell.Editor.OpenAsync(_plain);

        harness.VaultFolder.Write("apuntes.md", "# Apuntes sueltos\n\nCambiado desde VS Code.\n");

        await UiTest.WaitForAsync(() => shell.Editor.Text.Contains("VS Code", StringComparison.Ordinal), "the reload from disk");
        shell.Editor.State.Should().Be(SaveState.Saved);
        shell.Notification.Message.Should().Be(Strings.Notify_Reloaded);
    }

    [AvaloniaFact]
    public async Task ExternalEdit_WhileTheNoteHasUnsavedChanges_IsAConflict_ResolvedByTheUser()
    {
        await using var harness = await DesktopHarness.StartAsync(new AvaloniaUiDispatcher(), seed: SampleVault.Seed);
        var shell = harness.Shell;
        await shell.Editor.OpenAsync(_plain);

        shell.Editor.Text += "\nMi edición sin guardar.\n";
        harness.VaultFolder.Write("apuntes.md", "# Apuntes sueltos\n\nCambiado desde VS Code.\n");

        await UiTest.WaitForAsync(() => shell.Editor.IsConflict, "the conflict");
        harness.VaultFolder.Read("apuntes.md").Should().Contain("VS Code", "the external version is never overwritten silently");
        shell.Editor.Text.Should().Contain("Mi edición", "the user's text is never discarded silently");

        // Switching notes is refused while the conflict is unresolved.
        (await shell.Editor.OpenAsync(_deadlock)).Should().BeFalse();

        await shell.Editor.KeepMineCommand.ExecuteAsync(null);

        shell.Editor.State.Should().Be(SaveState.Saved);
        harness.VaultFolder.Read("apuntes.md").Should().Contain("Mi edición").And.NotContain("VS Code");
    }

    [AvaloniaFact]
    public async Task Conflict_SaveCopy_KeepsBothVersionsOnDisk()
    {
        await using var harness = await DesktopHarness.StartAsync(new AvaloniaUiDispatcher(), seed: SampleVault.Seed);
        var shell = harness.Shell;
        await shell.Editor.OpenAsync(_deadlock);

        shell.Editor.Text += "\nMi edición.\n";
        harness.VaultFolder.Write("bugs/deadlock-inventario.md", SampleVault.DeadlockNote + "\nEdición externa.\n");
        await UiTest.WaitForAsync(() => shell.Editor.IsConflict, "the conflict");

        await shell.Editor.SaveCopyCommand.ExecuteAsync(null);

        shell.Editor.Text.Should().Contain("Edición externa").And.NotContain("Mi edición");
        var copy = harness.VaultFolder.Read("bugs/deadlock-inventario-copy.md");
        copy.Should().Contain("Mi edición").And.NotContain("01J8ZQ4M9T3N7K5W2X6Y8V0B1C", "the copy gets its own id");
    }

    [AvaloniaFact]
    public async Task SwitchingAndRemovingVaults_KeepsNotesOnDisk()
    {
        await using var harness = await DesktopHarness.StartAsync(new AvaloniaUiDispatcher(), seed: SampleVault.Seed);
        var shell = harness.Shell;
        using var second = new TempDirectory("devnotes-ui-second-");
        second.Write("solo.md", "# Única nota\n");

        harness.FolderPicker.NextResult = second.Path;
        await shell.AddVaultCommand.ExecuteAsync(null);
        await harness.SettleAsync();
        shell.Vaults.Should().HaveCount(2);
        shell.ActiveVault!.Path.Should().Be(second.Path);
        shell.NoteList.Items.Should().ContainSingle();

        await shell.SwitchVaultCommand.ExecuteAsync(shell.Vaults.First(vault => vault.Path == harness.VaultFolder.Path));
        await harness.SettleAsync();
        shell.NoteList.Items.Should().HaveCount(3);
        harness.Settings.Current.ActiveVaultId.Should().Be(shell.ActiveVault!.Vault.Id.Value);

        var removal = shell.RemoveVaultCommand.ExecuteAsync(shell.ActiveVault);
        ((ConfirmDialogViewModel)shell.Dialogs.Current!).ConfirmCommand.Execute(null);
        await removal;
        await harness.SettleAsync();

        shell.Vaults.Should().ContainSingle().Which.Path.Should().Be(second.Path);
        shell.ActiveVault!.Path.Should().Be(second.Path, "the remaining vault is opened");
        harness.VaultFolder.Exists("apuntes.md").Should().BeTrue("removing a vault never deletes notes");

        removal = shell.RemoveVaultCommand.ExecuteAsync(shell.ActiveVault);
        ((ConfirmDialogViewModel)shell.Dialogs.Current!).ConfirmCommand.Execute(null);
        await removal;
        shell.ShowWelcome.Should().BeTrue();
    }

    [AvaloniaFact]
    public async Task AppearanceCommands_ApplyAndPersist()
    {
        await using var harness = await DesktopHarness.StartAsync(new AvaloniaUiDispatcher(), seed: SampleVault.Seed);
        var shell = harness.Shell;
        harness.Settings.Current.Theme.Should().Be(AppTheme.Dark, "dark is the default theme");

        await shell.SetThemeLightCommand.ExecuteAsync(null);
        harness.Settings.Current.Theme.Should().Be(AppTheme.Light);
        harness.Theme.Applied[^1].Theme.Should().Be(AppTheme.Light);
        shell.Editor.IsDarkTheme.Should().BeFalse();

        await shell.SetThemeSystemCommand.ExecuteAsync(null);
        harness.Settings.Current.Theme.Should().Be(AppTheme.System);
        await shell.SetThemeDarkCommand.ExecuteAsync(null);

        await shell.ZoomInCommand.ExecuteAsync(null);
        await shell.ZoomInCommand.ExecuteAsync(null);
        harness.Settings.Current.FontSize.Should().Be(16);
        await shell.ZoomOutCommand.ExecuteAsync(null);
        harness.Settings.Current.FontSize.Should().Be(15);
        await shell.ZoomResetCommand.ExecuteAsync(null);
        harness.Settings.Current.FontSize.Should().Be(AppSettings.DefaultFontSize);

        await shell.SetDensityCompactCommand.ExecuteAsync(null);
        harness.Settings.Current.Density.Should().Be(UiDensity.Compact);
        await shell.SetDensityComfortableCommand.ExecuteAsync(null);
        harness.Settings.Current.Density.Should().Be(UiDensity.Comfortable);

        File.ReadAllText(harness.DataFolder.Combine("settings.json")).Should().Contain("\"theme\": \"Dark\"");
    }

    [AvaloniaFact]
    public async Task AppearanceAndPanelCommands_StayResponsiveWhileTheSettingsAreBeingSaved()
    {
        var store = new HeldSettingsStore();
        await using var harness = await DesktopHarness.StartAsync(
            new AvaloniaUiDispatcher(),
            openVault: false,
            configure: services => services.AddSingleton<ISettingsStore>(store));
        var shell = harness.Shell;
        var release = store.HoldSaves();

        // Two quick presses of the same shortcut: the second one arrives while the first is still saving.
        shell.ToggleSidebarCommand.Execute(null);
        shell.IsSidebarCollapsed.Should().BeTrue();
        shell.ToggleSidebarCommand.CanExecute(null).Should().BeTrue("a slow disk must not swallow the next key press");
        shell.ToggleSidebarCommand.Execute(null);
        shell.IsSidebarCollapsed.Should().BeFalse();

        shell.ToggleInspectorCommand.Execute(null);
        shell.ToggleInspectorCommand.Execute(null);
        shell.ToggleInspectorCommand.Execute(null);
        shell.IsInspectorCollapsed.Should().BeTrue();

        // Key repeat on zoom: every step counts.
        shell.ZoomInCommand.Execute(null);
        shell.ZoomInCommand.Execute(null);
        shell.ZoomInCommand.Execute(null);

        release();
        await UiTest.WaitForAsync(
            () => harness.Settings.Current.FontSize == AppSettings.DefaultFontSize + 3 && harness.Theme.Applied[^1].FontSize == AppSettings.DefaultFontSize + 3,
            "the three zoom steps to be saved and applied");
        await UiTest.WaitForAsync(
            () => harness.Settings.Current.Layout is { IsSidebarCollapsed: false, IsInspectorCollapsed: true },
            "the panel state to be saved");
        store.Stored.Should().Be(harness.Settings.Current);
    }

    [AvaloniaFact]
    public async Task LayoutSortAndViewMode_ArePersisted()
    {
        await using var harness = await DesktopHarness.StartAsync(new AvaloniaUiDispatcher(), seed: SampleVault.Seed);
        var shell = harness.Shell;

        await shell.ToggleSidebarCommand.ExecuteAsync(null);
        await shell.ToggleInspectorCommand.ExecuteAsync(null);
        shell.ToggleViewModeCommand.Execute(null);
        shell.NoteList.SortOrder = NoteSortOrder.TitleAscending;

        await UiTest.WaitForAsync(
            () => harness.Settings.Current is { SortOrder: NoteSortOrder.TitleAscending, ViewMode: EditorViewMode.Preview },
            "the settings to be saved");
        shell.IsSidebarCollapsed.Should().BeTrue();
        harness.Settings.Current.Layout.IsSidebarCollapsed.Should().BeTrue();
        harness.Settings.Current.Layout.IsInspectorCollapsed.Should().BeTrue();
        shell.NoteList.Items.Select(item => item.PlainTitle).Should().BeInAscendingOrder(StringComparer.CurrentCultureIgnoreCase);
    }

    [AvaloniaFact]
    public async Task Reindex_RebuildsTheIndexFromTheFiles()
    {
        await using var harness = await DesktopHarness.StartAsync(new AvaloniaUiDispatcher(), seed: SampleVault.Seed);
        var shell = harness.Shell;

        shell.ReindexCommand.Execute(null);

        shell.Notification.Message.Should().Be(Strings.Notify_Reindexing);
        await harness.SettleAsync();
        shell.NoteList.Items.Should().HaveCount(3);
        await UiTest.WaitForAsync(() => shell.IndexStatusText == Strings.Status_IndexReady && !shell.IsIndexing, "the index to be ready");
    }

    [AvaloniaFact]
    public async Task Shutdown_SavesPendingChangesAndTheLayout()
    {
        await using var harness = await DesktopHarness.StartAsync(new AvaloniaUiDispatcher(), seed: SampleVault.Seed);
        var shell = harness.Shell;
        await shell.Editor.OpenAsync(_plain);
        shell.Editor.Text += "\nEscrito justo antes de cerrar.\n";

        var closed = await shell.ShutdownAsync(new WindowLayout(300, 320, 200, 1000, 700, IsMaximized: false));

        closed.Should().BeTrue();
        harness.VaultFolder.Read("apuntes.md").Should().Contain("Escrito justo antes de cerrar.");
        harness.Settings.Current.Layout.Should().BeEquivalentTo(new LayoutSettings
        {
            SidebarWidth = 300,
            InspectorWidth = 320,
            NoteListHeight = 200,
            WindowWidth = 1000,
            WindowHeight = 700,
        });
    }

    [AvaloniaFact]
    public async Task Shutdown_WithAnUnsavableNote_AsksBeforeDiscarding()
    {
        await using var harness = await DesktopHarness.StartAsync(new AvaloniaUiDispatcher(), seed: SampleVault.Seed);
        var shell = harness.Shell;
        await shell.Editor.OpenAsync(_plain);
        shell.Editor.Text += "\nSin guardar.\n";
        harness.VaultFolder.Write("apuntes.md", "# Cambiado fuera\n");
        await UiTest.WaitForAsync(() => shell.Editor.IsConflict, "the conflict");

        var stay = shell.ShutdownAsync(layout: null);
        await UiTest.WaitForAsync(() => shell.Dialogs.Current is ConfirmDialogViewModel, "the confirmation");
        shell.Dialogs.CancelCurrent();
        (await stay).Should().BeFalse("the user chose to stay");
        shell.Editor.Text.Should().Contain("Sin guardar.");

        var leave = shell.ShutdownAsync(layout: null);
        await UiTest.WaitForAsync(() => shell.Dialogs.Current is ConfirmDialogViewModel, "the confirmation");
        ((ConfirmDialogViewModel)shell.Dialogs.Current!).ConfirmCommand.Execute(null);
        (await leave).Should().BeTrue();
        harness.VaultFolder.Read("apuntes.md").Should().Be("# Cambiado fuera\n", "the external version stays untouched");
    }

    [AvaloniaFact]
    public async Task Commands_CarryTheShortcutsOfTheSpecification()
    {
        await using var harness = await DesktopHarness.StartAsync(new AvaloniaUiDispatcher(), openVault: false);

        var shortcuts = harness.Shell.Commands
            .Where(command => command.Shortcut is not null)
            .ToDictionary(command => command.Id, command => command.Shortcut!.Format(macStyle: false));

        shortcuts.Should().Contain(new Dictionary<string, string>
        {
            ["note.new"] = "Ctrl+N",
            ["note.save"] = "Ctrl+S",
            ["search.open"] = "Ctrl+K",
            ["note.switch"] = "Ctrl+P",
            ["palette.open"] = "Ctrl+Shift+P",
            ["view.sidebar"] = "Ctrl+B",
            ["view.mode"] = "Ctrl+E",
            ["note.rename"] = "F2",
        });
        harness.Shell.Commands.Select(command => command.Id).Should().OnlyHaveUniqueItems();
        shortcuts.Values.Should().OnlyHaveUniqueItems("two commands must not share a shortcut");
        harness.Shell.EmptyVaultHint.Should().Contain(new ShortcutKey("N", Primary: true).DisplayText);
    }
}
