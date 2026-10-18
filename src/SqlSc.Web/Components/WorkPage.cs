using Microsoft.AspNetCore.Components;
using SqlSc.Core.Database;
using SqlSc.Core.Export;
using SqlSc.Core.Settings;
using SqlSc.Core.WorkingFolders;
using SqlSc.Web.Services;

namespace SqlSc.Web.Components;

public enum GitOperation
{
    Fetch,
    Pull,
    Push,
}

/// <summary>A branch to switch to or create, and what to do first with the working folder's uncommitted changes.</summary>
public sealed record BranchRequest(string Branch, bool Create, UncommittedChanges Changes = UncommittedChanges.Refuse, string? Message = null);

/// <summary>A page that compares the linked folder with its database: one piece of work at a time, with its outcome as a notice.</summary>
public abstract class WorkPage : ComponentBase
{
    [Inject]
    protected UiState State { get; set; } = null!;

    protected GitState? Git { get; set; }

    protected IReadOnlyList<GitBranch> Branches { get; set; } = [];

    protected IReadOnlyList<GitStash> Stashes { get; set; } = [];

    /// <summary>The branch a switch was refused to because of uncommitted changes, while the git bar asks what to do with them.</summary>
    protected string? BlockedSwitch { get; set; }

    protected bool Busy { get; private set; }

    protected string? BusyText { get; private set; }

    protected string? Notice { get; set; }

    protected bool Failed { get; set; }

    protected override async Task OnInitializedAsync()
    {
        if (State.CurrentLink is not null)
        {
            await Refresh();
        }
    }

    protected abstract Task Refresh();

    protected async Task RunGit(GitOperation operation)
    {
        if (State.CurrentLink is not { } link)
        {
            return;
        }

        await Run("Running git...", () =>
        {
            var output = operation switch
            {
                GitOperation.Fetch => GitSync.Fetch(link.Folder),
                GitOperation.Pull => GitSync.Pull(link.Folder),
                _ => GitSync.Push(link.Folder),
            };
            UpdateGit(link.Folder);
            return string.IsNullOrWhiteSpace(output) ? "Done." : output;
        });
        if (operation == GitOperation.Pull)
        {
            await RefreshAfter();
        }
    }

    /// <summary>Switches to an existing branch or creates one, then compares again.</summary>
    protected async Task ChangeBranch(BranchRequest request)
    {
        if (State.CurrentLink is not { } link)
        {
            return;
        }

        var blocked = false;
        await Run(request.Create ? "Creating the branch..." : "Switching branch...", () =>
        {
            if (!request.Create && request.Changes == UncommittedChanges.Refuse)
            {
                UpdateGit(link.Folder);
                if (Git is { ChangedFiles.Count: > 0 })
                {
                    (blocked, BlockedSwitch) = (true, request.Branch);
                    return null;
                }
            }

            var output = request.Create
                ? GitSync.CreateBranch(link.Folder, request.Branch)
                : GitSync.Switch(link.Folder, request.Branch, request.Changes, request.Message);
            BlockedSwitch = null;
            UpdateGit(link.Folder);
            return string.IsNullOrWhiteSpace(output) ? $"On branch {request.Branch}." : output;
        });
        if (!blocked)
        {
            await RefreshAfter();
        }
    }

    protected void CancelSwitch() => BlockedSwitch = null;

    /// <summary>Restores a stash into the working folder, or deletes it, then compares again.</summary>
    protected async Task ChangeStash((GitStash Stash, bool Restore) request)
    {
        if (State.CurrentLink is not { } link)
        {
            return;
        }

        await Run(request.Restore ? "Restoring the stash..." : "Deleting the stash...", () =>
        {
            var output = request.Restore ? GitSync.RestoreStash(link.Folder, request.Stash.Id) : GitSync.DropStash(link.Folder, request.Stash.Id);
            UpdateGit(link.Folder);
            return string.IsNullOrWhiteSpace(output) ? (request.Restore ? "Restored the stash." : "Deleted the stash.") : output;
        });
        await RefreshAfter();
    }

    /// <summary>Refreshes after other work, keeping that work's notice unless the refresh fails.</summary>
    protected async Task RefreshAfter()
    {
        if (Failed)
        {
            return;
        }

        var text = Notice;
        await Refresh();
        if (!Failed)
        {
            Notice = text;
        }
    }

    protected void UpdateGit(string folder)
    {
        Git = GitSync.IsRepository(folder) ? GitSync.State(folder) : null;
        Branches = Git is null ? [] : GitSync.Branches(folder);
        Stashes = Git is null ? [] : GitSync.Stashes(folder);
        if (Git is not { ChangedFiles.Count: > 0 })
        {
            BlockedSwitch = null;
        }
    }

    protected bool Connect(out Link link, out WorkingFolder folder, out string connection)
    {
        (link, folder, connection) = (State.CurrentLink!, null!, null!);
        if (State.CurrentLink is null)
        {
            return false;
        }

        connection = State.ConnectionFor(link)!;
        if (ConnectionStrings.NeedsPassword(connection))
        {
            (Notice, Failed) = ("This link needs a SQL password. Enter it on Setup.", true);
            return false;
        }

        try
        {
            folder = WorkingFolder.Open(link.Folder);
            return true;
        }
        catch (Exception e) when (e is IOException or InvalidDataException or UnauthorizedAccessException)
        {
            (Notice, Failed) = (e.Message, true);
            return false;
        }
    }

    protected async Task Run(string text, Func<string?> work)
    {
        (Busy, BusyText, Notice, Failed) = (true, text, null, false);
        try
        {
            Notice = await State.RunAsync(work);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            (Notice, Failed) = (e.Message, true);
        }
        finally
        {
            Busy = false;
        }
    }
}
