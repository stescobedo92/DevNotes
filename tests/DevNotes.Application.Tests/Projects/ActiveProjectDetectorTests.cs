using DevNotes.Application.Abstractions;
using DevNotes.Application.Projects;
using DevNotes.Application.Settings;

namespace DevNotes.Application.Tests.Projects;

public sealed class ActiveProjectDetectorTests
{
    private static readonly string _root = Path.Combine(Path.GetTempPath(), "repos", "cslinq");
    private static readonly GitRepositoryInfo _repository = new(_root, "main", null);

    [Fact]
    public void Detect_WithoutRepository_IsNull()
    {
        ActiveProjectDetector.Detect(null, [new ProjectSettings("cslinq", _root)], ["cslinq"]).Should().BeNull();
    }

    [Fact]
    public void Detect_ConfiguredRepositoryPath_Wins()
    {
        var projects = new[]
        {
            new ProjectSettings("other", Path.Combine(Path.GetTempPath(), "repos", "other")),
            new ProjectSettings("Mi Proyecto", _root + Path.DirectorySeparatorChar),
        };

        var active = ActiveProjectDetector.Detect(_repository, projects, ["cslinq"]);

        active.Should().Be(new ActiveProject("Mi Proyecto", _repository, IsConfigured: true));
    }

    [Fact]
    public void Detect_RepositoryInsideTheConfiguredFolder_Matches()
    {
        var nested = new GitRepositoryInfo(Path.Combine(_root, "worktrees", "feature"), "feature", null);

        var active = ActiveProjectDetector.Detect(nested, [new ProjectSettings("cslinq", _root)], []);

        active!.Name.Should().Be("cslinq");
        active.IsConfigured.Should().BeTrue();
    }

    [Fact]
    public void Detect_SiblingWithACommonPrefix_DoesNotMatch()
    {
        var sibling = new GitRepositoryInfo(_root + "-extra", "main", null);

        ActiveProjectDetector.Detect(sibling, [new ProjectSettings("cslinq", _root)], []).Should().BeNull();
    }

    [Fact]
    public void Detect_FallsBackToTheFolderName_WhenSuchAProjectExists()
    {
        var active = ActiveProjectDetector.Detect(_repository, [], ["Azure", "CSLinq"]);

        active.Should().Be(new ActiveProject("CSLinq", _repository, IsConfigured: false));
        ActiveProjectDetector.Detect(_repository, [], ["Azure"]).Should().BeNull();
    }

    [Fact]
    public void Detect_UnusableConfiguredPath_IsIgnored()
    {
        var active = ActiveProjectDetector.Detect(_repository, [new ProjectSettings("x", "::not a path::\0")], ["cslinq"]);

        active!.Name.Should().Be("cslinq");
    }
}
