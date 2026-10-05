using Microsoft.Data.SqlClient;
using Microsoft.SqlServer.Dac.Compare;
using SqlSc.Core.ChangeTracking;
using SqlSc.Core.Comparison;
using SqlSc.Core.Export;
using SqlSc.Core.Modeling;
using SqlSc.Core.WorkingFolders;

namespace SqlSc.Core.Apply;

public enum ApplyAction
{
    /// <summary>The object is in the folder but not the database.</summary>
    Create,

    /// <summary>The object differs; the database gets the folder's version.</summary>
    Alter,

    /// <summary>The object is only in the database. Only named objects are dropped, never those picked by <c>--all</c>.</summary>
    Drop,
}

public sealed record AppliedObject(ApplyAction Action, string ObjectType, string Name, string? File, ChangeEvent? LastChange);

/// <summary>
/// <see cref="Script"/> is DacFx's deployment script for the selected objects (SQLCMD mode), or null if there was nothing to script.
/// <see cref="Applied"/> is whether it was run.
/// </summary>
public sealed record ApplyResult(
    StatusReport Status,
    IReadOnlyList<AppliedObject> Objects,
    string? Script,
    IReadOnlyList<string> Messages,
    IReadOnlyList<string> Problems,
    bool Applied);

/// <summary>Deploys the working folder's version of selected objects to the database (Get latest).</summary>
public static class ApplyService
{
    /// <param name="all">Every object that differs, except those only in the database, which are never dropped unless named.</param>
    /// <param name="scriptOnly">Generate the script without running it.</param>
    /// <param name="allowDataLoss">Allow changes that could lose data, such as dropping a column or a table with rows.</param>
    /// <param name="force">Overwrite objects whose last change in the database was made by another login.</param>
    public static ApplyResult Apply(
        WorkingFolder folder,
        string connectionString,
        IReadOnlyList<string> names,
        bool all = false,
        bool scriptOnly = false,
        bool allowDataLoss = false,
        bool force = false,
        bool fullExtract = false)
    {
        using var session = StatusService.Open(folder, connectionString, includeChangedBy: true, fullExtract);
        var report = session.Report;
        var problems = new List<string>();
        var selected = ExportService.Select(report, new ExportSelection(names, all), connectionString, problems)
            .Where(c => !all || c.Status != ObjectStatus.New)
            .ToList();
        if (!force && report.ChangeLog.Available && selected.Any(c => c.LastChange?.LoginName is not null))
        {
            var login = ExportService.CurrentLogin(connectionString);
            problems.AddRange(selected
                .Where(c => c.LastChange is { LoginName: { } who } && !string.Equals(who, login, StringComparison.OrdinalIgnoreCase))
                .Select(c => $"{c.Name} was last changed in the database by {c.LastChange!.LoginName} at {c.LastChange.StartTime:yyyy-MM-dd HH:mm}; pass --force to overwrite it."));
        }

        var objects = selected
            .Select(c => new AppliedObject(
                c.Status switch
                {
                    ObjectStatus.Deleted => ApplyAction.Create,
                    ObjectStatus.New => ApplyAction.Drop,
                    _ => ApplyAction.Alter,
                },
                c.ObjectType,
                c.Name,
                c.File,
                c.LastChange))
            .ToList();
        if (selected.Count == 0)
        {
            return new ApplyResult(report, objects, null, [], problems, false);
        }

        var drops = objects.Where(o => o.Action == ApplyAction.Drop).Select(o => o.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (drops.Count > 0)
        {
            var removed = BrokenObjects.Remove(session.Folder.Model, DatabaseReferences.IsBorrowed, o => drops.Contains(StatusService.FormatName(o.Name) ?? string.Empty));
            problems.AddRange(drops.Except(removed, StringComparer.OrdinalIgnoreCase)
                .Order(StringComparer.Ordinal)
                .Select(name => $"{name} can't be dropped, because objects in the folder use it."));
            File.Delete(session.FolderPackage);
            session.Folder.BuildPackage(session.FolderPackage, report.Database);
        }

        var options = folder.Settings.Compare.ToDeployOptions();
        options.BlockOnPossibleDataLoss = !allowDataLoss;
        options.IncludeTransactionalScripts = true;
        var comparison = new SchemaComparison(new SchemaCompareDacpacEndpoint(session.FolderPackage), new SchemaCompareDacpacEndpoint(session.DatabasePackage))
        {
            Options = options,
        };
        var result = comparison.Compare();
        if (!result.IsValid)
        {
            throw new InvalidOperationException($"Schema comparison failed:{Environment.NewLine}{string.Join(Environment.NewLine, result.GetErrors().Select(e => e.Message))}");
        }

        var keys = selected.Select(c => (c.ObjectType, c.Name)).ToHashSet(ObjectFiles.KeyComparer.Instance);
        foreach (var difference in result.Differences.ToList())
        {
            var owner = (difference.SourceObject ?? difference.TargetObject) is { } obj ? StatusService.OwnerOf(obj) : null;
            var wanted = owner is not null
                && !DatabaseReferences.IsBorrowed(difference.SourceObject)
                && keys.Contains((owner.ObjectType.Name, StatusService.FormatName(owner.Name) ?? string.Empty));
            if (!wanted && difference.Included && !result.Exclude(difference))
            {
                problems.Add($"{StatusService.FormatName(owner?.Name) ?? difference.Name} has to be applied too, because a selected object depends on it.");
            }
        }

        if (!result.Differences.Any(d => d.Included))
        {
            problems.Add("DacFx found nothing to change for the selected objects.");
            return new ApplyResult(report, objects, null, [], problems, false);
        }

        var generated = result.GenerateScript(report.Database);
        if (!generated.Success)
        {
            problems.Add($"DacFx couldn't generate the deployment script: {generated.Message}");
            return new ApplyResult(report, objects, null, [], problems, false);
        }

        // DacFx stores scripts with LF line endings; working folder files have CRLF, as export writes them.
        var script = ColumnNamedAliases.Undo(generated.Script).ReplaceLineEndings("\r\n");
        var messages = new List<string>();
        if (scriptOnly || problems.Count > 0)
        {
            return new ApplyResult(report, objects, script, messages, problems, false);
        }

        try
        {
            SqlCmdScript.Run(connectionString, script, messages);
        }
        catch (SqlException ex)
        {
            problems.Add($"Applying failed, and its transaction was rolled back: {ex.Message}");
            return new ApplyResult(report, objects, script, messages, problems, false);
        }

        return new ApplyResult(report, objects, script, messages, problems, true);
    }
}
