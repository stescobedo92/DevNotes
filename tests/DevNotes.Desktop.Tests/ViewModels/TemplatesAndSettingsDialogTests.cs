using Avalonia.Headless.XUnit;
using DevNotes.Application.Settings;
using DevNotes.Desktop.Resources;
using DevNotes.Desktop.Services;
using DevNotes.Desktop.Tests.Support;
using DevNotes.Desktop.ViewModels.Dialogs;

namespace DevNotes.Desktop.Tests.ViewModels;

public sealed class TemplatesAndSettingsDialogTests
{
    [AvaloniaFact]
    public async Task Templates_ListsBuiltIns_SavesACustomizedCopy_AndRestoresIt()
    {
        await using var harness = await DesktopHarness.StartAsync(new AvaloniaUiDispatcher(), seed: SampleVault.Seed);
        var shell = harness.Shell;

        var opening = shell.EditTemplatesCommand.ExecuteAsync(null);
        await UiTest.WaitForAsync(() => shell.Dialogs.Current is TemplatesDialogViewModel, "the templates dialog");
        var dialog = (TemplatesDialogViewModel)shell.Dialogs.Current!;

        dialog.Items.Select(item => item.Key).Should().Equal("note", "bug", "adr", "runbook", "learning", "snippet");
        dialog.Items.Should().OnlyContain(item => item.IsBuiltIn && !item.IsCustomized);
        dialog.SelectedItem!.Key.Should().Be("note");
        dialog.IsDirty.Should().BeFalse();
        dialog.SaveCommand.CanExecute(null).Should().BeFalse();

        dialog.SelectedItem = dialog.Items.Single(item => item.Key == "bug");
        dialog.EditorText += "\n## Mi sección\n";
        dialog.IsDirty.Should().BeTrue();
        await dialog.SaveCommand.ExecuteAsync(null);

        dialog.IsDirty.Should().BeFalse();
        dialog.SelectedItem.IsCustomized.Should().BeTrue();
        dialog.SelectedItem.KindLabel.Should().Be(Strings.Dialog_Templates_Customized);
        harness.VaultFolder.Read(".devnotes/templates/bug.md").Should().Contain("## Mi sección");
        harness.VaultFolder.Read(".devnotes/.gitignore").Should().Contain("trash/");

        var reset = dialog.ResetCommand.ExecuteAsync(null);
        await UiTest.WaitForAsync(() => shell.Dialogs.Current is ConfirmDialogViewModel, "the confirmation");
        ((ConfirmDialogViewModel)shell.Dialogs.Current!).ConfirmCommand.Execute(null);
        await reset;

        dialog.SelectedItem.IsCustomized.Should().BeFalse();
        dialog.EditorText.Should().NotContain("## Mi sección");
        harness.VaultFolder.Exists(".devnotes/templates/bug.md").Should().BeFalse();

        dialog.Cancel();
        await opening;
    }

    [AvaloniaFact]
    public async Task Templates_CreatesAndDeletesTheUsersOwn_AndProtectsUnsavedEdits()
    {
        await using var harness = await DesktopHarness.StartAsync(new AvaloniaUiDispatcher(), seed: SampleVault.Seed);
        var shell = harness.Shell;
        var opening = shell.EditTemplatesCommand.ExecuteAsync(null);
        await UiTest.WaitForAsync(() => shell.Dialogs.Current is TemplatesDialogViewModel, "the templates dialog");
        var dialog = (TemplatesDialogViewModel)shell.Dialogs.Current!;

        var creating = dialog.NewCommand.ExecuteAsync(null);
        await UiTest.WaitForAsync(() => shell.Dialogs.Current is PromptDialogViewModel, "the name prompt");
        var prompt = (PromptDialogViewModel)shell.Dialogs.Current!;
        prompt.Value = "bug";
        prompt.ConfirmCommand.Execute(null);
        prompt.Error.Should().Be(Strings.Error_TemplateExists);
        prompt.Value = "Stand-up diario";
        prompt.ConfirmCommand.Execute(null);
        await creating;

        dialog.SelectedItem!.Key.Should().Be("stand-up-diario");
        dialog.SelectedItem.KindLabel.Should().Be(Strings.Dialog_Templates_Custom);
        harness.VaultFolder.Exists(".devnotes/templates/stand-up-diario.md").Should().BeTrue();

        // Unsaved edits: switching away asks first; refusing keeps the selection and the text.
        dialog.EditorText = "# {{title}}\nEditado";
        dialog.SelectedItem = dialog.Items[0];
        await UiTest.WaitForAsync(() => shell.Dialogs.Current is ConfirmDialogViewModel, "the discard confirmation");
        shell.Dialogs.CancelCurrent();
        await UiTest.WaitForAsync(() => dialog.SelectedItem?.Key == "stand-up-diario", "the selection to be restored");
        dialog.EditorText.Should().Be("# {{title}}\nEditado");

        // Accepting the discard moves on and drops the edit.
        dialog.SelectedItem = dialog.Items[0];
        await UiTest.WaitForAsync(() => shell.Dialogs.Current is ConfirmDialogViewModel, "the discard confirmation");
        ((ConfirmDialogViewModel)shell.Dialogs.Current!).ConfirmCommand.Execute(null);
        await UiTest.WaitForAsync(() => !dialog.IsDirty && dialog.SelectedItem?.Key == "note", "the plain note to be selected");

        dialog.SelectedItem = dialog.Items.Single(item => item.Key == "stand-up-diario");
        var deleting = dialog.ResetCommand.ExecuteAsync(null);
        await UiTest.WaitForAsync(() => shell.Dialogs.Current is ConfirmDialogViewModel, "the delete confirmation");
        ((ConfirmDialogViewModel)shell.Dialogs.Current!).ConfirmCommand.Execute(null);
        await deleting;

        dialog.Items.Should().NotContain(item => item.Key == "stand-up-diario");
        harness.VaultFolder.Exists(".devnotes/templates/stand-up-diario.md").Should().BeFalse();

        dialog.Cancel();
        await opening;
    }

    [AvaloniaFact]
    public async Task Templates_NewWithUnsavedEdits_AsksBeforeAnythingIsWritten()
    {
        await using var harness = await DesktopHarness.StartAsync(new AvaloniaUiDispatcher(), seed: SampleVault.Seed);
        var shell = harness.Shell;
        var opening = shell.EditTemplatesCommand.ExecuteAsync(null);
        await UiTest.WaitForAsync(() => shell.Dialogs.Current is TemplatesDialogViewModel, "the templates dialog");
        var dialog = (TemplatesDialogViewModel)shell.Dialogs.Current!;
        var original = dialog.EditorText;
        dialog.EditorText += "\n## Sin guardar\n";

        // Refusing to drop the edits cancels the creation: no name is asked and no file appears.
        var refused = dialog.NewCommand.ExecuteAsync(null);
        await UiTest.WaitForAsync(() => shell.Dialogs.Current is ConfirmDialogViewModel, "the discard confirmation");
        shell.Dialogs.CancelCurrent();
        await refused;

        ReferenceEquals(shell.Dialogs.Current, dialog).Should().BeTrue();
        dialog.Items.Should().HaveCount(6);
        dialog.EditorText.Should().EndWith("## Sin guardar\n");
        var templatesFolder = harness.VaultFolder.Combine(".devnotes", "templates");
        (Directory.Exists(templatesFolder) ? Directory.GetFiles(templatesFolder) : []).Should().BeEmpty();

        // Accepting and then cancelling the name still keeps the edits: nothing was created.
        var cancelled = dialog.NewCommand.ExecuteAsync(null);
        await UiTest.WaitForAsync(() => shell.Dialogs.Current is ConfirmDialogViewModel, "the discard confirmation");
        ((ConfirmDialogViewModel)shell.Dialogs.Current!).ConfirmCommand.Execute(null);
        await UiTest.WaitForAsync(() => shell.Dialogs.Current is PromptDialogViewModel, "the name prompt");
        shell.Dialogs.CancelCurrent();
        await cancelled;

        dialog.EditorText.Should().EndWith("## Sin guardar\n");

        // Accepting and naming it creates the template and moves to it without asking again.
        var creating = dialog.NewCommand.ExecuteAsync(null);
        await UiTest.WaitForAsync(() => shell.Dialogs.Current is ConfirmDialogViewModel, "the discard confirmation");
        ((ConfirmDialogViewModel)shell.Dialogs.Current!).ConfirmCommand.Execute(null);
        await UiTest.WaitForAsync(() => shell.Dialogs.Current is PromptDialogViewModel, "the name prompt");
        var prompt = (PromptDialogViewModel)shell.Dialogs.Current!;
        prompt.Value = "Retro";
        prompt.ConfirmCommand.Execute(null);
        await creating;

        ReferenceEquals(shell.Dialogs.Current, dialog).Should().BeTrue("no second confirmation is shown");
        dialog.SelectedItem!.Key.Should().Be("retro");
        dialog.IsDirty.Should().BeFalse();
        harness.VaultFolder.Exists(".devnotes/templates/retro.md").Should().BeTrue();
        harness.VaultFolder.Exists(".devnotes/templates/note.md").Should().BeFalse("the dropped edits were never saved");
        dialog.SelectedItem = dialog.Items.Single(item => item.Key == "note");
        dialog.EditorText.Should().Be(original);

        dialog.Cancel();
        await opening;
    }

    [AvaloniaFact]
    public async Task Templates_EscapeAndShutdown_AskBeforeDroppingUnsavedEdits()
    {
        await using var harness = await DesktopHarness.StartAsync(new AvaloniaUiDispatcher(), seed: SampleVault.Seed);
        var shell = harness.Shell;
        var opening = shell.EditTemplatesCommand.ExecuteAsync(null);
        await UiTest.WaitForAsync(() => shell.Dialogs.Current is TemplatesDialogViewModel, "the templates dialog");
        var dialog = (TemplatesDialogViewModel)shell.Dialogs.Current!;
        dialog.EditorText += "\n## Sin guardar\n";

        // Escape: refusing keeps the dialog and the text.
        shell.Dialogs.CancelCurrent();
        await UiTest.WaitForAsync(() => shell.Dialogs.Current is ConfirmDialogViewModel, "the discard confirmation");
        shell.Dialogs.CancelCurrent();
        await UiTest.WaitForAsync(() => ReferenceEquals(shell.Dialogs.Current, dialog), "the templates dialog to stay");
        dialog.EditorText.Should().Contain("## Sin guardar");

        // Closing the app: the same question, and refusing cancels the shutdown.
        var shutdown = shell.ShutdownAsync(layout: null);
        await UiTest.WaitForAsync(() => shell.Dialogs.Current is ConfirmDialogViewModel, "the discard confirmation");
        shell.Dialogs.CancelCurrent();
        (await shutdown).Should().BeFalse();
        ReferenceEquals(shell.Dialogs.Current, dialog).Should().BeTrue();

        // Accepting drops the edit and closes.
        shell.Dialogs.CancelCurrent();
        await UiTest.WaitForAsync(() => shell.Dialogs.Current is ConfirmDialogViewModel, "the discard confirmation");
        ((ConfirmDialogViewModel)shell.Dialogs.Current!).ConfirmCommand.Execute(null);
        await opening;
        shell.Dialogs.IsOpen.Should().BeFalse();
        harness.VaultFolder.Exists(".devnotes/templates/note.md").Should().BeFalse();
    }

    [AvaloniaFact]
    public async Task Settings_AppearanceChanges_ApplyAndPersistAtOnce()
    {
        await using var harness = await DesktopHarness.StartAsync(new AvaloniaUiDispatcher(), seed: SampleVault.Seed);
        var shell = harness.Shell;
        var opening = shell.OpenSettingsCommand.ExecuteAsync(null);
        var dialog = (SettingsDialogViewModel)shell.Dialogs.Current!;

        dialog.Theme = dialog.ThemeChoices.Single(choice => choice.Value == AppTheme.Light);
        dialog.Density = dialog.DensityChoices.Single(choice => choice.Value == UiDensity.Compact);
        dialog.FontSize = 17;
        dialog.ReduceMotion = true;
        dialog.Language = dialog.LanguageChoices.Single(choice => choice.Value == "es");
        await UiTest.WaitForAsync(() => harness.Settings.Current.Language == "es", "the settings to be saved");

        harness.Settings.Current.Should().Match<AppSettings>(settings =>
            settings.Theme == AppTheme.Light && settings.Density == UiDensity.Compact && settings.FontSize == 17 && settings.ReduceMotion == true);
        harness.Theme.Applied.Should().Contain(settings => settings.Theme == AppTheme.Light && settings.FontSize == 17);
        dialog.CanReindex.Should().BeTrue();

        dialog.Cancel();
        await opening;
    }

    [AvaloniaFact]
    public async Task Settings_Hotkey_IsValidatedAndNormalized()
    {
        await using var harness = await DesktopHarness.StartAsync(new AvaloniaUiDispatcher(), openVault: false);
        var shell = harness.Shell;
        harness.Hotkey.SetState(new HotkeyState(HotkeyStatus.Failed, "in use"));
        var opening = shell.OpenSettingsCommand.ExecuteAsync(null);
        var dialog = (SettingsDialogViewModel)shell.Dialogs.Current!;

        dialog.HotkeyStatusText.Should().Be(ErrorMessages.Format(Strings.Settings_HotkeyStatus_Failed, "in use"));
        dialog.CanReindex.Should().BeFalse();

        dialog.Hotkey = "Shift+N";
        dialog.ApplyHotkeyCommand.Execute(null);
        dialog.HotkeyError.Should().Be(Strings.Error_Hotkey);
        harness.Settings.Current.QuickCapture.Hotkey.Should().Be(HotkeyGesture.DefaultText);

        dialog.Hotkey = " control + alt + k ";
        dialog.ApplyHotkeyCommand.Execute(null);
        dialog.HasHotkeyError.Should().BeFalse();
        dialog.Hotkey.Should().Be("Ctrl+Alt+K");
        await UiTest.WaitForAsync(() => harness.Settings.Current.QuickCapture.Hotkey == "Ctrl+Alt+K", "the shortcut to be saved");

        dialog.GlobalHotkeyEnabled = false;
        await UiTest.WaitForAsync(() => !harness.Settings.Current.QuickCapture.GlobalHotkeyEnabled, "the toggle to be saved");

        harness.Hotkey.SetState(HotkeyState.Active);
        dialog.HotkeyStatusText.Should().Be(Strings.Settings_HotkeyStatus_Active);

        dialog.Cancel();
        await opening;
    }

    [AvaloniaFact]
    public async Task Settings_ProjectRepositories_UseTheFolderPicker()
    {
        await using var harness = await DesktopHarness.StartAsync(new AvaloniaUiDispatcher(), seed: SampleVault.Seed);
        var shell = harness.Shell;
        var opening = shell.OpenSettingsCommand.ExecuteAsync(null);
        var dialog = (SettingsDialogViewModel)shell.Dialogs.Current!;

        dialog.Projects.Select(project => project.Name).Should().Equal("azure-microservices", "oatpp-api");
        var project = dialog.Projects[1];
        harness.FolderPicker.NextResult = Path.GetTempPath();
        await dialog.BrowseRepositoryCommand.ExecuteAsync(project);

        project.HasRepositoryPath.Should().BeTrue();
        await UiTest.WaitForAsync(() => harness.Settings.Current.FindProject("oatpp-api")?.RepositoryPath is not null, "the path to be saved");

        harness.FolderPicker.NextResult = null;
        await dialog.BrowseRepositoryCommand.ExecuteAsync(project);
        project.HasRepositoryPath.Should().BeTrue("cancelling the picker changes nothing");

        dialog.ClearRepositoryCommand.Execute(project);
        await UiTest.WaitForAsync(() => harness.Settings.Current.FindProject("oatpp-api") is null, "the path to be removed");

        dialog.Cancel();
        await opening;
    }
}
