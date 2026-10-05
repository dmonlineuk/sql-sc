using SqlSc.Core.WorkingFolders;

namespace SqlSc.Core.Export;

/// <summary><see cref="Commit"/> is the new commit, or null if nothing was committed.</summary>
public sealed record CommitResult(ExportResult Export, IReadOnlyList<string> CommittedFiles, string? Commit);

/// <summary>Exports objects and commits exactly their files, leaving anything else staged or changed alone.</summary>
public static class CommitService
{
    public static CommitResult Commit(WorkingFolder folder, string connectionString, ExportSelection selection, string message, bool fullExtract = false)
    {
        if (string.IsNullOrWhiteSpace(message))
        {
            throw new ArgumentException("A commit message is required.", nameof(message));
        }

        if (Git.Run(folder.RootPath, "rev-parse", "--is-inside-work-tree").ExitCode != 0)
        {
            throw new InvalidDataException($"{folder.RootPath} isn't in a git repository.");
        }

        var export = ExportService.Export(folder, connectionString, selection, fullExtract, writeIfProblems: false);
        if (export.Problems.Count > 0 || export.Files.Count == 0)
        {
            return new CommitResult(export, [], null);
        }

        var paths = export.Files
            .Where(f => f.Action != FileAction.Deleted || IsTracked(folder.RootPath, f.Path))
            .Where(f => File.Exists(Path.Combine(folder.RootPath, f.Path)) || IsTracked(folder.RootPath, f.Path))
            .Select(f => f.Path)
            .ToList();
        if (paths.Count == 0)
        {
            return new CommitResult(export, [], null);
        }

        Git.RunOrThrow(folder.RootPath, ["add", "--all", "--", .. paths]);
        var hasHead = Git.Run(folder.RootPath, "rev-parse", "--verify", "--quiet", "HEAD").ExitCode == 0;
        var changed = Git.RunOrThrow(folder.RootPath, ["diff", "--cached", "--name-only", "--relative", .. hasHead ? ["HEAD"] : Array.Empty<string>(), "--", .. paths]);
        var committed = changed.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
        if (committed.Count == 0)
        {
            return new CommitResult(export, [], null);
        }

        Git.RunOrThrow(folder.RootPath, ["commit", "--quiet", "-m", message, "--", .. paths]);
        return new CommitResult(export, committed, Git.RunOrThrow(folder.RootPath, "rev-parse", "--short", "HEAD"));
    }

    private static bool IsTracked(string folder, string path) =>
        Git.Run(folder, "ls-files", "--error-unmatch", "--", path).ExitCode == 0;
}
