using Avalonia.Headless.XUnit;
using DevNotes.Desktop.Services;
using DevNotes.Desktop.Tests.Support;
using DevNotes.Desktop.ViewModels;
using DevNotes.Desktop.ViewModels.Dialogs;

namespace DevNotes.Desktop.Tests.ViewModels;

public sealed class FilterPanelTests
{
    [AvaloniaFact]
    public async Task Facets_ShowProjectsTagsAndTypesWithCounts()
    {
        await using var harness = await DesktopHarness.StartAsync(new AvaloniaUiDispatcher(), seed: SampleVault.Seed);
        var filters = harness.Shell.Filters;

        filters.Projects.Select(item => (item.Value, item.Count)).Should().BeEquivalentTo([("azure-microservices", 1), ("oatpp-api", 1)]);
        filters.Tags.Select(item => item.Value).Should().BeEquivalentTo(["sql-server", "deadlock", "performance", "azure", "deploy"]);
        filters.Tags.Should().OnlyContain(item => item.Count == 1 && item.Label == "#" + item.Value);
        filters.Types.Select(item => (item.Value, item.Count)).Should().BeEquivalentTo([("bug", 1), ("runbook", 1), ("note", 1)]);
        filters.HasActiveFilter.Should().BeFalse();
        filters.ProjectNames.Should().BeEquivalentTo(["azure-microservices", "oatpp-api"]);
    }

    [AvaloniaFact]
    public async Task SelectingFacets_FiltersTheList_AndCombines()
    {
        await using var harness = await DesktopHarness.StartAsync(new AvaloniaUiDispatcher(), seed: SampleVault.Seed);
        var shell = harness.Shell;
        var filters = shell.Filters;

        filters.Projects.Single(item => item.Value == "azure-microservices").IsSelected = true;
        await shell.NoteList.WhenSettledAsync();

        shell.NoteList.Items.Select(item => item.Path.Value).Should().Equal("bugs/deadlock-inventario.md");
        shell.NoteList.HasFilter.Should().BeTrue();
        filters.ActiveCountText.Should().Contain("1");

        // A second project widens the selection (alternatives); a tag narrows it (all required).
        filters.Projects.Single(item => item.Value == "oatpp-api").IsSelected = true;
        await shell.NoteList.WhenSettledAsync();
        shell.NoteList.Items.Should().HaveCount(2);

        filters.Tags.Single(item => item.Value == "deploy").IsSelected = true;
        await shell.NoteList.WhenSettledAsync();
        shell.NoteList.Items.Select(item => item.Path.Value).Should().Equal("runbooks/despliegue.md");

        filters.Types.Single(item => item.Value == "bug").IsSelected = true;
        await shell.NoteList.WhenSettledAsync();
        shell.NoteList.Items.Should().BeEmpty();
        shell.NoteList.HasNoResults.Should().BeTrue("a filter that matches nothing is 'no results', not 'empty vault'");

        shell.NoteList.ClearFilterCommand.Execute(null);
        await shell.NoteList.WhenSettledAsync();
        filters.HasActiveFilter.Should().BeFalse();
        shell.NoteList.Items.Should().HaveCount(3);
    }

    [AvaloniaFact]
    public async Task TypedFilters_CombineWithTheSidebar_AndTheQuickOpenIgnoresTheSidebar()
    {
        await using var harness = await DesktopHarness.StartAsync(new AvaloniaUiDispatcher(), seed: SampleVault.Seed);
        var shell = harness.Shell;

        shell.NoteList.SearchText = "type:runbook";
        await shell.NoteList.WhenSettledAsync();
        shell.NoteList.Items.Select(item => item.Path.Value).Should().Equal("runbooks/despliegue.md");

        shell.Filters.Projects.Single(item => item.Value == "azure-microservices").IsSelected = true;
        await shell.NoteList.WhenSettledAsync();
        shell.NoteList.Items.Should().BeEmpty("runbook AND azure-microservices matches nothing");

        shell.NoteList.SearchText = "-deploy";
        await shell.NoteList.WhenSettledAsync();
        shell.NoteList.Items.Select(item => item.Path.Value).Should().Equal("bugs/deadlock-inventario.md");

        shell.QuickOpen.Open(QuickOpenMode.Notes);
        await UiTest.WaitForAsync(() => shell.QuickOpen.Items.Count == 3, "quick open ignores the sidebar selection");
        shell.QuickOpen.Close();
    }

    [AvaloniaFact]
    public async Task Facets_FollowTheNotes_AndKeepTheSelection()
    {
        await using var harness = await DesktopHarness.StartAsync(new AvaloniaUiDispatcher(), seed: SampleVault.Seed);
        var shell = harness.Shell;
        shell.Filters.Tags.Single(item => item.Value == "azure").IsSelected = true;

        harness.VaultFolder.Write("nueva.md", "---\ntitle: Nueva\nproject: oatpp-api\ntags: [azure, nueva]\n---\n# Nueva\n");
        await UiTest.WaitForAsync(() => shell.Filters.Tags.Any(item => item.Value == "nueva"), "the facets to pick up the new note");
        await harness.SettleAsync();

        shell.Filters.Projects.Single(item => item.Value == "oatpp-api").Count.Should().Be(2);
        shell.Filters.Tags.Single(item => item.Value == "azure").Should().Match<FacetItemViewModel>(item => item.Count == 2 && item.IsSelected);
        shell.NoteList.Items.Should().HaveCount(2);
    }

    [AvaloniaFact]
    public async Task ToggleProject_SelectsOnlyThatProject_AndTogglesItOff()
    {
        await using var harness = await DesktopHarness.StartAsync(new AvaloniaUiDispatcher(), seed: SampleVault.Seed);
        var filters = harness.Shell.Filters;
        filters.Projects.Single(item => item.Value == "oatpp-api").IsSelected = true;

        filters.ToggleProject("AZURE-MICROSERVICES");

        filters.Projects.Where(item => item.IsSelected).Select(item => item.Value).Should().Equal("azure-microservices");

        filters.ToggleProject("azure-microservices");

        filters.HasActiveFilter.Should().BeFalse();
        filters.ToggleProject("unknown");
        filters.HasActiveFilter.Should().BeFalse();
    }

    [AvaloniaFact]
    public async Task ClosingTheVault_ClearsTheFacets()
    {
        await using var harness = await DesktopHarness.StartAsync(new AvaloniaUiDispatcher(), seed: SampleVault.Seed);
        var shell = harness.Shell;
        shell.Filters.Projects[0].IsSelected = true;

        var removal = shell.RemoveVaultCommand.ExecuteAsync(shell.Vaults[0]);
        ((ConfirmDialogViewModel)shell.Dialogs.Current!).ConfirmCommand.Execute(null);
        await removal;
        shell.HasVault.Should().BeFalse();

        shell.Filters.Projects.Should().BeEmpty();
        shell.Filters.HasActiveFilter.Should().BeFalse();
    }
}
