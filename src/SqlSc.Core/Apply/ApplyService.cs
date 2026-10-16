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
/// <see cref="Applied"/> is whether the changes were made. <see cref="Skipped"/> says why objects picked by <c>--all</c> were left out.
/// <see cref="Declined"/> are the changes the user chose not to make, and <see cref="SavedScript"/> the audit copy of what ran.
/// </summary>
public sealed record ApplyResult(
    StatusReport Status,
    IReadOnlyList<AppliedObject> Objects,
    string? Script,
    IReadOnlyList<string> Messages,
    IReadOnlyList<string> Problems,
    bool Applied,
    IReadOnlyList<AppliedData> Data,
    string? DataScript = null,
    IReadOnlyList<string>? Skipped = null,
    IReadOnlyList<string>? Declined = null,
    string? SavedScript = null);

/// <summary>The planned changes the user agreed to make.</summary>
public sealed record ApplyChoice(IReadOnlyList<AppliedObject> Objects, IReadOnlyList<AppliedData> Data);

/// <summary>Asks which of the planned changes to make. Called once, before anything is scripted or run, and never on a script-only run.</summary>
public delegate ApplyChoice ApplyConfirmation(StatusReport status, IReadOnlyList<AppliedObject> objects, IReadOnlyList<AppliedData> data);

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
    /// <param name="force">
    /// Overwrite objects whose last change in the database was made by another login, or that changed in the database since they
    /// last matched the folder.
    /// </param>
    /// <param name="baseline">Where the baseline is kept; null to keep none.</param>
    /// <param name="confirm">Asks which changes to make; null to make them all.</param>
    /// <param name="log">Where a copy of each script that runs is saved; null to save none.</param>
    /// <param name="dataNames">
    /// The tables whose static data is applied, as if named, instead of those picked by <paramref name="names"/> or
    /// <paramref name="all"/>; implies <paramref name="data"/>.
    /// </param>
    public static ApplyResult Apply(
        WorkingFolder folder,
        string connectionString,
        IReadOnlyList<string> names,
        bool all = false,
        bool data = false,
        bool scriptOnly = false,
        bool allowDataLoss = false,
        bool force = false,
        bool fullExtract = false,
        BaselineStore? baseline = null,
        ApplyConfirmation? confirm = null,
        DeploymentLog? log = null,
        IReadOnlyList<string>? dataNames = null)
    {
        using var session = StatusService.Open(folder, connectionString, includeChangedBy: true, fullExtract, baselineStore: baseline);
        var report = session.Report;
        var problems = new List<string>();
        var skipped = new List<string>();
        var selection = new ExportSelection(names, all, Data: data);
        var selected = SideGuard.Filter(
            ExportService.Select(report, selection, connectionString, problems).Where(c => !all || c.Status != ObjectStatus.New),
            ChangeSide.Database,
            selection.NamedObjects,
            force,
            problems,
            skipped);
        if (!force && report.ChangeLog.Available && selected.Any(c => c.LastChange?.LoginName is not null))
        {
            var login = ExportService.CurrentLogin(connectionString);
            problems.AddRange(selected
                .Where(c => c.LastChange is { LoginName: { } who } && !string.Equals(who, login, StringComparison.OrdinalIgnoreCase))
                .Select(c => $"{c.Name} was last changed in the database by {c.LastChange!.LoginName} at {c.LastChange.StartTime:yyyy-MM-dd HH:mm}; pass --force to overwrite it."));
        }

        var dataAll = all && dataNames is null;
        var selectedData = ExportService.SelectData(report, new ExportSelection(names, all, Data: data || dataNames is not null, DataNames: dataNames), problems)
            .Where(d => !dataAll || d.Deleted + d.Modified > 0)
            .ToList();
        var tracked = StaticDataService.Tracked(folder);
        var dataTables = selectedData
            .Select(d => (Table: tracked.First(t => string.Equals(t.File, d.File, StringComparison.OrdinalIgnoreCase)), DeleteRows: !dataAll))
            .ToList();
        IReadOnlyList<AppliedData> appliedData = selectedData.Select(d => new AppliedData(d.Table, d.File, d.Deleted, d.Modified, dataAll ? 0 : d.New)).ToList();

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
        var declined = new List<string>();
        if (confirm is not null && !scriptOnly && problems.Count == 0 && objects.Count + appliedData.Count > 0)
        {
            var choice = confirm(report, objects, appliedData);
            var keptObjects = choice.Objects.Select(o => o.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var keptData = choice.Data.Select(d => d.File).ToHashSet(StringComparer.OrdinalIgnoreCase);
            declined.AddRange(objects.Where(o => !keptObjects.Contains(o.Name)).Select(o => o.Name));
            declined.AddRange(appliedData.Where(d => !keptData.Contains(d.File)).Select(d => $"{d.Table} (data)"));
            selected = selected.Where(c => keptObjects.Contains(c.Name)).ToList();
            objects = objects.Where(o => keptObjects.Contains(o.Name)).ToList();
            dataTables = dataTables.Where(d => keptData.Contains(d.Table.File)).ToList();
            appliedData = appliedData.Where(d => keptData.Contains(d.File)).ToList();
        }

        string? savedScript = null;
        var messages = new List<string>();
        if (selected.Count == 0 && dataTables.Count == 0)
        {
            return Result(null, false);
        }

        var script = selected.Count == 0 ? null : SchemaScript(folder, session, objects, selected, allowDataLoss, problems);
        if (selected.Count > 0 && script is null)
        {
            return Result(null, false);
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

        if (scriptOnly || problems.Count > 0)
        {
            return Result(script, false, dataScript);
        }

        if (log is not null)
        {
            savedScript = log.Start(report, folder.RootPath, ExportService.CurrentLogin(connectionString), objects, appliedData, DateTime.UtcNow);
            if (script is not null)
            {
                DeploymentLog.Append(savedScript, script);
            }
        }

        if (script is not null)
        {
            try
            {
                SqlCmdScript.Run(connectionString, script, messages);
                session.Baseline?.Applied(objects.Select(o => (o.ObjectType, o.Name, o.Action == ApplyAction.Drop)));
                session.Baseline?.Save();
            }
            catch (SqlException ex)
            {
                problems.Add($"Applying failed, and its transaction was rolled back: {ex.Message}");
                Finish("failed, and its transaction was rolled back: " + ex.Message);
                return Result(script, false);
            }
        }

        if (dataTables.Count > 0)
        {
            try
            {
                appliedData = StaticDataService.Run(
                    connectionString,
                    folder,
                    dataTables,
                    dataSteps => Append("\r\n-- Static data\r\n" + dataSteps));
            }
            catch (Exception ex) when (ex is SqlException or InvalidDataException)
            {
                var schema = script is null ? "" : " The schema changes were applied.";
                problems.Add($"Applying static data failed, and its transaction was rolled back.{schema} {ex.Message}");
                Finish($"static data failed, and its transaction was rolled back.{schema} {ex.Message}");
                return Result(script, script is not null);
            }
        }

        Finish("applied");
        return Result(script, true);

        ApplyResult Result(string? schemaScript, bool applied, string? previewDataScript = null) =>
            new(report, objects, schemaScript, messages, problems, applied, appliedData, previewDataScript, skipped, declined, savedScript);

        void Append(string text)
        {
            if (savedScript is not null)
            {
                DeploymentLog.Append(savedScript, text);
            }
        }

        void Finish(string outcome)
        {
            if (savedScript is not null)
            {
                DeploymentLog.Finish(savedScript, outcome);
            }
        }
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
