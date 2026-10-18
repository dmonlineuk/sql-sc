using SqlSc.Core.Comparison;
using SqlSc.Core.Settings;
using SqlSc.Core.WorkingFolders;

namespace SqlSc.Core.Export;

/// <summary><see cref="Commit"/> is the new commit, or null if nothing was committed.</summary>
public sealed record CommitResult(ExportResult Export, IReadOnlyList<string> CommittedFiles, string? Commit);

/// <summary>
/// A working folder settings file (sql-sc.json, the filter file or RedGateDatabaseInfo.xml) with uncommitted changes.
/// <see cref="Change"/> is New, Modified or Deleted; <see cref="Diff"/> is the unified diff from the last commit, ignoring line endings.
/// </summary>
public sealed record SettingsFileChange(string Path, string Change, string Diff);

/// <summary>Exports objects and commits exactly their files, leaving anything else staged or changed alone.</summary>
public static class CommitService
{
    /// <param name="settingsFiles">Settings files to commit with the objects, from <see cref="SettingsChanges"/>.</param>
    public static CommitResult Commit(
        WorkingFolder folder,
        string connectionString,
        ExportSelection selection,
        string message,
        bool fullExtract = false,
        BaselineStore? baseline = null,
        IReadOnlyList<string>? settingsFiles = null)
    {
        if (string.IsNullOrWhiteSpace(message))
        {
            throw new ArgumentException("A commit message is required.", nameof(message));
        }

        if (Git.Run(folder.RootPath, "rev-parse", "--is-inside-work-tree").ExitCode != 0)
        {
            throw new InvalidDataException($"{folder.RootPath} isn't in a git repository.");
        }

        var export = ExportService.Export(folder, connectionString, selection, fullExtract, writeIfProblems: false, baseline: baseline);
        var extra = Settings(folder, settingsFiles ?? []);
        if (export.Problems.Count > 0 || export.Files.Count + extra.Count == 0)
        {
            return new CommitResult(export, [], null);
        }

        var paths = export.Files
            .Where(f => f.Action != FileAction.Deleted || IsTracked(folder.RootPath, f.Path))
            .Where(f => File.Exists(Path.Combine(folder.RootPath, f.Path)) || IsTracked(folder.RootPath, f.Path))
            .Select(f => f.Path)
            .Concat(extra)
            .ToList();
        var (committed, commit) = CommitPaths(folder.RootPath, paths, message);
        return new CommitResult(export, committed, commit);
    }

    /// <summary>Commits only settings files, from <see cref="SettingsChanges"/>, without comparing the database.</summary>
    /// <returns>The files committed and the new commit, or null if nothing changed.</returns>
    public static (IReadOnlyList<string> Files, string? Commit) CommitSettings(WorkingFolder folder, IReadOnlyList<string> settingsFiles, string message)
    {
        if (string.IsNullOrWhiteSpace(message))
        {
            throw new ArgumentException("A commit message is required.", nameof(message));
        }

        return CommitPaths(folder.RootPath, Settings(folder, settingsFiles), message);
    }

    /// <summary>The working folder's settings files that differ from the last commit.</summary>
    public static IReadOnlyList<SettingsFileChange> SettingsChanges(WorkingFolder folder)
    {
        var root = folder.RootPath;
        var hasHead = HasHead(root);
        var changes = new List<SettingsFileChange>();
        foreach (var path in SettingsPaths(folder))
        {
            var exists = File.Exists(Path.Combine(root, path));
            var tracked = IsTracked(root, path);
            var change = (tracked, exists) switch
            {
                (false, true) => "New",
                (true, false) => "Deleted",
                (true, true) when Git.Run(root, ["diff", "--quiet", .. hasHead ? ["HEAD"] : Array.Empty<string>(), "--", path]).ExitCode == 1 => "Modified",
                _ => null,
            };
            if (change is null)
            {
                continue;
            }

            var before = tracked && hasHead && Git.Run(root, "show", $"HEAD:./{path.Replace('\\', '/')}") is { ExitCode: 0, Output: var committed } ? Normalize(committed) : null;
            var after = exists ? Normalize(File.ReadAllText(Path.Combine(root, path))) : null;
            var label = path.Replace('\\', '/');
            changes.Add(new SettingsFileChange(path, change, UnifiedDiff.Create(before, after, before is null ? UnifiedDiff.NoFile : "a/" + label, after is null ? UnifiedDiff.NoFile : "b/" + label)));
        }

        return changes;
    }

    private static IEnumerable<string> SettingsPaths(WorkingFolder folder) =>
        new[] { ProjectSettings.FileName, folder.FilterPath ?? ProjectSettings.DefaultFilter, RedgateDatabaseInfo.FileName }
            .Distinct(StringComparer.OrdinalIgnoreCase);

    /// <summary>Only the requested files that really are settings files, so nothing else can be committed this way.</summary>
    private static List<string> Settings(WorkingFolder folder, IReadOnlyList<string> requested) =>
        SettingsPaths(folder).Where(p => requested.Contains(p, StringComparer.OrdinalIgnoreCase)).ToList();

    private static (IReadOnlyList<string> Files, string? Commit) CommitPaths(string root, List<string> paths, string message)
    {
        if (paths.Count == 0)
        {
            return ([], null);
        }

        Git.RunOrThrow(root, ["add", "--all", "--", .. paths]);
        var hasHead = HasHead(root);
        var changed = Git.RunOrThrow(root, ["diff", "--cached", "--name-only", "--relative", .. hasHead ? ["HEAD"] : Array.Empty<string>(), "--", .. paths]);
        var committed = changed.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
        if (committed.Count == 0)
        {
            return ([], null);
        }

        Git.RunOrThrow(root, ["commit", "--quiet", "-m", message, "--", .. paths]);
        return (committed, Git.RunOrThrow(root, "rev-parse", "--short", "HEAD"));
    }

    private static bool HasHead(string root) => Git.Run(root, "rev-parse", "--verify", "--quiet", "HEAD").ExitCode == 0;

    private static string Normalize(string text) => text.ReplaceLineEndings("\n").TrimEnd('\n') + "\n";

    private static bool IsTracked(string folder, string path) =>
        Git.Run(folder, "ls-files", "--error-unmatch", "--", path).ExitCode == 0;
}
