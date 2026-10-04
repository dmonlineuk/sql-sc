using System.Diagnostics;
using Microsoft.SqlServer.Dac;
using Microsoft.SqlServer.Dac.Compare;
using Microsoft.SqlServer.Dac.Model;
using SqlSc.Core.ChangeTracking;
using SqlSc.Core.Database;
using SqlSc.Core.Diagnostics;
using SqlSc.Core.Filtering;
using SqlSc.Core.Modeling;
using SqlSc.Core.Scripting;
using SqlSc.Core.Settings;
using SqlSc.Core.WorkingFolders;

namespace SqlSc.Core.Comparison;

/// <summary>
/// Compares a live database (source of truth in database-first development) with its working folder.
/// </summary>
public static class StatusService
{
    /// <summary>Child types that sit directly under a schema but belong to it rather than being schema-scoped objects.</summary>
    private static readonly HashSet<string> SchemaChildTypes = new(StringComparer.Ordinal) { "ExtendedProperty", "Permission" };

    /// <summary>Relationships that point from a parentless child object to the object it belongs to.</summary>
    private static readonly HashSet<string> OwnerRelationships = new(StringComparer.Ordinal) { "Host", "SecuredObject" };

    /// <summary>Types the default trace never reports by their own name.</summary>
    private static readonly HashSet<string> UntracedTypes = new(StringComparer.Ordinal) { "ExtendedProperty", "Permission", "RoleMembership" };

    public static StatusReport GetStatus(WorkingFolder folder, string connectionString, bool includeChangedBy = true, bool fullExtract = false, bool includeDifferences = false)
    {
        var timings = new Dictionary<string, TimeSpan>();
        var stopwatch = Stopwatch.StartNew();

        var server = ServerInfo.Query(connectionString);
        var explicitConstraintNames = ConstraintNames.ExplicitWithGeneratedStyle(connectionString);
        timings["server"] = Lap(stopwatch);

        using var folderModel = FolderModelLoader.Load(folder, server.Platform, explicitConstraintNames);
        timings["loadFolder"] = Lap(stopwatch);

        using var database = DatabaseModelLoader.Load(
            connectionString,
            server.Platform,
            folder.Filter,
            DatabaseModelLoader.UnresolvedNames(folderModel.Model),
            fullExtract);
        var databaseModel = database.Model;
        timings["loadDatabase"] = Lap(stopwatch);

        var files = folderModel.Model.GetObjects(DacQueryScopes.UserDefined)
            .Where(o => o.Name.HasName)
            .GroupBy(o => FormatName(o.Name)!, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => ScriptSource.Decode(g.First().GetSourceInformation()?.SourceName)?.File, StringComparer.OrdinalIgnoreCase);
        var borrowed = DatabaseReferences.AddMissing(folderModel.Model, databaseModel);
        timings["resolveReferences"] = Lap(stopwatch);

        var workDirectory = Path.Combine(Path.GetTempPath(), $"sql-sc-{Guid.NewGuid():N}");
        Directory.CreateDirectory(workDirectory);
        try
        {
            var databasePackage = Path.Combine(workDirectory, "database.dacpac");
            var folderPackage = Path.Combine(workDirectory, "folder.dacpac");
            try
            {
                DacPackageExtensions.BuildPackage(databasePackage, databaseModel, new PackageMetadata { Name = server.DatabaseName });
            }
            catch (DacServicesException ex)
            {
                throw new InvalidDataException($"The database model can't be saved ({database.Info.Describe()}). {Doctor.DescribeErrors(ex.Message)}", ex);
            }

            try
            {
                folderModel.BuildPackage(folderPackage, server.DatabaseName);
            }
            catch (DacServicesException ex)
            {
                throw new InvalidDataException($"The folder model can't be saved. {Doctor.DescribeErrors(ex.Message)}{DescribeCopied(ex.Message, borrowed)}", ex);
            }

            timings["buildPackage"] = Lap(stopwatch);

            var compare = folder.Settings.Compare;
            var collations = includeDifferences
                ? new[] { databaseModel.CopyModelOptions().Collation, folderModel.Model.CopyModelOptions().Collation }
                    .OfType<string>()
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList()
                : null;
            var raw = Compare(databasePackage, folderPackage, compare.ToDeployOptions(), collations)
                .Concat(borrowed.Objects.Select(o => new RawChange(ObjectStatus.New, o, FormatName(o.Name)!)))
                .Where(r => compare.Includes(r.Object.ObjectType.Name))
                .ToList();
            timings["compare"] = Lap(stopwatch);

            var changeLog = includeChangedBy
                ? DefaultTraceReader.Read(connectionString)
                : new ChangeLog(false, "Not requested.", []);
            timings["changedBy"] = Lap(stopwatch);

            return new StatusReport(
                server.ServerName,
                server.DatabaseName,
                server.Platform.ToString(),
                folderModel.ObjectCount,
                folder.FilterPath,
                folderModel.Issues,
                Group(raw, files, folder.Filter, changeLog),
                changeLog,
                timings,
                database.Info);
        }
        finally
        {
            Directory.Delete(workDirectory, recursive: true);
        }
    }

    /// <summary>
    /// Differences between two models of the same database, for objects <paramref name="filter"/> tracks,
    /// formatted as "Status Type Name". Used to check catalog scripting against DacFx's full extract.
    /// </summary>
    internal static IReadOnlyList<string> CompareModels(TSqlModel expected, TSqlModel actual, ObjectFilter filter)
    {
        var workDirectory = Path.Combine(Path.GetTempPath(), $"sql-sc-{Guid.NewGuid():N}");
        Directory.CreateDirectory(workDirectory);
        try
        {
            var expectedPackage = Path.Combine(workDirectory, "expected.dacpac");
            var actualPackage = Path.Combine(workDirectory, "actual.dacpac");
            DacPackageExtensions.BuildPackage(expectedPackage, expected, new PackageMetadata { Name = "expected" });
            DacPackageExtensions.BuildPackage(actualPackage, actual, new PackageMetadata { Name = "actual" });
            return Compare(expectedPackage, actualPackage, new CompareSettings().ToDeployOptions())
                .Select(r => (Raw: r, Owner: OwnerOf(r.Object)))
                .Where(x => !DatabaseReferences.IsInfrastructure(x.Owner) && filter.Includes(x.Owner.ObjectType.Name, x.Owner.Name.Parts))
                .Select(x => $"{x.Raw.Status} {x.Raw.Object.ObjectType.Name} {x.Raw.Name}")
                .Distinct(StringComparer.Ordinal)
                .ToList();
        }
        finally
        {
            Directory.Delete(workDirectory, recursive: true);
        }
    }

    private static List<RawChange> Compare(string databasePackage, string folderPackage, DacDeployOptions options, IReadOnlyCollection<string>? differenceCollations = null)
    {
        var comparison = new SchemaComparison(
            new SchemaCompareDacpacEndpoint(databasePackage),
            new SchemaCompareDacpacEndpoint(folderPackage))
        {
            Options = options,
        };

        var result = comparison.Compare();
        if (!result.IsValid)
        {
            var errors = string.Join(Environment.NewLine, result.GetErrors().Select(e => e.Message));
            throw new InvalidOperationException($"Schema comparison failed:{Environment.NewLine}{errors}");
        }

        return result.Differences
            .SelectMany(Flatten)
            .Where(d => !DatabaseReferences.IsBorrowed(d.TargetObject))
            .Select(d => (Difference: d, Object: d.SourceObject ?? d.TargetObject))
            .Where(x => x.Object is not null && !DatabaseReferences.IsInfrastructure(x.Object))
            .Select(x => new RawChange(
                x.Difference.UpdateAction switch
                {
                    SchemaUpdateAction.Add => ObjectStatus.New,
                    SchemaUpdateAction.Delete => ObjectStatus.Deleted,
                    _ => ObjectStatus.Modified,
                },
                x.Object!,
                FormatName(x.Object!.Name) ?? x.Difference.Name,
                differenceCollations is not null && x.Difference.UpdateAction == SchemaUpdateAction.Change
                    ? ScriptDifference.First(result.GetDiffEntrySourceScript(x.Difference), result.GetDiffEntryTargetScript(x.Difference), "in the database", "in the folder", differenceCollations)
                    : null))
            .ToList();
    }

    /// <summary>
    /// A difference and its child differences. A changed object whose own definition is unchanged is only reported
    /// through its children (e.g. a view whose permissions changed).
    /// </summary>
    private static IEnumerable<SchemaDifference> Flatten(SchemaDifference difference)
    {
        var children = difference.Children.SelectMany(Flatten).ToList();
        var unchangedItself = children.Count > 0
            && difference.UpdateAction == SchemaUpdateAction.Change
            && difference.SourceObject is { } source && difference.TargetObject is { } target
            && NormalizeScript(source) == NormalizeScript(target);
        return unchangedItself ? children : children.Prepend(difference);
    }

    private static string? NormalizeScript(TSqlObject obj) =>
        obj.TryGetScript(out var script) ? string.Join(' ', script.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)) : null;

    private static List<ObjectChange> Group(
        IEnumerable<RawChange> raw,
        IReadOnlyDictionary<string, string?> files,
        ObjectFilter filter,
        ChangeLog changeLog)
    {
        return raw
            .Select(r => (Raw: r, Owner: OwnerOf(r.Object)))
            .Where(x => !DatabaseReferences.IsInfrastructure(x.Owner))
            .Where(x => filter.Includes(x.Owner.ObjectType.Name, x.Owner.Name.Parts))
            .GroupBy(x => Key(x.Owner, x.Raw.Name), StringComparer.OrdinalIgnoreCase)
            .Select(g =>
            {
                var owner = g.First().Owner;
                var name = FormatName(owner.Name) ?? g.First().Raw.Name;
                var self = g.Select(x => x.Raw).FirstOrDefault(r => Key(r.Object, r.Name) == g.Key);
                var children = g.Select(x => x.Raw)
                    .Where(r => r != self && (self is null || self.Status == ObjectStatus.Modified || r.Status != self.Status))
                    .Select(r => new ChildChange(r.Status, r.Object.ObjectType.Name, r.Name))
                    .Distinct()
                    .OrderBy(c => c.ObjectType, StringComparer.Ordinal)
                    .ThenBy(c => c.Name, StringComparer.OrdinalIgnoreCase)
                    .ToList();
                var lastChange = g.Select(x => x.Raw.Object)
                    .Prepend(owner)
                    .Select(o => FindChange(changeLog, o))
                    .OfType<ChangeEvent>()
                    .MaxBy(e => e.StartTime);
                return new ObjectChange(
                    self?.Status ?? ObjectStatus.Modified,
                    owner.ObjectType.Name,
                    name,
                    files.GetValueOrDefault(name),
                    lastChange,
                    children,
                    self?.Difference is not { } difference ? null
                        : difference != ScriptDifference.Same ? difference
                        : children.Count == 0 ? SameScriptsDifference
                        : null);
            })
            .OrderBy(c => c.ObjectType, StringComparer.Ordinal)
            .ThenBy(c => c.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>Names objects copied from the database that clash with one already in the folder model.</summary>
    private static string DescribeCopied(string message, BorrowedObjects borrowed)
    {
        var clashes = borrowed.Objects
            .Select(o => (o.ObjectType.Name, Name: FormatName(o.Name)))
            .Where(o => o.Name is not null && message.Contains($"element {o.Name}: The model already has", StringComparison.Ordinal))
            .Select(o => $"{o.Item1} {o.Name}")
            .ToList();
        return clashes.Count == 0 ? string.Empty : $" Copied from the database although the folder has the same name: {string.Join(", ", clashes)}.";
    }

    /// <summary>The object whose script file contains <paramref name="obj"/>: e.g. a constraint's table.</summary>
    internal static TSqlObject OwnerOf(TSqlObject obj)
    {
        var current = obj;
        while (current.GetParent() is { } parent
            && (parent.ObjectType.Name != "Schema" || SchemaChildTypes.Contains(current.ObjectType.Name)))
        {
            current = parent;
        }

        if (ReferenceEquals(current, obj) && obj.GetParent() is null)
        {
            var owner = obj.GetReferencedRelationshipInstances(DacQueryScopes.All)
                .FirstOrDefault(r => OwnerRelationships.Contains(r.Relationship.Name) && r.Object is not null)?.Object;
            if (owner is not null)
            {
                return OwnerOf(owner);
            }
        }

        return current;
    }

    internal static string? FormatName(ObjectIdentifier? id) =>
        id is { HasName: true } ? string.Join('.', id.Parts.Select(p => $"[{p}]")) : null;

    private static string Key(TSqlObject obj, string fallbackName) =>
        $"{obj.ObjectType.Name}|{FormatName(obj.Name) ?? fallbackName}";

    private static ChangeEvent? FindChange(ChangeLog log, TSqlObject obj)
    {
        if (!obj.Name.HasName || UntracedTypes.Contains(obj.ObjectType.Name))
        {
            return null;
        }

        var parts = obj.Name.Parts;
        return parts.Count >= 2 ? log.Find(parts[^2], parts[^1]) : log.Find(null, parts[0]);
    }

    private static TimeSpan Lap(Stopwatch stopwatch)
    {
        var elapsed = stopwatch.Elapsed;
        stopwatch.Restart();
        return elapsed;
    }

    private const string SameScriptsDifference = "scripts match apart from layout and default collations, so the difference is in a setting outside the script text";

    private sealed record RawChange(ObjectStatus Status, TSqlObject Object, string Name, string? Difference = null);
}
