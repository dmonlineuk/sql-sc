using SqlSc.Core.Export;

namespace SqlSc.Core.Tests;

public sealed class GitSyncTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "sqlsc-git-" + Guid.NewGuid().ToString("N"));

    public GitSyncTests() => Directory.CreateDirectory(root);

    public void Dispose()
    {
        foreach (var file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
        {
            File.SetAttributes(file, FileAttributes.Normal);
        }

        Directory.Delete(root, recursive: true);
    }

    [Fact]
    public void ReportsAheadAndBehindAndPullsAndPushes()
    {
        var remote = Path.Combine(root, "remote.git");
        Git.RunOrThrow(root, "init", "--quiet", "--bare", "-b", "main", remote);
        var mine = Clone(remote, "mine");
        Commit(mine, "a.sql", "one");
        Assert.Equal(new GitState("main", null, 0, 0, []), GitSync.State(mine) with { ChangedFiles = [] });

        GitSync.Push(mine);
        Assert.Equal(("main", "origin/main", 0, 0), Summary(GitSync.State(mine)));

        var theirs = Clone(remote, "theirs");
        Commit(theirs, "b.sql", "two");
        GitSync.Push(theirs);
        Commit(mine, "c.sql", "three");
        GitSync.Fetch(mine);
        Assert.Equal(("main", "origin/main", 1, 1), Summary(GitSync.State(mine)));

        var error = Assert.Throws<InvalidDataException>(() => GitSync.Pull(mine));
        Assert.Contains("pull --ff-only failed", error.Message, StringComparison.Ordinal);
        Assert.False(File.Exists(Path.Combine(mine, "b.sql")));

        Commit(theirs, "d.sql", "four");
        GitSync.Push(theirs);
        GitSync.Pull(theirs);
        File.WriteAllText(Path.Combine(theirs, "e.sql"), "five");
        Assert.Equal(["?? e.sql"], GitSync.State(theirs).ChangedFiles);
    }

    [Fact]
    public void ListsBranchesAndSwitchesOnlyWithoutUncommittedChanges()
    {
        var remote = Path.Combine(root, "remote.git");
        Git.RunOrThrow(root, "init", "--quiet", "--bare", "-b", "main", remote);
        var theirs = Clone(remote, "theirs");
        Commit(theirs, "a.sql", "one");
        GitSync.Push(theirs);
        GitSync.CreateBranch(theirs, "feature/x");
        Commit(theirs, "b.sql", "two");
        GitSync.Push(theirs);

        var mine = Clone(remote, "mine");
        Assert.Equal([new GitBranch("main", false), new GitBranch("feature/x", true)], GitSync.Branches(mine));

        File.WriteAllText(Path.Combine(mine, "c.sql"), "three");
        var refused = Assert.Throws<InvalidDataException>(() => GitSync.Switch(mine, "feature/x"));
        Assert.Contains("1 uncommitted change", refused.Message, StringComparison.Ordinal);
        Assert.Contains("Commit, stash or discard", refused.Message, StringComparison.Ordinal);
        Assert.Equal("main", GitSync.State(mine).Branch);

        File.Delete(Path.Combine(mine, "c.sql"));
        GitSync.Switch(mine, "feature/x");
        Assert.Equal(("feature/x", "origin/feature/x", 0, 0), Summary(GitSync.State(mine)));
        Assert.True(File.Exists(Path.Combine(mine, "b.sql")));
        Assert.Equal([new GitBranch("feature/x", false), new GitBranch("main", false)], GitSync.Branches(mine));

        File.WriteAllText(Path.Combine(mine, "c.sql"), "three");
        GitSync.CreateBranch(mine, "mine/new");
        Assert.Equal(("mine/new", null, 0, 0), Summary(GitSync.State(mine)));
        Assert.Equal(["?? c.sql"], GitSync.State(mine).ChangedFiles);

        Assert.Throws<InvalidDataException>(() => GitSync.CreateBranch(mine, "bad name"));
        Assert.Throws<InvalidDataException>(() => GitSync.Switch(mine, "--orphan"));
    }

    [Fact]
    public void UncommittedChangesCanBeCommittedStashedOrDiscardedBeforeSwitching()
    {
        var repo = Path.Combine(root, "repo");
        Git.RunOrThrow(root, "init", "--quiet", "-b", "main", repo);
        Git.RunOrThrow(repo, "config", "user.email", "test@example.com");
        Git.RunOrThrow(repo, "config", "user.name", "Test");
        Commit(repo, "a.sql", "one");
        Commit(repo, "b.sql", "two");
        GitSync.CreateBranch(repo, "test");

        File.Delete(Path.Combine(repo, "a.sql"));
        File.WriteAllText(Path.Combine(repo, "c.sql"), "three");
        Assert.Throws<InvalidDataException>(() => GitSync.Switch(repo, "main", UncommittedChanges.Commit));
        GitSync.Switch(repo, "main", UncommittedChanges.Commit, "Test deletions");
        Assert.Equal(("main", 0), (GitSync.State(repo).Branch, GitSync.State(repo).ChangedFiles.Count));
        Assert.True(File.Exists(Path.Combine(repo, "a.sql")));
        Assert.False(File.Exists(Path.Combine(repo, "c.sql")));
        Assert.Equal("Test deletions", Git.RunOrThrow(repo, "log", "-1", "--format=%s", "test"));
        Assert.Equal("D\ta.sql\nA\tc.sql", Git.RunOrThrow(repo, "show", "--format=", "--name-status", "test").ReplaceLineEndings("\n"));

        File.WriteAllText(Path.Combine(repo, "b.sql"), "changed");
        File.WriteAllText(Path.Combine(repo, "d.sql"), "four");
        GitSync.Switch(repo, "test", UncommittedChanges.Stash);
        Assert.Equal(("test", 0), (GitSync.State(repo).Branch, GitSync.State(repo).ChangedFiles.Count));
        var stash = Assert.Single(GitSync.Stashes(repo));
        Assert.Equal("On main: sql-sc: before switching to test", stash.Description);

        File.WriteAllText(Path.Combine(repo, "e.sql"), "five");
        Assert.Throws<InvalidDataException>(() => GitSync.RestoreStash(repo, stash.Id));
        GitSync.Discard(repo);
        Assert.False(File.Exists(Path.Combine(repo, "e.sql")));

        GitSync.Switch(repo, "main");
        GitSync.RestoreStash(repo, stash.Id);
        Assert.Empty(GitSync.Stashes(repo));
        Assert.Equal("changed", File.ReadAllText(Path.Combine(repo, "b.sql")));
        Assert.Equal("four", File.ReadAllText(Path.Combine(repo, "d.sql")));

        GitSync.Stash(repo, "keep");
        Assert.Throws<InvalidDataException>(() => GitSync.RestoreStash(repo, "0000"));
        GitSync.DropStash(repo, Assert.Single(GitSync.Stashes(repo)).Id);
        Assert.Empty(GitSync.Stashes(repo));

        File.Delete(Path.Combine(repo, "a.sql"));
        File.WriteAllText(Path.Combine(repo, "f.sql"), "six");
        GitSync.Switch(repo, "test", UncommittedChanges.Discard);
        GitSync.Switch(repo, "main");
        Assert.Empty(GitSync.State(repo).ChangedFiles);
        Assert.True(File.Exists(Path.Combine(repo, "a.sql")));
        Assert.False(File.Exists(Path.Combine(repo, "f.sql")));
    }

    [Fact]
    public void FolderOutsideARepositoryIsNotOne() =>
        Assert.False(GitSync.IsRepository(root));

    private static (string?, string?, int, int) Summary(GitState state) => (state.Branch, state.Upstream, state.Ahead, state.Behind);

    private string Clone(string remote, string name)
    {
        var path = Path.Combine(root, name);
        Git.RunOrThrow(root, "clone", "--quiet", remote, path);
        Git.RunOrThrow(path, "config", "user.email", "test@example.com");
        Git.RunOrThrow(path, "config", "user.name", "Test");
        Git.RunOrThrow(path, "checkout", "--quiet", "-B", "main");
        return path;
    }

    private static void Commit(string repo, string file, string text)
    {
        File.WriteAllText(Path.Combine(repo, file), text);
        Git.RunOrThrow(repo, "add", file);
        Git.RunOrThrow(repo, "commit", "--quiet", "-m", file);
    }
}
