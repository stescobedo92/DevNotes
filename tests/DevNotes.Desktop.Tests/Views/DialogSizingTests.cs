using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Threading;
using DevNotes.Desktop.Services;
using DevNotes.Desktop.Tests.Support;
using DevNotes.Desktop.ViewModels.Dialogs;
using DevNotes.Desktop.Views;

namespace DevNotes.Desktop.Tests.Views;

public sealed class DialogSizingTests
{
    [AvaloniaFact]
    public async Task WideDialogs_GetTheWideCard_AndOthersTheStandardOne()
    {
        await using var harness = await DesktopHarness.StartAsync(new AvaloniaUiDispatcher(), seed: SampleVault.Seed);
        var window = UiTest.ShowMainWindow(harness);
        var shell = harness.Shell;
        var card = window.FindControl<DialogHostView>("DialogHost")!.FindControl<Border>("Card")!;

        var settings = shell.OpenSettingsCommand.ExecuteAsync(null);
        Dispatcher.UIThread.RunJobs();
        card.Classes.Should().Contain("wide");
        card.Width.Should().Be(680);
        shell.Dialogs.CancelCurrent();
        await settings;

        var newNote = shell.NewNoteCommand.ExecuteAsync(null);
        await UiTest.WaitForAsync(() => shell.Dialogs.Current is NewNoteDialogViewModel, "the new note dialog");
        card.Classes.Should().NotContain("wide");
        card.Width.Should().Be(460);
        shell.Dialogs.CancelCurrent();
        await newNote;
    }

    [AvaloniaFact]
    public async Task Escape_ClosesTheTemplatesDialog_FromItsInitialFocus()
    {
        await using var harness = await DesktopHarness.StartAsync(new AvaloniaUiDispatcher(), seed: SampleVault.Seed);
        var window = UiTest.ShowMainWindow(harness);
        var shell = harness.Shell;

        var opening = shell.EditTemplatesCommand.ExecuteAsync(null);
        await UiTest.WaitForAsync(() => shell.Dialogs.Current is TemplatesDialogViewModel, "the templates dialog");
        await UiTest.WaitForAsync(() => window.FocusManager?.GetFocusedElement() is Control { Name: "TemplateList" } or ListBoxItem, "focus in the list");

        window.Press(Key.Escape);
        await opening;

        shell.Dialogs.IsOpen.Should().BeFalse();
    }
}
