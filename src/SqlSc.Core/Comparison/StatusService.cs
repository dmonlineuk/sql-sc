using System.Diagnostics;
using Microsoft.SqlServer.Dac;
using Microsoft.SqlServer.Dac.Compare;
using Microsoft.SqlServer.Dac.Model;
using SqlSc.Core.ChangeTracking;
using SqlSc.Core.Database;
using SqlSc.Core.Modeling;
using SqlSc.Core.WorkingFolders;

namespace SqlSc.Core.Comparison;

/// <summary>
/// Compares a live database (source of truth in database-first development) with its working folder.
/// </summary>
public static class StatusService
{
    public static StatusReport GetStatus(WorkingFolder folder, string connectionString, bool includeChangedBy = true)
    {
        var timings = new Dictionary<string, TimeSpan>();
        var stopwatch = Stopwatch.StartNew();

        var server = ServerInfo.Query(connectionString);
        timings["server"] = Lap(stopwatch);

        using var folderModel = FolderModelLoader.Load(folder, server.Platform);
        timings["loadFolder"] = Lap(stopwatch);

        using var databaseModel = TSqlModel.LoadFromDatabase(connectionString, new ModelExtractOptions
        {
            LoadAsScriptBackedModel = true,
            ExtractReferencedServerScopedElements = true,
        });
        timings["loadDatabase"] = Lap(stopwatch);

        var files = folderModel.Model.GetObjects(DacQueryScopes.UserDefined)
            .Where(o => o.Name.HasName)
            .GroupBy(o => FormatName(o.Name)!, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => ScriptSource.Decode(g.First().GetSourceInformation()?.SourceName)?.File, StringComparer.OrdinalIgnoreCase);
        var borrowed = DatabaseReferences.AddMissing(folderModel.Model, databaseModel);
        timings["resolveReferences"] = Lap(stopwatch);

        var dacpac = Path.Combine(Path.GetTempPath(), $"sql-sc-{Guid.NewGuid():N}.dacpac");
        try
        {
            folderModel.BuildPackage(dacpac, server.DatabaseName);
            timings["buildPackage"] = Lap(stopwatch);

            var changes = Compare(connectionString, dacpac, files)
                .Concat(borrowed.TopLevel.Select(o => new ObjectChange(ObjectStatus.New, o.ObjectType.Name, FormatName(o.Name)!, null, null)))
                .OrderBy(c => c.ObjectType, StringComparer.Ordinal)
                .ThenBy(c => c.Name, StringComparer.OrdinalIgnoreCase)
                .ToList();
            timings["compare"] = Lap(stopwatch);

            var changeLog = includeChangedBy
                ? DefaultTraceReader.Read(connectionString)
                : new ChangeLog(false, "Not requested.", []);
            timings["changedBy"] = Lap(stopwatch);

            var withChangedBy = changes
                .Select(c => c with { LastChange = FindChange(changeLog, c.Name) })
                .ToList();

            return new StatusReport(
                server.ServerName,
                server.DatabaseName,
                server.Platform.ToString(),
                folderModel.ObjectCount,
                folderModel.Issues,
                withChangedBy,
                changeLog,
                timings);
        }
        finally
        {
            File.Delete(dacpac);
        }
    }

    public static DacDeployOptions CompareOptions() => new()
    {
        DropObjectsNotInSource = true,
        BlockOnPossibleDataLoss = true,
        IgnoreWhitespace = true,
        IgnoreKeywordCasing = true,
        IgnoreSemicolonBetweenStatements = true,
        AllowIncompatiblePlatform = true,
        ScriptDatabaseOptions = false,
    };

    private static List<ObjectChange> Compare(string connectionString, string dacpacPath, IReadOnlyDictionary<string, string?> files)
    {
        var comparison = new SchemaComparison(
            new SchemaCompareDatabaseEndpoint(connectionString),
            new SchemaCompareDacpacEndpoint(dacpacPath))
        {
            Options = CompareOptions(),
        };

        var result = comparison.Compare();
        if (!result.IsValid)
        {
            var errors = string.Join(Environment.NewLine, result.GetErrors().Select(e => e.Message));
            throw new InvalidOperationException($"Schema comparison failed:{Environment.NewLine}{errors}");
        }

        return result.Differences
            .Where(d => !DatabaseReferences.IsBorrowed(d.TargetObject))
            .Where(d => d.SourceObject is not { } obj || !DatabaseReferences.IsInfrastructure(obj))
            .Select(d => ToChange(d, files))
            .ToList();
    }

    private static ObjectChange ToChange(SchemaDifference difference, IReadOnlyDictionary<string, string?> files)
    {
        var obj = difference.SourceObject ?? difference.TargetObject;
        var status = difference.UpdateAction switch
        {
            SchemaUpdateAction.Add => ObjectStatus.New,
            SchemaUpdateAction.Delete => ObjectStatus.Deleted,
            _ => ObjectStatus.Modified,
        };

        var name = FormatName(obj?.Name) ?? difference.Name;
        return new ObjectChange(status, obj?.ObjectType.Name ?? "Unknown", name, files.GetValueOrDefault(name), null);
    }

    internal static string? FormatName(ObjectIdentifier? id) =>
        id is { HasName: true } ? string.Join('.', id.Parts.Select(p => $"[{p}]")) : null;

    private static ChangeEvent? FindChange(ChangeLog log, string bracketedName)
    {
        var parts = bracketedName.Split("].[", StringSplitOptions.None)
            .Select(p => p.Trim('[', ']'))
            .ToArray();
        return parts.Length >= 2 ? log.Find(parts[0], parts[1]) : log.Find(null, parts[0]);
    }

    private static TimeSpan Lap(Stopwatch stopwatch)
    {
        var elapsed = stopwatch.Elapsed;
        stopwatch.Restart();
        return elapsed;
    }
}
