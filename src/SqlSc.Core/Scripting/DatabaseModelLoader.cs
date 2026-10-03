using System.Diagnostics;
using Microsoft.Data.SqlClient;
using Microsoft.SqlServer.Dac;
using Microsoft.SqlServer.Dac.Model;
using SqlSc.Core.Database;
using SqlSc.Core.Filtering;
using SqlSc.Core.Modeling;

namespace SqlSc.Core.Scripting;

/// <summary>How a database model was built, and why the faster catalog path was not used if it wasn't.</summary>
public sealed record DatabaseModelInfo(
    bool FromCatalog,
    int TrackedCount,
    int ScriptedCount,
    IReadOnlyList<string> Unsupported,
    TimeSpan Elapsed,
    IReadOnlyList<string>? LeftOut = null)
{
    public string Describe()
    {
        var reasons = Unsupported.Count == 0 ? string.Empty : $" (catalog scripting unsupported: {string.Join("; ", Unsupported.Take(5))}{(Unsupported.Count > 5 ? $"; {Unsupported.Count - 5} more" : string.Empty)})";
        return FromCatalog
            ? FormattableString.Invariant($"catalog scripting: {TrackedCount} tracked objects, {ScriptedCount - TrackedCount} dependencies, {Elapsed.TotalSeconds:0.00}s")
            : FormattableString.Invariant($"full DacFx extract, {Elapsed.TotalSeconds:0.00}s{reasons}{DescribeLeftOut(LeftOut)}");
    }

    internal static string DescribeLeftOut(IReadOnlyList<string>? leftOut) =>
        leftOut is not { Count: > 0 }
            ? string.Empty
            : FormattableString.Invariant($"; left out {leftOut.Count} untracked objects with unresolved references: {string.Join(", ", leftOut.Take(5))}{(leftOut.Count > 5 ? ", ..." : string.Empty)}");
}

public sealed class DatabaseModel(TSqlModel model, DatabaseModelInfo info) : IDisposable
{
    public TSqlModel Model { get; } = model;

    public DatabaseModelInfo Info { get; } = info;

    public void Dispose() => Model.Dispose();
}

/// <summary>
/// Builds a DacFx model of a live database. By default only the objects a filter tracks (and what they depend on)
/// are scripted from the catalog views, which is much faster than DacFx's full extract on large databases.
/// Databases using features the catalog scripter does not handle fall back to the full extract.
/// </summary>
public static class DatabaseModelLoader
{
    private const int MaxPasses = 5;
    private const int BatchSize = 500;

    public static DatabaseModel Load(
        string connectionString,
        SqlServerVersion platform,
        ObjectFilter filter,
        IEnumerable<(string? Schema, string Name)> referencedNames,
        bool fullExtract = false)
    {
        var stopwatch = Stopwatch.StartNew();
        IReadOnlyList<string> unsupported = ["--full-extract requested"];
        if (!fullExtract)
        {
            var (model, info) = TryLoadFromCatalog(connectionString, platform, filter, referencedNames);
            if (model is not null)
            {
                return new DatabaseModel(model, info);
            }

            unsupported = info.Unsupported;
        }

        var (full, leftOut) = LoadFull(connectionString, filter);
        return new DatabaseModel(full, new DatabaseModelInfo(false, 0, 0, unsupported, stopwatch.Elapsed, leftOut));
    }

    /// <summary>DacFx's full extract, without untracked objects that stop it being saved (see <see cref="BrokenObjects"/>).</summary>
    public static (TSqlModel Model, IReadOnlyList<string> LeftOut) LoadFull(string connectionString, ObjectFilter filter)
    {
        var server = ServerInfo.Query(connectionString);
        var model = SystemDatabase.LoadFromDatabase(connectionString, server.DatabaseName, server.Platform, new DacExtractOptions
        {
            ExtractReferencedServerScopedElements = true,
            IgnorePermissions = false,
        });
        SystemDatabase.AddMissingLogins(model);
        SystemDatabase.AddMissingMasterKey(model);
        ColumnNamedAliases.Apply(model);
        return (model, BrokenObjects.RemoveUntracked(model, filter));
    }

    /// <summary>Scripts tracked objects from the catalog; returns a null model, with the reasons, if that isn't possible.</summary>
    public static (TSqlModel? Model, DatabaseModelInfo Info) TryLoadFromCatalog(
        string connectionString,
        SqlServerVersion platform,
        ObjectFilter filter,
        IEnumerable<(string? Schema, string Name)> referencedNames)
    {
        var stopwatch = Stopwatch.StartNew();
        CatalogSnapshot snapshot;
        try
        {
            using var connection = new SqlConnection(connectionString);
            connection.Open();
            snapshot = CatalogSnapshot.Read(connection);
        }
        catch (SqlException ex)
        {
            return (null, new DatabaseModelInfo(false, 0, 0, ["reading the catalog failed: " + ex.Message], stopwatch.Elapsed));
        }

        var names = referencedNames.ToHashSet();
        TSqlModel? model = null;
        var scripted = -1;
        for (var pass = 1; ; pass++)
        {
            var plan = new CatalogScripter(snapshot).Plan(filter, names);
            if (plan.Unsupported.Count > 0)
            {
                model?.Dispose();
                return (null, new DatabaseModelInfo(false, plan.TrackedCount, plan.ScriptedCount, plan.Unsupported, stopwatch.Elapsed));
            }

            var info = new DatabaseModelInfo(true, plan.TrackedCount, plan.ScriptedCount, [], stopwatch.Elapsed);
            if (model is not null && plan.ScriptedCount == scripted)
            {
                return (model, info with { Elapsed = stopwatch.Elapsed });
            }

            model?.Dispose();
            if (pass > MaxPasses)
            {
                return (null, info with { FromCatalog = false, Unsupported = [FormattableString.Invariant($"dependencies still growing after {MaxPasses} passes")], Elapsed = stopwatch.Elapsed });
            }

            scripted = plan.ScriptedCount;
            model = Build(plan, platform, snapshot.Collation, out var problems);
            if (problems.Count > 0)
            {
                model.Dispose();
                return (null, info with { FromCatalog = false, Unsupported = problems, Elapsed = stopwatch.Elapsed });
            }

            var before = names.Count;
            names.UnionWith(UnresolvedNames(model));
            if (names.Count == before)
            {
                return (model, info with { Elapsed = stopwatch.Elapsed });
            }
        }
    }

    /// <summary>Names an object in the model refers to that are not in the model, as (schema, name) or (null, name).</summary>
    public static IEnumerable<(string? Schema, string Name)> UnresolvedNames(TSqlModel model)
    {
        var names = new HashSet<(string?, string)>();
        foreach (var obj in model.GetObjects(DacQueryScopes.UserDefined))
        {
            foreach (var reference in obj.GetReferencedRelationshipInstances(DacQueryScopes.All))
            {
                if (reference.Object is null && reference.ObjectName is { HasName: true, Parts: var parts })
                {
                    names.Add(parts.Count switch
                    {
                        1 => (null, parts[0]),
                        _ => (parts[^2], parts[^1]),
                    });
                    if (parts.Count >= 3)
                    {
                        names.Add((parts[^3], parts[^2]));
                    }
                }
            }
        }

        return names;
    }

    private static TSqlModel Build(ScriptPlan plan, SqlServerVersion platform, string collation, out List<string> problems)
    {
        problems = [];
        var model = SystemDatabase.CreateModel(platform, new TSqlModelOptions { Collation = collation });
        var batches = plan.Units
            .Where(u => u.Script.Length > 0)
            .GroupBy(u => (QuotedIdentifier: u.QuotedIdentifier ?? true, AnsiNulls: u.AnsiNulls ?? true))
            .SelectMany(g => g.Chunk(BatchSize).Select(chunk => (g.Key, Units: chunk)));
        foreach (var (key, units) in batches)
        {
            try
            {
                model.AddOrUpdateObjects(
                    ColumnNamedAliases.Rewrite(string.Join("\nGO\n", units.Select(u => u.Script)), key.QuotedIdentifier),
                    units[0].Source,
                    new TSqlObjectOptions { QuotedIdentifier = key.QuotedIdentifier, AnsiNulls = key.AnsiNulls });
            }
            catch (DacModelException ex)
            {
                var errors = ex.Messages.Where(m => m.MessageType == DacMessageType.Error).Select(m => $"{m.Prefix}{m.Number}: {m.Message}");
                var sources = string.Join(", ", units.Take(3).Select(u => u.Source)) + (units.Length > 3 ? ", ..." : string.Empty);
                problems.Add(FormattableString.Invariant($"{string.Join(" ", errors)} (in {sources})"));
                return model;
            }
        }

        SystemDatabase.AddMissingMasterKey(model);
        foreach (var error in model.GetModelErrors().Where(e => e.Severity == ModelErrorSeverity.Error))
        {
            problems.Add(FormattableString.Invariant($"{error.SourceName}: {error.Prefix}{error.ErrorCode} {error.Message}"));
        }

        foreach (var unit in plan.Units.Where(u => u.ModuleType is not null))
        {
            var typeClass = unit.ModuleType switch
            {
                "View" => View.TypeClass,
                "Procedure" => Procedure.TypeClass,
                "ScalarFunction" => ScalarFunction.TypeClass,
                "TableValuedFunction" => TableValuedFunction.TypeClass,
                _ => DmlTrigger.TypeClass,
            };
            if (model.GetObject(typeClass, new ObjectIdentifier(unit.Schema, unit.Name), DacQueryScopes.UserDefined) is null)
            {
                problems.Add(FormattableString.Invariant($"{unit.Source}: definition does not create [{unit.Schema}].[{unit.Name}] (renamed with sp_rename?)"));
            }
        }

        return model;
    }
}
