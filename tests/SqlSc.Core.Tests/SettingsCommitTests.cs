using SqlSc.Core.Export;
using SqlSc.Core.WorkingFolders;

namespace SqlSc.Core.Tests;

public sealed class SettingsCommitTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "sqlsc-settings-" + Guid.NewGuid().ToString("N"));

    public SettingsCommitTests()
    {
        Directory.CreateDirectory(root);
        Git.RunOrThrow(root, "init", "--quiet", "-b", "main");
        Git.RunOrThrow(root, "config", "user.email", "test@example.com");
        Git.RunOrThrow(root, "config", "user.name", "Test");
    }

    public void Dispose()
    {
        foreach (var file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
        {
            File.SetAttributes(file, FileAttributes.Normal);
        }

        Directory.Delete(root, recursive: true);
    }

    [Fact]
    public void ChangedSettingsFilesAreListedAndCommittedOnTheirOwn()
    {
        File.WriteAllText(Path.Combine(root, "sql-sc.json"), "{\r\n  \"compare\": {}\r\n}\r\n");
        File.WriteAllText(Path.Combine(root, "a.sql"), "one");
        Git.RunOrThrow(root, "add", "--all");
        Git.RunOrThrow(root, "commit", "--quiet", "-m", "start");
        File.WriteAllText(Path.Combine(root, "sql-sc.json"), "{\r\n  \"compare\": { \"ignoreComments\": true }\r\n}\r\n");
        File.WriteAllText(Path.Combine(root, "Filter.scpf"), "<NamedFilter><Filter><Filters /></Filter></NamedFilter>\r\n");
        File.WriteAllText(Path.Combine(root, "a.sql"), "two");
        var folder = WorkingFolder.Open(root);

        var changes = CommitService.SettingsChanges(folder);

        Assert.Equal([("sql-sc.json", "Modified"), ("Filter.scpf", "New")], changes.Select(c => (c.Path, c.Change)));
        Assert.Contains("+  \"compare\": { \"ignoreComments\": true }", changes[0].Diff, StringComparison.Ordinal);
        Assert.DoesNotContain("\r", changes[0].Diff, StringComparison.Ordinal);
        Assert.Contains("+<NamedFilter><Filter><Filters /></Filter></NamedFilter>", changes[1].Diff, StringComparison.Ordinal);

        var (files, commit) = CommitService.CommitSettings(folder, ["Filter.scpf", "a.sql"], "Add filter");

        Assert.Equal(["Filter.scpf"], files);
        Assert.NotNull(commit);
        Assert.Equal(["sql-sc.json"], CommitService.SettingsChanges(folder).Select(c => c.Path));
        Assert.Contains(GitSync.State(root).ChangedFiles, f => f.EndsWith("a.sql", StringComparison.Ordinal));
    }
}
