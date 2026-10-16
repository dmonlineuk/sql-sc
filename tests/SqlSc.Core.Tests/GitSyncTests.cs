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
