using System.Globalization;

namespace SqlSc.Core.Export;

/// <summary>
/// Where the working folder's git branch stands against its upstream. <see cref="Upstream"/> is null if the branch has none;
/// <see cref="Ahead"/> and <see cref="Behind"/> count commits as of the last fetch.
/// </summary>
public sealed record GitState(string? Branch, string? Upstream, int Ahead, int Behind, IReadOnlyList<string> ChangedFiles);

/// <summary>A branch the working folder can switch to. A <see cref="RemoteOnly"/> branch exists only on a remote; switching to it creates a local branch that tracks it.</summary>
public sealed record GitBranch(string Name, bool RemoteOnly);

/// <summary>Changes git has put aside with <c>git stash</c>, newest first. <see cref="Id"/> is the stash's commit, which stays the same as others come and go.</summary>
public sealed record GitStash(string Id, string Description, DateTimeOffset Created);

/// <summary>What <see cref="GitSync.Switch"/> does with the working folder's uncommitted changes.</summary>
public enum UncommittedChanges
{
    /// <summary>Don't switch while there are any.</summary>
    Refuse,

    /// <summary>Commit them to the current branch first.</summary>
    Commit,

    /// <summary>Stash them first, to restore later with <see cref="GitSync.RestoreStash"/>.</summary>
    Stash,

    /// <summary>Throw them away first. They can't be got back.</summary>
    Discard,
}

/// <summary>The git operations the UI offers on a working folder's repository: status, branches, fetch, pull (fast-forward only) and push.</summary>
public static class GitSync
{
    public static bool IsRepository(string folder) =>
        Directory.Exists(folder) && Git.Run(folder, "rev-parse", "--is-inside-work-tree").ExitCode == 0;

    public static GitState State(string folder)
    {
        var branch = Git.Run(folder, "symbolic-ref", "--quiet", "--short", "HEAD") is { ExitCode: 0, Output: { Length: > 0 } name } ? name : null;
        var upstream = Git.Run(folder, "rev-parse", "--abbrev-ref", "--symbolic-full-name", "@{upstream}") is { ExitCode: 0, Output: { Length: > 0 } u } ? u : null;
        var (ahead, behind) = (0, 0);
        if (upstream is not null
            && Git.Run(folder, "rev-list", "--left-right", "--count", "HEAD...@{upstream}") is { ExitCode: 0, Output: var counts }
            && counts.Split((char[])['\t', ' '], StringSplitOptions.RemoveEmptyEntries) is [var a, var b])
        {
            ahead = int.Parse(a, CultureInfo.InvariantCulture);
            behind = int.Parse(b, CultureInfo.InvariantCulture);
        }

        var changed = Git.RunOrThrow(folder, "status", "--porcelain", "--", ".")
            .Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(l => l.TrimEnd('\r'))
            .ToList();
        return new GitState(branch, upstream, ahead, behind, changed);
    }

    /// <summary>Local branches, then branches that exist only on a remote, by name.</summary>
    public static IReadOnlyList<GitBranch> Branches(string folder)
    {
        static IEnumerable<string> Refs(string folder, string prefix) =>
            Git.RunOrThrow(folder, "for-each-ref", "--format=%(refname)", prefix)
                .Split('\n', StringSplitOptions.RemoveEmptyEntries)
                .Select(r => r.TrimEnd('\r')[prefix.Length..]);

        var local = Refs(folder, "refs/heads/").Order(StringComparer.Ordinal).ToList();
        var remote = Refs(folder, "refs/remotes/")
            .Select(r => r.IndexOf('/', StringComparison.Ordinal) is var slash and > 0 ? r[(slash + 1)..] : null)
            .OfType<string>()
            .Where(r => r != "HEAD" && !local.Contains(r, StringComparer.Ordinal))
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal);
        return [.. local.Select(b => new GitBranch(b, false)), .. remote.Select(b => new GitBranch(b, true))];
    }

    /// <summary>
    /// Switches to <paramref name="branch"/>, creating a tracking branch for a remote-only one. Uncommitted changes in the working folder
    /// are never carried onto the other branch: they're refused, or committed, stashed or discarded first as <paramref name="changes"/> says.
    /// </summary>
    public static string Switch(string folder, string branch, UncommittedChanges changes = UncommittedChanges.Refuse, string? message = null)
    {
        CheckName(folder, branch);
        var output = new List<string>();
        if (State(folder).ChangedFiles is { Count: > 0 } changed)
        {
            output.Add(changes switch
            {
                UncommittedChanges.Commit => CommitAll(folder, message ?? ""),
                UncommittedChanges.Stash => Stash(folder, string.IsNullOrWhiteSpace(message) ? $"sql-sc: before switching to {branch}" : message),
                UncommittedChanges.Discard => Discard(folder),
                _ => throw new InvalidDataException($"The working folder has {changed.Count} uncommitted change(s). Commit, stash or discard them before switching branch."),
            });
        }

        output.Add(Combined(Git.Run(folder, "switch", "--quiet", branch), "switch"));
        return string.Join('\n', output.Where(o => o.Length > 0));
    }

    /// <summary>Commits every change in the working folder, including new and deleted files, to the current branch.</summary>
    public static string CommitAll(string folder, string message)
    {
        if (string.IsNullOrWhiteSpace(message))
        {
            throw new InvalidDataException("Enter a commit message.");
        }

        Git.RunOrThrow(folder, "add", "--all", "--", ".");
        return Combined(Git.Run(folder, "commit", "--quiet", "-m", message.Trim(), "--", "."), "commit");
    }

    /// <summary>Puts every change in the working folder, including new files, aside in a stash and leaves the folder as the last commit has it.</summary>
    public static string Stash(string folder, string message) =>
        Combined(Git.Run(folder, "stash", "push", "--quiet", "--include-untracked", "-m", message, "--", "."), "stash");

    public static IReadOnlyList<GitStash> Stashes(string folder) =>
        Git.RunOrThrow(folder, "stash", "list", "--format=%H%x1f%gs%x1f%cI")
            .Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(l => l.TrimEnd('\r').Split('\u001f'))
            .Where(p => p.Length == 3)
            .Select(p => new GitStash(p[0], p[1], DateTimeOffset.Parse(p[2], CultureInfo.InvariantCulture)))
            .ToList();

    /// <summary>
    /// Puts a stash's changes back into the working folder and removes the stash. Refuses while the folder has uncommitted changes, so
    /// the stash never gets mixed with them; if git still can't restore it cleanly, the stash is kept.
    /// </summary>
    public static string RestoreStash(string folder, string id)
    {
        var reference = StashReference(folder, id);
        if (State(folder).ChangedFiles is { Count: > 0 } changed)
        {
            throw new InvalidDataException($"The working folder has {changed.Count} uncommitted change(s). Commit, stash or discard them before restoring a stash.");
        }

        return Combined(Git.Run(folder, "stash", "pop", "--quiet", reference), "stash pop");
    }

    /// <summary>Deletes a stash. Its changes can't be got back.</summary>
    public static string DropStash(string folder, string id) =>
        Combined(Git.Run(folder, "stash", "drop", "--quiet", StashReference(folder, id)), "stash drop");

    /// <summary>Throws away every change in the working folder: files go back to the last commit and new files are deleted. Ignored files are kept.</summary>
    public static string Discard(string folder)
    {
        Git.RunOrThrow(folder, "restore", "--source=HEAD", "--staged", "--worktree", "--", ".");
        return Combined(Git.Run(folder, "clean", "--force", "-d", "--quiet", "--", "."), "clean");
    }

    private static string StashReference(string folder, string id) =>
        Stashes(folder).Select((s, i) => (s, i)).FirstOrDefault(x => x.s.Id == id) is ({ }, var index)
            ? $"stash@{{{index}}}"
            : throw new InvalidDataException("That stash no longer exists.");

    /// <summary>Creates <paramref name="branch"/> from the current commit and switches to it. Uncommitted changes stay in the working folder.</summary>
    public static string CreateBranch(string folder, string branch)
    {
        CheckName(folder, branch);
        return Combined(Git.Run(folder, "switch", "--quiet", "--create", branch), "switch --create");
    }

    private static void CheckName(string folder, string branch)
    {
        if (string.IsNullOrWhiteSpace(branch) || branch.StartsWith('-') || Git.Run(folder, "check-ref-format", "--branch", branch).ExitCode != 0)
        {
            throw new InvalidDataException($"'{branch}' isn't a valid branch name.");
        }
    }

    /// <summary>Fetches the upstream so <see cref="State"/> shows how far behind the branch is.</summary>
    public static string Fetch(string folder) => Combined(Git.Run(folder, "fetch", "--quiet"), "fetch");

    /// <summary>Pulls with <c>--ff-only</c>, so it never creates a merge commit or starts a rebase.</summary>
    public static string Pull(string folder) => Combined(Git.Run(folder, "pull", "--ff-only"), "pull --ff-only");

    /// <summary>Pushes the current branch, setting its upstream on <c>origin</c> if it has none.</summary>
    public static string Push(string folder)
    {
        var state = State(folder);
        if (state.Branch is null)
        {
            throw new InvalidDataException("HEAD isn't on a branch, so there's nothing to push.");
        }

        return state.Upstream is null
            ? Combined(Git.Run(folder, "push", "--set-upstream", "origin", state.Branch), "push")
            : Combined(Git.Run(folder, "push"), "push");
    }

    private static string Combined((int ExitCode, string Output, string Error) result, string command)
    {
        var text = string.Join('\n', new[] { result.Output, result.Error }.Where(t => t.Length > 0));
        return result.ExitCode == 0 ? text : throw new InvalidDataException($"git {command} failed: {(text.Length > 0 ? text : $"exit code {result.ExitCode}")}");
    }
}
