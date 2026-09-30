using DevNotes.Application.Abstractions;
using DevNotes.Infrastructure.Git;

namespace DevNotes.Infrastructure.Tests.Git;

public sealed class GitRepositoryLocatorTests : IDisposable
{
    private readonly TempDirectory _temp = new("devnotes-git-");
    private readonly GitRepositoryLocator _locator = new();

    public void Dispose() => _temp.Dispose();

    [Fact]
    public void Find_NoRepository_IsNull()
    {
        Directory.CreateDirectory(_temp.Combine("plain", "nested"));

        _locator.Find(_temp.Combine("plain", "nested")).Should().BeNull();
        _locator.Find(_temp.Combine("does-not-exist")).Should().BeNull();
        _locator.Find(null).Should().BeNull();
        _locator.Find("  ").Should().BeNull();
    }

    [Fact]
    public void Find_BranchCheckedOut_ReturnsRootAndBranch()
    {
        var root = CreateRepository("repo", "ref: refs/heads/feature/phase-2\n");
        Directory.CreateDirectory(Path.Combine(root, "src", "deep"));
        File.WriteAllText(Path.Combine(root, "src", "deep", "file.cs"), "x");

        var fromNestedFolder = _locator.Find(Path.Combine(root, "src", "deep"));
        var fromFile = _locator.Find(Path.Combine(root, "src", "deep", "file.cs"));

        fromNestedFolder.Should().Be(new GitRepositoryInfo(root, "feature/phase-2", null));
        fromFile.Should().Be(fromNestedFolder);
        fromNestedFolder!.Display.Should().Be("feature/phase-2");
    }

    [Fact]
    public void Find_DetachedHead_ReturnsShortCommit()
    {
        var root = CreateRepository("repo", "a3f9c21d0f3b7a1c9e5d2b4f6a8c0e1d3f5b7a9c\n");

        var info = _locator.Find(root);

        info.Should().Be(new GitRepositoryInfo(root, null, "a3f9c21"));
        info!.Display.Should().Be("a3f9c21");
    }

    [Fact]
    public void Find_WorktreeGitFile_FollowsTheGitDir()
    {
        var main = CreateRepository("main", "ref: refs/heads/main\n");
        var worktreeGitDir = Directory.CreateDirectory(Path.Combine(main, ".git", "worktrees", "feature")).FullName;
        File.WriteAllText(Path.Combine(worktreeGitDir, "HEAD"), "ref: refs/heads/feature\n");
        var worktree = Directory.CreateDirectory(_temp.Combine("feature-wt")).FullName;
        File.WriteAllText(Path.Combine(worktree, ".git"), $"gitdir: {Path.Combine("..", "main", ".git", "worktrees", "feature")}\n");

        var info = _locator.Find(worktree);

        info.Should().Be(new GitRepositoryInfo(worktree, "feature", null));
    }

    [Fact]
    public void Find_NestedRepository_ReturnsTheInnermost()
    {
        var outer = CreateRepository("outer", "ref: refs/heads/main\n");
        var inner = CreateRepository(Path.Combine("outer", "libs", "inner"), "ref: refs/heads/dev\n");

        _locator.Find(inner)!.RootPath.Should().Be(inner);
        _locator.Find(Path.Combine(outer, "libs"))!.RootPath.Should().Be(outer);
    }

    [Theory]
    [InlineData("")]
    [InlineData("garbage\n")]
    [InlineData("ref: \n")]
    public void Find_UnreadableHead_StillReportsTheRoot(string head)
    {
        var root = CreateRepository("repo", head);

        _locator.Find(root).Should().Be(new GitRepositoryInfo(root, null, null));
    }

    [Fact]
    public void Find_GitFileThatIsNotAWorktree_IsNotARepository()
    {
        var folder = Directory.CreateDirectory(_temp.Combine("odd")).FullName;
        File.WriteAllText(Path.Combine(folder, ".git"), "something else\n");

        _locator.Find(folder).Should().BeNull();
    }

    private string CreateRepository(string relative, string head)
    {
        var root = Directory.CreateDirectory(_temp.Combine(relative)).FullName;
        Directory.CreateDirectory(Path.Combine(root, ".git"));
        File.WriteAllText(Path.Combine(root, ".git", "HEAD"), head);
        return Path.TrimEndingDirectorySeparator(root);
    }
}
