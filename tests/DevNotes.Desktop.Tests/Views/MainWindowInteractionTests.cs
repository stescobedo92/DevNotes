using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Threading;
using AvaloniaEdit;
using DevNotes.Application.Settings;
using DevNotes.Desktop.Controls;
using DevNotes.Desktop.Services;
using DevNotes.Desktop.Tests.Support;
using DevNotes.Desktop.ViewModels;
using DevNotes.Desktop.ViewModels.Dialogs;
using DevNotes.Desktop.Views;
using DevNotes.Domain.Notes;

namespace DevNotes.Desktop.Tests.Views;

/// <summary>
/// Critical flows driven through the real window with simulated keyboard input only
/// (the app must be fully usable without a mouse): search, create, edit and save.
/// </summary>
public sealed class MainWindowInteractionTests
{
    private static readonly NotePath _plain = NotePath.Create("apuntes.md");

    [AvaloniaFact]
    public async Task CreateNote_WithKeyboardOnly()
    {
        await using var harness = await DesktopHarness.StartAsync(new AvaloniaUiDispatcher(), seed: SampleVault.Seed);
        var window = UiTest.ShowMainWindow(harness);
        var shell = harness.Shell;

        window.PressPrimary(Key.N);
        await UiTest.WaitForAsync(() => shell.Dialogs.Current is PromptDialogViewModel, "the new note dialog");
        window.FindControl<DialogHostView>("DialogHost")!.IsEffectivelyVisible.Should().BeTrue();

        // Focus is already in the title box: just type and confirm with Enter.
        window.Type("Nota creada con el teclado");
        ((PromptDialogViewModel)shell.Dialogs.Current!).Value.Should().Be("Nota creada con el teclado");
        window.Press(Key.Enter);

        await UiTest.WaitForAsync(() => shell.Editor.Path?.Value == "nota-creada-con-el-teclado.md", "the new note to open");
        harness.VaultFolder.Exists("nota-creada-con-el-teclado.md").Should().BeTrue();
        shell.Dialogs.IsOpen.Should().BeFalse();

        // Focus lands in the editor, so the user can start writing immediately.
        await UiTest.WaitForAsync(() => EditorOf(window).TextArea.IsFocused, "focus in the editor");
        window.Type("Primera línea escrita.");
        shell.Editor.Text.Should().Contain("Primera línea escrita.");
    }

    [AvaloniaFact]
    public async Task EditAndSave_WithKeyboardOnly()
    {
        await using var harness = await DesktopHarness.StartAsync(new AvaloniaUiDispatcher(), seed: SampleVault.Seed);
        var window = UiTest.ShowMainWindow(harness);
        var shell = harness.Shell;
        await OpenAsync(harness, _plain);
        var editor = EditorOf(window);
        editor.Text.Should().Be(SampleVault.PlainNote);

        editor.TextArea.Focus();
        editor.CaretOffset = editor.Document.TextLength;
        window.Type("Texto tecleado en el editor.");

        shell.Editor.Text.Should().EndWith("Texto tecleado en el editor.");
        shell.Editor.State.Should().Be(SaveState.Modified);
        harness.VaultFolder.Read("apuntes.md").Should().Be(SampleVault.PlainNote, "nothing is written before the save");

        window.PressPrimary(Key.S);
        await UiTest.WaitForAsync(() => shell.Editor.State == SaveState.Saved, "the save to finish");

        harness.VaultFolder.Read("apuntes.md").Should().Contain("Texto tecleado en el editor.");
        editor.Text.Should().Be(shell.Editor.Text, "the editor shows exactly what was written, including the stamped frontmatter");
        editor.Text.Should().StartWith("---\nid: ");
    }

    [AvaloniaFact]
    public async Task SavingStampedFrontmatter_DoesNotMoveTheCaret()
    {
        await using var harness = await DesktopHarness.StartAsync(new AvaloniaUiDispatcher(), seed: SampleVault.Seed);
        var window = UiTest.ShowMainWindow(harness);
        var shell = harness.Shell;
        await OpenAsync(harness, _plain);
        var editor = EditorOf(window);

        editor.TextArea.Focus();
        editor.CaretOffset = editor.Document.TextLength;
        window.Type("fin");
        window.PressPrimary(Key.S);
        await UiTest.WaitForAsync(() => shell.Editor.State == SaveState.Saved, "the save to finish");

        editor.CaretOffset.Should().Be(editor.Document.TextLength, "the frontmatter was inserted above the caret");
        window.Type("!");
        shell.Editor.Text.Should().EndWith("fin!");
    }

    [AvaloniaFact]
    public async Task Search_TypingFiltersTheList_AndArrowKeysOpenAResult()
    {
        await using var harness = await DesktopHarness.StartAsync(new AvaloniaUiDispatcher(), seed: SampleVault.Seed);
        var window = UiTest.ShowMainWindow(harness);
        var shell = harness.Shell;
        var noteList = window.FindControl<NoteListView>("NoteList")!;
        shell.NoteList.Items.Should().HaveCount(3);

        noteList.FocusSearch();
        window.Type("inventario");
        await UiTest.WaitForAsync(() => shell.NoteList.Items.Count == 1, "the filtered list");

        var item = shell.NoteList.Items[0];
        item.Path.Value.Should().Be("bugs/deadlock-inventario.md");
        item.Title.Should().Contain(segment => segment.IsMatch, "the match is highlighted in the title");

        var rendered = noteList.FindDescendants<SegmentedTextBlock>().First();
        rendered.Inlines!.Count.Should().BeGreaterThan(1, "highlighted fragments are rendered as separate runs");

        window.Press(Key.Down);
        await UiTest.WaitForAsync(() => shell.Editor.Path == item.Path, "the note to open");

        window.Press(Key.Escape);
        await UiTest.WaitForAsync(() => shell.NoteList.Items.Count == 3, "the search to be cleared");
        shell.NoteList.SelectedItem!.Path.Should().Be(item.Path, "the open note stays selected");
    }

    [AvaloniaFact]
    public async Task Search_NoResults_ShowsAGuidedEmptyState()
    {
        await using var harness = await DesktopHarness.StartAsync(new AvaloniaUiDispatcher(), seed: SampleVault.Seed);
        var window = UiTest.ShowMainWindow(harness);

        window.FindControl<NoteListView>("NoteList")!.FocusSearch();
        window.Type("zzzznoexiste");

        await UiTest.WaitForAsync(() => harness.Shell.NoteList.HasNoResults, "the empty state");
        harness.Shell.NoteList.HasNotes.Should().BeFalse();
    }

    [AvaloniaFact]
    public async Task QuickOpen_SearchesLive_OpensWithEnter_AndClosesWithEscape()
    {
        await using var harness = await DesktopHarness.StartAsync(new AvaloniaUiDispatcher(), seed: SampleVault.Seed);
        var window = UiTest.ShowMainWindow(harness);
        var shell = harness.Shell;
        var overlay = window.FindControl<QuickOpenView>("QuickOpen")!;
        overlay.IsVisible.Should().BeFalse();

        window.PressPrimary(Key.K);
        await UiTest.WaitForAsync(() => shell.QuickOpen.IsOpen && shell.QuickOpen.Items.Count == 3, "the overlay with recent notes");
        overlay.IsVisible.Should().BeTrue();

        await UiTest.WaitForAsync(() => overlay.FindDescendant<TextBox>("QueryBox").IsFocused, "focus in the query box");
        window.Type("runbook");
        await UiTest.WaitForAsync(() => shell.QuickOpen.Items.Count == 1, "the live results");

        window.Press(Key.Enter);
        await UiTest.WaitForAsync(() => shell.Editor.Path?.Value == "runbooks/despliegue.md", "the note to open");
        shell.QuickOpen.IsOpen.Should().BeFalse();

        window.PressPrimary(Key.P);
        await UiTest.WaitForAsync(() => shell.QuickOpen.IsOpen, "the note switcher");
        await UiTest.WaitForAsync(() => overlay.FindDescendant<TextBox>("QueryBox").IsFocused, "focus in the query box");
        window.Press(Key.Down);
        window.Press(Key.Escape);
        shell.QuickOpen.IsOpen.Should().BeFalse();
        shell.Editor.Path!.Value.Value.Should().Be("runbooks/despliegue.md", "Escape must not open anything");
    }

    [AvaloniaFact]
    public async Task QuickOpen_ClosedWithEscape_GivesTheFocusBackToWhereTheUserWas()
    {
        await using var harness = await DesktopHarness.StartAsync(new AvaloniaUiDispatcher(), seed: SampleVault.Seed);
        var window = UiTest.ShowMainWindow(harness);
        var shell = harness.Shell;
        await OpenAsync(harness, _plain);
        var editor = EditorOf(window);
        editor.TextArea.Focus();
        var overlay = window.FindControl<QuickOpenView>("QuickOpen")!;

        window.PressPrimary(Key.K);
        await UiTest.WaitForAsync(() => overlay.FindDescendant<TextBox>("QueryBox").IsFocused, "focus in the query box");
        window.Press(Key.Escape);

        shell.QuickOpen.IsOpen.Should().BeFalse();
        await UiTest.WaitForAsync(() => editor.TextArea.IsFocused, "focus back in the editor");
        window.Type("sigo escribiendo");
        shell.Editor.Text.Should().Contain("sigo escribiendo", "the keyboard must keep working without touching the mouse");
    }

    [AvaloniaFact]
    public async Task FocusListShortcut_MovesTheKeyboardToTheSearchBox_EvenFromTheEditor()
    {
        await using var harness = await DesktopHarness.StartAsync(new AvaloniaUiDispatcher(), seed: SampleVault.Seed);
        var window = UiTest.ShowMainWindow(harness);
        var shell = harness.Shell;
        await OpenAsync(harness, _plain);
        EditorOf(window).TextArea.Focus();

        window.PressPrimary(Key.F, shift: true);

        var searchBox = window.FindControl<NoteListView>("NoteList")!.FindDescendant<TextBox>("SearchBox");
        await UiTest.WaitForAsync(() => searchBox.IsFocused, "focus in the search box");
        window.Type("runbook");
        await UiTest.WaitForAsync(() => shell.NoteList.Items.Count == 1, "the filtered list");
    }

    [AvaloniaFact]
    public async Task ExternalReload_DropsTheUndoHistory_SoUndoCannotBringBackAStaleVersion()
    {
        await using var harness = await DesktopHarness.StartAsync(new AvaloniaUiDispatcher(), seed: SampleVault.Seed);
        var window = UiTest.ShowMainWindow(harness);
        var shell = harness.Shell;
        await OpenAsync(harness, _plain);
        var editor = EditorOf(window);
        editor.TextArea.Focus();
        editor.CaretOffset = editor.Document.TextLength;
        window.Type("tecleado");
        window.PressPrimary(Key.S);
        await UiTest.WaitForAsync(() => shell.Editor.State == SaveState.Saved, "the save to finish");
        editor.Document.UndoStack.CanUndo.Should().BeTrue();

        // A git pull (or another editor) replaces the note while it is open and clean.
        harness.VaultFolder.Write("apuntes.md", "# Versión que llegó con git pull\n");
        await UiTest.WaitForAsync(() => editor.Text.Contains("git pull", StringComparison.Ordinal), "the reload from disk");
        Dispatcher.UIThread.RunJobs();

        editor.Document.UndoStack.CanUndo.Should().BeFalse(
            "undoing would restore text based on the old version and the next autosave would overwrite the pulled one without any conflict");
    }

    [AvaloniaFact]
    public async Task Editor_FollowsTheReadOnlyStateOfTheViewModel()
    {
        await using var harness = await DesktopHarness.StartAsync(new AvaloniaUiDispatcher(), seed: SampleVault.Seed);
        var window = UiTest.ShowMainWindow(harness);
        await OpenAsync(harness, _plain);
        var editor = EditorOf(window);
        editor.TextArea.Focus();
        var before = editor.Text;

        using (harness.Shell.Editor.SuspendEditing())
        {
            editor.IsReadOnly.Should().BeTrue();
            window.Type("no debe entrar");
            editor.Text.Should().Be(before);
        }

        editor.IsReadOnly.Should().BeFalse();
        window.Type("sí entra");
        editor.Text.Should().Contain("sí entra");
    }

    [AvaloniaFact]
    public async Task CommandPalette_RunsACommandByName()
    {
        await using var harness = await DesktopHarness.StartAsync(new AvaloniaUiDispatcher(), seed: SampleVault.Seed);
        var window = UiTest.ShowMainWindow(harness);
        var shell = harness.Shell;
        var overlay = window.FindControl<QuickOpenView>("QuickOpen")!;

        window.PressPrimary(Key.P, shift: true);
        await UiTest.WaitForAsync(() => shell.QuickOpen is { IsOpen: true, Mode: QuickOpenMode.Commands }, "the command palette");
        shell.QuickOpen.Items.Count.Should().BeGreaterThan(10, "every available action is listed");

        await UiTest.WaitForAsync(() => overlay.FindDescendant<TextBox>("QueryBox").IsFocused, "focus in the query box");
        window.Type(Resources.Strings.Command_ToggleSidebar);
        await UiTest.WaitForAsync(() => shell.QuickOpen.Items.Count == 1, "the filtered commands");
        window.Press(Key.Enter);

        await UiTest.WaitForAsync(() => shell.IsSidebarCollapsed, "the sidebar to collapse");
        shell.QuickOpen.IsOpen.Should().BeFalse();
    }

    [AvaloniaFact]
    public async Task Shortcuts_TogglePanelsAndViewModes()
    {
        await using var harness = await DesktopHarness.StartAsync(new AvaloniaUiDispatcher(), seed: SampleVault.Seed);
        var window = UiTest.ShowMainWindow(harness);
        var shell = harness.Shell;
        await OpenAsync(harness, _plain);
        var sidebar = window.FindControl<SidebarView>("Sidebar")!;
        var inspector = window.FindControl<InspectorView>("Inspector")!;
        var editor = EditorOf(window);
        var preview = window.FindControl<NoteEditorView>("EditorView")!.FindDescendant<MarkdownView>();

        sidebar.IsEffectivelyVisible.Should().BeTrue();
        window.PressPrimary(Key.B);
        await UiTest.WaitForAsync(() => !sidebar.IsEffectivelyVisible, "the sidebar to hide");
        window.PressPrimary(Key.B);
        await UiTest.WaitForAsync(() => sidebar.IsEffectivelyVisible, "the sidebar to return");

        window.PressPrimary(Key.I, shift: true);
        await UiTest.WaitForAsync(() => !inspector.IsEffectivelyVisible, "the details panel to hide");

        (editor.IsEffectivelyVisible, preview.IsEffectivelyVisible).Should().Be((true, true), "split is the default mode");
        window.PressPrimary(Key.E);
        (shell.Editor.ViewMode, editor.IsEffectivelyVisible, preview.IsEffectivelyVisible).Should().Be((EditorViewMode.Preview, false, true));
        window.PressPrimary(Key.E);
        (shell.Editor.ViewMode, editor.IsEffectivelyVisible, preview.IsEffectivelyVisible).Should().Be((EditorViewMode.Editor, true, false));
        window.PressPrimary(Key.E);
        shell.Editor.ViewMode.Should().Be(EditorViewMode.Split);
    }

    [AvaloniaFact]
    public async Task Rename_F2OpensTheDialog_EscapeCancels_AndShortcutsAreInertWhileADialogIsOpen()
    {
        await using var harness = await DesktopHarness.StartAsync(new AvaloniaUiDispatcher(), seed: SampleVault.Seed);
        var window = UiTest.ShowMainWindow(harness);
        var shell = harness.Shell;
        await OpenAsync(harness, _plain);

        window.Press(Key.F2);
        await UiTest.WaitForAsync(() => shell.Dialogs.Current is PromptDialogViewModel, "the rename dialog");

        window.PressPrimary(Key.B);
        shell.IsSidebarCollapsed.Should().BeFalse("window shortcuts do nothing while a modal dialog is open");
        window.PressPrimary(Key.N);
        shell.Dialogs.Current.Should().BeOfType<PromptDialogViewModel>().Which.Title.Should().Be(Resources.Strings.Dialog_Rename_Title);

        window.Press(Key.Escape);
        await UiTest.WaitForAsync(() => !shell.Dialogs.IsOpen, "the dialog to close");
        harness.VaultFolder.Exists("apuntes.md").Should().BeTrue("cancelling renames nothing");
    }

    [AvaloniaFact]
    public async Task DestructiveConfirmation_FocusesCancel_SoEnterDoesNotDelete()
    {
        await using var harness = await DesktopHarness.StartAsync(new AvaloniaUiDispatcher(), seed: SampleVault.Seed);
        var window = UiTest.ShowMainWindow(harness);
        var shell = harness.Shell;
        await OpenAsync(harness, _plain);

        var deletion = shell.DeleteCommand.ExecuteAsync(null);
        await UiTest.WaitForAsync(() => shell.Dialogs.Current is ConfirmDialogViewModel, "the confirmation");
        var host = window.FindControl<DialogHostView>("DialogHost")!;
        await UiTest.WaitForAsync(() => host.FindDescendant<Button>("CancelButton").IsFocused, "focus on the safe choice");

        window.Press(Key.Enter);
        await deletion;

        harness.VaultFolder.Exists("apuntes.md").Should().BeTrue("the default action of a destructive dialog is to cancel");
        shell.Editor.HasNote.Should().BeTrue();
    }

    [AvaloniaFact]
    public async Task ConflictBanner_IsShownWithItsActions()
    {
        await using var harness = await DesktopHarness.StartAsync(new AvaloniaUiDispatcher(), seed: SampleVault.Seed);
        var window = UiTest.ShowMainWindow(harness);
        var shell = harness.Shell;
        await OpenAsync(harness, _plain);
        var editorView = window.FindControl<NoteEditorView>("EditorView")!;
        var banner = editorView.FindDescendants<Border>().First(border => border.Classes.Contains("banner"));
        banner.IsEffectivelyVisible.Should().BeFalse();

        shell.Editor.Text += "\nmío\n";
        harness.VaultFolder.Write("apuntes.md", "# externo\n");
        await UiTest.WaitForAsync(() => shell.Editor.IsConflict, "the conflict");
        Dispatcher.UIThread.RunJobs();

        banner.IsEffectivelyVisible.Should().BeTrue();
        banner.FindDescendants<Button>().Count(button => button.IsEffectivelyVisible).Should().Be(3, "keep mine, load disk version, save a copy");
    }

    [AvaloniaFact]
    public async Task Notification_IsRenderedAsAToast()
    {
        await using var harness = await DesktopHarness.StartAsync(new AvaloniaUiDispatcher(), seed: SampleVault.Seed);
        var window = UiTest.ShowMainWindow(harness);

        harness.Shell.Notification.Show("Algo salió mal", NotificationKind.Error);
        Dispatcher.UIThread.RunJobs();

        var toast = window.FindControl<TextBlock>("ToastText")!;
        toast.IsEffectivelyVisible.Should().BeTrue();
        toast.Text.Should().Be("Algo salió mal");
    }

    [AvaloniaFact]
    public async Task ClosingTheWindow_FlushesPendingChanges()
    {
        await using var harness = await DesktopHarness.StartAsync(new AvaloniaUiDispatcher(), seed: SampleVault.Seed);
        var window = UiTest.ShowMainWindow(harness);
        var shell = harness.Shell;
        await OpenAsync(harness, _plain);
        shell.Editor.Text += "\nPendiente al cerrar.\n";
        var closed = false;
        window.Closed += (_, _) => closed = true;

        window.Close();
        await UiTest.WaitForAsync(() => closed, "the window to close after saving");

        harness.VaultFolder.Read("apuntes.md").Should().Contain("Pendiente al cerrar.");
        harness.Settings.Current.Layout.WindowWidth.Should().Be(window.Width);
    }

    /// <summary>Opens a note and lets layout attach the editor view (it is hidden while no note is open).</summary>
    private static async Task OpenAsync(DesktopHarness harness, NotePath path)
    {
        (await harness.Shell.Editor.OpenAsync(path)).Should().BeTrue();
        Dispatcher.UIThread.RunJobs();
    }

    private static TextEditor EditorOf(Window window) =>
        window.FindControl<NoteEditorView>("EditorView")!.TextEditor;
}
