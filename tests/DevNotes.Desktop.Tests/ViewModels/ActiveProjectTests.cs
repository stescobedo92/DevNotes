using Avalonia.Headless.XUnit;
using DevNotes.Desktop.Resources;
using DevNotes.Desktop.Services;
using DevNotes.Desktop.Tests.Support;
using Microsoft.Extensions.DependencyInjection;

namespace DevNotes.Desktop.Tests.ViewModels;

public sealed class ActiveProjectTests
{
    [AvaloniaFact]
    public async Task StartedInsideARepositoryNamedLikeAProject_DetectsIt_AndFiltersOnDemand()
    {
        using var repository = new TempDirectory("devnotes-repo-");
        var root = Path.Combine(repository.Path, "oatpp-api");
        CreateRepository(root, "ref: refs/heads/feature/x\n");
        await using var harness = await DesktopHarness.StartAsync(
            new AvaloniaUiDispatcher(),
            seed: SampleVault.Seed,
            configure: services => services.AddSingleton(new LaunchContext(Path.Combine(root, "src"))));
        var shell = harness.Shell;

        shell.ActiveProject.Should().NotBeNull();
        shell.ActiveProject!.Name.Should().Be("oatpp-api");
        shell.ActiveProject.IsConfigured.Should().BeFalse();
        shell.ActiveProject.Repository.Branch.Should().Be("feature/x");
        shell.ActiveProjectText.Should().Be(ErrorMessages.Format(Strings.Status_ActiveProject, "oatpp-api"));
        shell.Capture.DefaultProject.Should().Be("oatpp-api");
        shell.FilterActiveProjectCommand.CanExecute(null).Should().BeTrue();

        shell.FilterActiveProjectCommand.Execute(null);
        await shell.NoteList.WhenSettledAsync();

        shell.NoteList.Items.Select(item => item.Path.Value).Should().Equal("runbooks/despliegue.md");

        shell.FilterActiveProjectCommand.Execute(null);
        await shell.NoteList.WhenSettledAsync();
        shell.NoteList.Items.Should().HaveCount(3);
    }

    [AvaloniaFact]
    public async Task ConfiguredRepositoryPath_WinsOverTheFolderName()
    {
        using var repository = new TempDirectory("devnotes-repo-");
        var root = Path.Combine(repository.Path, "some-checkout");
        CreateRepository(root, "ref: refs/heads/main\n");
        await using var harness = await DesktopHarness.StartAsync(
            new AvaloniaUiDispatcher(),
            seed: SampleVault.Seed,
            configure: services => services.AddSingleton(new LaunchContext(root)));
        var shell = harness.Shell;
        shell.ActiveProject.Should().BeNull("no project is called some-checkout");

        await harness.Settings.UpdateAsync(settings => settings.WithProjectRepository("azure-microservices", root), CancellationToken.None);
        var opening = shell.OpenSettingsCommand.ExecuteAsync(null);
        shell.Dialogs.CancelCurrent();
        await opening;

        shell.ActiveProject!.Name.Should().Be("azure-microservices");
        shell.ActiveProject.IsConfigured.Should().BeTrue();
    }

    [AvaloniaFact]
    public async Task VaultInsideARepository_ShowsTheBranch()
    {
        await using var harness = await DesktopHarness.StartAsync(new AvaloniaUiDispatcher(), seed: vault =>
        {
            SampleVault.Seed(vault);
            CreateRepository(vault.Path, "ref: refs/heads/notes-main\n");
        });

        harness.Shell.BranchText.Should().Be("notes-main");
        harness.Shell.HasBranch.Should().BeTrue();
        harness.Shell.BranchTip.Should().Be(ErrorMessages.Format(Strings.Status_Branch, "notes-main"));
    }

    [AvaloniaFact]
    public async Task NoRepositoryAnywhere_ShowsNothing()
    {
        await using var harness = await DesktopHarness.StartAsync(new AvaloniaUiDispatcher(), seed: SampleVault.Seed);

        harness.Shell.HasBranch.Should().BeFalse();
        harness.Shell.HasActiveProject.Should().BeFalse();
        harness.Shell.FilterActiveProjectCommand.CanExecute(null).Should().BeFalse();
    }

    private static void CreateRepository(string root, string head)
    {
        Directory.CreateDirectory(Path.Combine(root, ".git"));
        Directory.CreateDirectory(Path.Combine(root, "src"));
        File.WriteAllText(Path.Combine(root, ".git", "HEAD"), head);
    }
}
