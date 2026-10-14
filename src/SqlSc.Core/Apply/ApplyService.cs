using Microsoft.Data.SqlClient;
using Microsoft.SqlServer.Dac.Compare;
using SqlSc.Core.ChangeTracking;
using SqlSc.Core.Comparison;
using SqlSc.Core.Export;
using SqlSc.Core.Modeling;
using SqlSc.Core.StaticData;
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
/// <see cref="Data"/> are the static data tables whose rows change, and <see cref="DataScript"/> their script on a script-only run.
/// <see cref="Applied"/> is whether the changes were made.
/// </summary>
public sealed record ApplyResult(
    StatusReport Status,
    IReadOnlyList<AppliedObject> Objects,
    string? Script,
    IReadOnlyList<string> Messages,
    IReadOnlyList<string> Problems,
    bool Applied,
    IReadOnlyList<AppliedData> Data,
    string? DataScript = null);

/// <summary>
/// Deploys the working folder's version of selected objects and static data to the database (Get latest).
/// Schema changes run first, in DacFx's transaction; static data then runs in a second transaction against the changed schema.
/// </summary>
public static class ApplyService
{
    /// <param name="all">
    /// Every object that differs, except those only in the database, which are never dropped unless named. Likewise static data
    /// rows that are only in the database are deleted only from tables that are named.
    /// </param>
    /// <param name="data">Include the static data of selected tables; without it only schema is applied.</param>
    /// <param name="scriptOnly">Generate the script without running it.</param>
    /// <param name="allowDataLoss">Allow changes that could lose data, such as dropping a column or a table with rows.</param>
    /// <param name="force">Overwrite objects whose last change in the database was made by another login.</param>
    public static ApplyResult Apply(
        WorkingFolder folder,
        string connectionString,
        IReadOnlyList<string> names,
        bool all = false,
        bool data = false,
        bool scriptOnly = false,
        bool allowDataLoss = false,
        bool force = false,
        bool fullExtract = false)
    {
        using var session = StatusService.Open(folder, connectionString, includeChangedBy: true, fullExtract);
        var report = session.Report;
        var problems = new List<string>();
        var selected = ExportService.Select(report, new ExportSelection(names, all, Data: data), connectionString, problems)
            .Where(c => !all || c.Status != ObjectStatus.New)
            .ToList();
        if (!force && report.ChangeLog.Available && selected.Any(c => c.LastChange?.LoginName is not null))
        {
            var login = ExportService.CurrentLogin(connectionString);
            problems.AddRange(selected
                .Where(c => c.LastChange is { LoginName: { } who } && !string.Equals(who, login, StringComparison.OrdinalIgnoreCase))
                .Select(c => $"{c.Name} was last changed in the database by {c.LastChange!.LoginName} at {c.LastChange.StartTime:yyyy-MM-dd HH:mm}; pass --force to overwrite it."));
        }

        var selectedData = ExportService.SelectData(report, new ExportSelection(names, all, Data: data), problems)
            .Where(d => !all || d.Deleted + d.Modified > 0)
            .ToList();
        var tracked = StaticDataService.Tracked(folder);
        var dataTables = selectedData
            .Select(d => (Table: tracked.First(t => string.Equals(t.File, d.File, StringComparison.OrdinalIgnoreCase)), DeleteRows: !all))
            .ToList();
        IReadOnlyList<AppliedData> appliedData = selectedData.Select(d => new AppliedData(d.Table, d.File, d.Deleted, d.Modified, all ? 0 : d.New)).ToList();

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
        if (selected.Count == 0 && dataTables.Count == 0)
        {
            return new ApplyResult(report, objects, null, [], problems, false, appliedData);
        }

        var script = selected.Count == 0 ? null : SchemaScript(folder, session, objects, selected, allowDataLoss, problems);
        if (selected.Count > 0 && script is null)
        {
            return new ApplyResult(report, objects, null, [], problems, false, appliedData);
        }

        string? dataScript = null;
        if (scriptOnly && dataTables.Count > 0)
        {
            using var connection = new SqlConnection(connectionString);
            connection.Open();
            try
            {
                dataScript = StaticDataService.ScriptOf(StaticDataService.Plan(connection, null, folder, dataTables));
            }
            catch (InvalidDataException ex)
            {
                problems.Add(ex.Message);
            }
        }

        var messages = new List<string>();
        if (scriptOnly || problems.Count > 0)
        {
            return new ApplyResult(report, objects, script, messages, problems, false, appliedData, dataScript);
        }

        if (script is not null)
        {
            try
            {
                SqlCmdScript.Run(connectionString, script, messages);
            }
            catch (SqlException ex)
            {
                problems.Add($"Applying failed, and its transaction was rolled back: {ex.Message}");
                return new ApplyResult(report, objects, script, messages, problems, false, appliedData);
            }
        }

        if (dataTables.Count > 0)
        {
            try
            {
                appliedData = StaticDataService.Run(connectionString, folder, dataTables);
            }
            catch (Exception ex) when (ex is SqlException or InvalidDataException)
            {
                var schema = script is null ? "" : " The schema changes were applied.";
                problems.Add($"Applying static data failed, and its transaction was rolled back.{schema} {ex.Message}");
                return new ApplyResult(report, objects, script, messages, problems, script is not null, appliedData);
            }
        }

        return new ApplyResult(report, objects, script, messages, problems, true, appliedData);
    }

    /// <summary>DacFx's deployment script for the selected objects, or null if it can't be generated (the reasons are added to <paramref name="problems"/>).</summary>
    private static string? SchemaScript(
        WorkingFolder folder,
        StatusSession session,
        List<AppliedObject> objects,
        List<ObjectChange> selected,
        bool allowDataLoss,
        List<string> problems)
    {
        var report = session.Report;
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
            return null;
        }

        var generated = result.GenerateScript(report.Database);
        if (!generated.Success)
        {
            problems.Add($"DacFx couldn't generate the deployment script: {generated.Message}");
            return null;
        }

        // DacFx stores scripts with LF line endings; working folder files have CRLF, as export writes them.
        return ColumnNamedAliases.Undo(generated.Script).ReplaceLineEndings("\r\n");
    }
}
