using System.Diagnostics;
using System.Globalization;
using System.Text.RegularExpressions;
using Microsoft.Data.SqlClient;
using Microsoft.SqlServer.Dac;
using Microsoft.SqlServer.Dac.Model;
using SqlSc.Core.ChangeTracking;
using SqlSc.Core.Comparison;
using SqlSc.Core.Database;
using SqlSc.Core.Filtering;
using SqlSc.Core.Modeling;
using SqlSc.Core.Scripting;
using SqlSc.Core.WorkingFolders;

namespace SqlSc.Core.Diagnostics;

public enum CheckResult
{
    Ok,
    Warning,
    Failed,
    Skipped,
}

public sealed record DoctorCheck(string Name, CheckResult Result, string Detail);

/// <summary>
/// Checks everything sql-sc needs from a server, database and working folder, and reports it in a form that is
/// safe to share (no connection strings or passwords).
/// </summary>
public static partial class Doctor
{
    public static IReadOnlyList<DoctorCheck> Run(string? connectionString, WorkingFolder? folder, bool extract = true)
    {
        var checks = new List<DoctorCheck>();
        if (folder is not null)
        {
            checks.Add(Check("Working folder", () => CheckFolder(folder)));
        }

        if (connectionString is null)
        {
            checks.Add(new DoctorCheck("Connection", CheckResult.Skipped, "No connection string. Run `sql-sc link`, pass --connection or set SQLSC_CONNECTION."));
            return checks;
        }

        var connection = Check("Connection", () => CheckConnection(connectionString));
        checks.Add(connection);
        if (connection.Result == CheckResult.Failed)
        {
            if (ConnectionStrings.NeedsPassword(connectionString))
            {
                checks[^1] = connection with { Detail = $"{connection.Detail} (no password: set {ConnectionStrings.PasswordVariable})" };
            }

            return checks;
        }

        checks.Add(Check("Server", () => CheckServer(connectionString)));
        checks.Add(Check("Permissions", () => CheckPermissions(connectionString)));
        checks.Add(Check("Default trace", () => CheckDefaultTrace(connectionString)));
        checks.Add(Check("Latency", () => CheckLatency(connectionString)));
        CatalogSummary? catalog = null;
        checks.Add(Check("Catalog", () =>
        {
            catalog = CatalogSummary.Query(connectionString);
            return CheckCatalog(catalog);
        }));
        if (folder is not null && catalog is not null)
        {
            checks.Add(Check("Filter", () => CheckFilter(catalog, folder)));
        }

        var filter = folder?.Filter ?? ObjectFilter.IncludeAll;
        TSqlModel? scripted = null;
        try
        {
            checks.Add(Check("Catalog scripting", () =>
            {
                var (model, info) = DatabaseModelLoader.TryLoadFromCatalog(connectionString, ServerInfo.Query(connectionString).Platform, filter, []);
                scripted = model;
                return model is null
                    ? (CheckResult.Warning, "status will use the " + info.Describe())
                    : (CheckResult.Ok, info.Describe());
            }));
            checks.Add(extract
                ? Check("Schema extract", () => CheckExtract(connectionString, scripted, filter))
                : new DoctorCheck("Schema extract", CheckResult.Skipped, "Skipped (--no-extract)"));
        }
        finally
        {
            scripted?.Dispose();
        }

        return checks;
    }

    private static DoctorCheck Check(string name, Func<(CheckResult, string)> run)
    {
        try
        {
            var (result, detail) = run();
            return new DoctorCheck(name, result, detail);
        }
        catch (Exception ex) when (ex is SqlException or InvalidOperationException or IOException or InvalidDataException
            or UnauthorizedAccessException or DacModelException or DacServicesException or FormatException)
        {
            return new DoctorCheck(name, CheckResult.Failed, ex is DacServicesException ? DescribeErrors(ex.Message) : ex.Message.Split('\n')[0].Trim());
        }
    }

    private static (CheckResult, string) CheckFolder(WorkingFolder folder)
    {
        using var model = FolderModelLoader.Load(folder);
        var errors = model.Issues.Count(i => i.Severity == IssueSeverity.Error);
        var detail = Invariant($"{model.FileCount} files, {model.ObjectCount} objects, {errors} errors, {model.Issues.Count - errors} warnings; ")
            + $"filter: {folder.FilterPath ?? "none"}; layout: {(folder.RedgateInfo is null ? "no RedGateDatabaseInfo.xml" : "Redgate")}";
        return (errors > 0 ? CheckResult.Failed : CheckResult.Ok, detail);
    }

    private static (CheckResult, string) CheckConnection(string connectionString)
    {
        var stopwatch = Stopwatch.StartNew();
        using var connection = new SqlConnection(connectionString);
        connection.Open();
        var elapsed = stopwatch.Elapsed;
        var (login, user) = Query(connection, "SELECT SUSER_SNAME(), USER_NAME()", r => (r.GetString(0), r.GetString(1)));
        return (CheckResult.Ok, Invariant($"{ConnectionStrings.Describe(connectionString)} as {login} (user {user}) using {ConnectionStrings.DescribeAuthentication(connectionString)}, {elapsed.TotalSeconds:0.00}s"));
    }

    private static (CheckResult, string) CheckServer(string connectionString)
    {
        var info = ServerInfo.Query(connectionString);
        using var connection = new SqlConnection(connectionString);
        connection.Open();
        var edition = Query(
            connection,
            """
            SELECT CAST(SERVERPROPERTY('Edition') AS nvarchar(128)),
                   CAST(DATABASEPROPERTYEX(DB_NAME(), 'Edition') AS nvarchar(128)),
                   CAST(DATABASEPROPERTYEX(DB_NAME(), 'ServiceObjective') AS nvarchar(128))
            """,
            r => info.EngineEdition == 5 && !r.IsDBNull(1)
                ? Invariant($"{r.GetString(0)} {r.GetString(1)} {(r.IsDBNull(2) ? string.Empty : r.GetString(2))}").TrimEnd()
                : r.GetString(0));
        var detail = Invariant($"{info.ProductVersion} {edition} (engine edition {info.EngineEdition}), platform {info.Platform}");
        return info.EngineEdition switch
        {
            5 => (CheckResult.Warning, detail + "; Azure SQL Database has no default trace, so Changed by is unavailable"),
            8 => (CheckResult.Ok, detail),
            _ when info.MajorVersion < 13 => (CheckResult.Warning, detail + "; SQL Server 2016 or later is recommended"),
            _ => (CheckResult.Ok, detail),
        };
    }

    private static (CheckResult, string) CheckPermissions(string connectionString)
    {
        using var connection = new SqlConnection(connectionString);
        connection.Open();
        var (viewDefinition, alterTrace, viewServerState) = Query(
            connection,
            """
            SELECT HAS_PERMS_BY_NAME(DB_NAME(), 'DATABASE', 'VIEW DEFINITION'),
                   HAS_PERMS_BY_NAME(NULL, NULL, 'ALTER TRACE'),
                   HAS_PERMS_BY_NAME(NULL, NULL, 'VIEW SERVER STATE')
            """,
            r => (Flag(r, 0), Flag(r, 1), Flag(r, 2)));
        var detail = $"VIEW DEFINITION {YesNo(viewDefinition)}, ALTER TRACE {YesNo(alterTrace)}, VIEW SERVER STATE {YesNo(viewServerState)}";
        if (viewDefinition != true)
        {
            return (CheckResult.Failed, detail + "; VIEW DEFINITION is needed to read the schema");
        }

        return alterTrace == true || viewServerState == true
            ? (CheckResult.Ok, detail)
            : (CheckResult.Warning, detail + "; Changed by needs ALTER TRACE");
    }

    private static (CheckResult, string) CheckDefaultTrace(string connectionString)
    {
        using (var connection = new SqlConnection(connectionString))
        {
            connection.Open();
            var enabled = Query(connection, "SELECT CAST(value_in_use AS int) FROM sys.configurations WHERE name = 'default trace enabled'", r => r.GetInt32(0));
            if (enabled == 0)
            {
                return (CheckResult.Warning, "Disabled on this server ('default trace enabled' = 0)");
            }
        }

        var log = DefaultTraceReader.Read(connectionString);
        if (!log.Available)
        {
            return (CheckResult.Warning, $"Unavailable: {log.UnavailableReason}");
        }

        if (log.Events.Count == 0)
        {
            return (CheckResult.Ok, "Readable; no object changes recorded for this database yet");
        }

        var oldest = log.Events.Min(e => e.StartTime);
        return (CheckResult.Ok, Invariant($"Readable; {log.Events.Count} object changes for this database since {oldest:yyyy-MM-dd HH:mm}"));
    }

    private static (CheckResult, string) CheckLatency(string connectionString)
    {
        const int RoundTrips = 5;
        using var connection = new SqlConnection(connectionString);
        connection.Open();
        using var command = new SqlCommand("SELECT 1", connection);
        command.ExecuteScalar();
        var stopwatch = Stopwatch.StartNew();
        for (var i = 0; i < RoundTrips; i++)
        {
            command.ExecuteScalar();
        }

        return (CheckResult.Ok, Invariant($"{stopwatch.Elapsed.TotalMilliseconds / RoundTrips:0.0} ms per round trip (average of {RoundTrips})"));
    }

    private static (CheckResult, string) CheckCatalog(CatalogSummary catalog)
    {
        var c = catalog.Children;
        var detail = Invariant($"{catalog.Objects.Count} objects ({Top(CatalogSummary.Largest(catalog.Objects, o => o.ObjectType, 6))}); ")
            + Invariant($"children: {c.Columns} columns, {c.Indexes} indexes, {c.Constraints} constraints, {c.Triggers} triggers, ")
            + Invariant($"{c.Statistics} statistics, {c.Permissions} permissions, {c.ExtendedProperties} extended properties, {c.RoleMembers} role members; ")
            + Invariant($"code {catalog.ModuleBytes / 1048576.0:0.0} MB; largest schemas: {Top(CatalogSummary.Largest(catalog.Objects, o => o.Schema, 5))}; ")
            + Invariant($"read in {catalog.Elapsed.TotalSeconds:0.00}s");
        return (CheckResult.Ok, detail);
    }

    private static (CheckResult, string) CheckFilter(CatalogSummary catalog, WorkingFolder folder)
    {
        if (folder.Filter.IsEmpty)
        {
            return (CheckResult.Ok, Invariant($"No filter: all {catalog.Objects.Count} objects are tracked"));
        }

        var tracked = catalog.Tracked(folder.Filter);
        var schemas = tracked.Select(o => o.Schema).OfType<string>().Distinct(StringComparer.Ordinal).Count();
        return (CheckResult.Ok, Invariant($"{folder.FilterPath} keeps {tracked.Count} of {catalog.Objects.Count} objects, in {schemas} schemas"));
    }

    private static string Top(IEnumerable<(string Key, int Count)> counts) =>
        string.Join(", ", counts.Select(c => Invariant($"{c.Key} {c.Count}")));

    private static (CheckResult, string) CheckExtract(string connectionString, TSqlModel? scripted, ObjectFilter filter)
    {
        var stopwatch = Stopwatch.StartNew();
        var (model, leftOut) = DatabaseModelLoader.LoadFull(connectionString, filter);
        using var disposeModel = model;
        var elapsed = stopwatch.Elapsed;
        var objects = model.GetObjects(DacQueryScopes.UserDefined).ToList();
        var largest = objects.GroupBy(o => o.ObjectType.Name, StringComparer.Ordinal)
            .Select(g => (g.Key, Count: g.Count()))
            .OrderByDescending(g => g.Count)
            .ThenBy(g => g.Key, StringComparer.Ordinal)
            .Take(6);
        var detail = Invariant($"{objects.Count} objects in {elapsed.TotalSeconds:0.00}s ({Top(largest)})") + DatabaseModelInfo.DescribeLeftOut(leftOut);
        if (scripted is null)
        {
            return (CheckResult.Ok, detail);
        }

        var differences = StatusService.CompareModels(model, scripted, filter);
        return differences.Count == 0
            ? (CheckResult.Ok, detail + "; catalog scripting matches it")
            : (CheckResult.Warning, detail + Invariant($"; catalog scripting differs in {differences.Count} places: {string.Join("; ", differences.Take(10))}"));
    }

    /// <summary>
    /// DacFx lists one error per line after its first line. Gives the first error of each object (up to
    /// <see cref="ErrorObjectsShown"/>), so it's clear what stops a model being saved.
    /// </summary>
    internal static string DescribeErrors(string message)
    {
        var lines = message.Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        if (lines.Length <= 1)
        {
            return lines.FirstOrDefault() ?? string.Empty;
        }

        var objects = lines.Skip(1)
            .Select(l => ErrorLine().Match(l))
            .Where(m => m.Success)
            .GroupBy(m => m.Groups["element"].Value, StringComparer.OrdinalIgnoreCase)
            .Select(g => Invariant($"{g.Key} {g.First().Groups["code"].Value}{(g.First().Groups["message"].Success ? ": " + g.First().Groups["message"].Value : string.Empty)}"))
            .ToList();
        var more = objects.Count > ErrorObjectsShown ? Invariant($"; and {objects.Count - ErrorObjectsShown} more") : string.Empty;
        return Invariant($"{lines[0].TrimEnd(':')}: {lines.Length - 1} errors in {objects.Count} objects: {string.Join("; ", objects.Take(ErrorObjectsShown))}{more}");
    }

    private const int ErrorObjectsShown = 10;

    [GeneratedRegex(@"^Error (?<code>SQL\d+): Error validating element (?<element>\[.*?\](?:\.\[.*?\])*)(?::\s*(?<message>.*))?$")]
    private static partial Regex ErrorLine();

    private static T Query<T>(SqlConnection connection, string sql, Func<SqlDataReader, T> read)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        using var reader = command.ExecuteReader();
        if (!reader.Read())
        {
            throw new InvalidOperationException($"No rows from: {sql}");
        }

        return read(reader);
    }

    private static bool? Flag(SqlDataReader reader, int ordinal) => reader.IsDBNull(ordinal) ? null : reader.GetInt32(ordinal) == 1;

    private static string YesNo(bool? value) => value switch { true => "yes", false => "no", null => "unknown" };

    private static string Invariant(FormattableString value) => value.ToString(CultureInfo.InvariantCulture);
}
