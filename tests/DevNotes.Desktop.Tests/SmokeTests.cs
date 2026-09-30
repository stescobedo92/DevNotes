using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Styling;
using Avalonia.Threading;
using DevNotes.Application.Settings;
using DevNotes.Desktop.Services;
using DevNotes.Desktop.Tests.Support;
using DevNotes.Desktop.Views;
using DevNotes.Domain.Notes;

namespace DevNotes.Desktop.Tests;

/// <summary>
/// Renders the real window with the real composition. Each test also saves a screenshot next to
/// the test binaries (folder "screenshots") so the visual result can be inspected.
/// </summary>
public sealed class SmokeTests
{
    [AvaloniaFact]
    public async Task FirstRun_ShowsWelcomeScreen()
    {
        await using var harness = await DesktopHarness.StartAsync(new AvaloniaUiDispatcher(), openVault: false);
        var window = Show(harness);

        harness.Shell.ShowWelcome.Should().BeTrue();
        window.FindControl<Button>("ChooseFolderButton")!.IsEffectivelyVisible.Should().BeTrue();
        window.FindControl<Grid>("Shell")!.IsVisible.Should().BeFalse();
        Capture(window, "welcome-dark");
    }

    [AvaloniaTheory]
    [InlineData(AppTheme.Dark)]
    [InlineData(AppTheme.Light)]
    public async Task MainWindow_WithVaultAndOpenNote_RendersAllPanels(AppTheme theme)
    {
        await using var harness = await DesktopHarness.StartAsync(new AvaloniaUiDispatcher(), seed: SampleVault.Seed);
        Avalonia.Application.Current!.RequestedThemeVariant = theme == AppTheme.Light ? ThemeVariant.Light : ThemeVariant.Dark;
        harness.Theme.Apply(harness.Settings.Current with { Theme = theme });
        var window = Show(harness);

        (await harness.Shell.Editor.OpenAsync(NotePath.Create("bugs/deadlock-inventario.md"))).Should().BeTrue();
        Dispatcher.UIThread.RunJobs();

        harness.Shell.NoteList.Items.Should().HaveCount(3);
        harness.Shell.Editor.Title.Should().Be("Deadlock en actualización de inventario");
        window.FindControl<NoteEditorView>("EditorView")!.IsEffectivelyVisible.Should().BeTrue();
        window.FindControl<InspectorView>("Inspector")!.IsEffectivelyVisible.Should().BeTrue();
        window.FindControl<SidebarView>("Sidebar")!.IsEffectivelyVisible.Should().BeTrue();
        Capture(window, $"main-{theme.ToString().ToLowerInvariant()}");

        Avalonia.Application.Current.RequestedThemeVariant = ThemeVariant.Dark;
    }

    private static MainWindow Show(DesktopHarness harness)
    {
        var window = new MainWindow { DataContext = harness.Shell };
        window.ApplyLayout(harness.Shell.Layout);
        window.Show();
        Dispatcher.UIThread.RunJobs();
        return window;
    }

    private static void Capture(Window window, string name)
    {
        Dispatcher.UIThread.RunJobs();
        var directory = Path.Combine(AppContext.BaseDirectory, "screenshots");
        Directory.CreateDirectory(directory);
        using var frame = window.CaptureRenderedFrame();
        frame.Should().NotBeNull("the headless platform renders with Skia");
        frame!.Save(Path.Combine(directory, name + ".png"), Avalonia.Media.Imaging.PngBitmapEncoderOptions.Default);
    }
}
