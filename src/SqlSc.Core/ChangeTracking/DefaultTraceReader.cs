using Microsoft.Data.SqlClient;

namespace SqlSc.Core.ChangeTracking;

public enum ChangeKind
{
    Created,
    Altered,
    Deleted,
}

public sealed record ChangeEvent(
    DateTime StartTime,
    ChangeKind Kind,
    string? SchemaName,
    string ObjectName,
    string? LoginName,
    string? HostName,
    string? ApplicationName)
{
    public string Key => SchemaName is null ? ObjectName : $"{SchemaName}.{ObjectName}";
}

public sealed record ChangeLog(bool Available, string? UnavailableReason, IReadOnlyList<ChangeEvent> Events)
{
    /// <summary>The most recent event per object, keyed by "schema.name" (or "name" for objects without a schema).</summary>
    public IReadOnlyDictionary<string, ChangeEvent> Latest { get; } = Events
        .GroupBy(e => e.Key, StringComparer.OrdinalIgnoreCase)
        .ToDictionary(g => g.Key, g => g.MaxBy(e => e.StartTime)!, StringComparer.OrdinalIgnoreCase);

    public ChangeEvent? Find(string? schema, string name) =>
        (schema is not null && Latest.TryGetValue($"{schema}.{name}", out var qualified)) ? qualified
        : Latest.TryGetValue(name, out var unqualified) ? unqualified
        : null;
}

/// <summary>
/// Reads object created / altered / deleted events for the current database from SQL Server's default trace.
/// Needs ALTER TRACE (or VIEW SERVER STATE on newer versions); returns an unavailable log rather than throwing when it cannot.
/// </summary>
public static class DefaultTraceReader
{
    private const int ObjectCreated = 46;
    private const int ObjectDeleted = 47;
    private const int ObjectAltered = 164;

    // Auto-created statistics ('ST') are created by the engine, not by developers.
    private const int StatisticsObjectType = 21587;

    public static ChangeLog Read(string connectionString)
    {
        try
        {
            using var connection = new SqlConnection(connectionString);
            connection.Open();

            var currentFile = GetDefaultTracePath(connection);
            if (currentFile is null)
            {
                return new ChangeLog(false, "The default trace is disabled on this server.", []);
            }

            return new ChangeLog(true, null, ReadEvents(connection, RolloverBasePath(currentFile)));
        }
        catch (SqlException ex)
        {
            return new ChangeLog(false, ex.Message, []);
        }
    }

    /// <summary>
    /// The default trace writes log_N.trc rollover files; reading from "log.trc" in the same folder returns all of them.
    /// </summary>
    public static string RolloverBasePath(string currentFile)
    {
        var separator = currentFile.LastIndexOfAny(['\\', '/']);
        return separator < 0 ? "log.trc" : string.Concat(currentFile.AsSpan(0, separator + 1), "log.trc");
    }

    private static string? GetDefaultTracePath(SqlConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT path FROM sys.traces WHERE is_default = 1";
        return command.ExecuteScalar() as string;
    }

    private static List<ChangeEvent> ReadEvents(SqlConnection connection, string tracePath)
    {
        using var command = connection.CreateCommand();
        command.CommandText = $"""
            SELECT t.StartTime, t.EventClass,
                   CASE WHEN OBJECT_NAME(t.ObjectID, t.DatabaseID) = t.ObjectName THEN OBJECT_SCHEMA_NAME(t.ObjectID, t.DatabaseID) END,
                   t.ObjectName, t.LoginName, t.HostName, t.ApplicationName, t.ObjectID
            FROM sys.fn_trace_gettable(@path, DEFAULT) AS t
            WHERE t.EventClass IN ({ObjectCreated}, {ObjectDeleted}, {ObjectAltered})
              AND t.EventSubClass = 0
              AND t.DatabaseID = DB_ID()
              AND t.ObjectName IS NOT NULL
              AND ISNULL(t.ObjectType, 0) <> {StatisticsObjectType}
            ORDER BY t.StartTime
            """;
        command.Parameters.AddWithValue("@path", tracePath);

        // Dropped objects no longer resolve to a schema; recover it from earlier events for the same object id.
        var schemaById = new Dictionary<int, string>();
        var events = new List<ChangeEvent>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            var objectId = reader.IsDBNull(7) ? (int?)null : reader.GetInt32(7);
            var schema = reader.IsDBNull(2) ? null : reader.GetString(2);
            if (objectId is { } id)
            {
                if (schema is not null)
                {
                    schemaById[id] = schema;
                }
                else
                {
                    schema = schemaById.GetValueOrDefault(id);
                }
            }

            events.Add(new ChangeEvent(
                StartTime: reader.GetDateTime(0),
                Kind: reader.GetInt32(1) switch
                {
                    ObjectCreated => ChangeKind.Created,
                    ObjectDeleted => ChangeKind.Deleted,
                    _ => ChangeKind.Altered,
                },
                SchemaName: schema,
                ObjectName: reader.GetString(3),
                LoginName: reader.IsDBNull(4) ? null : reader.GetString(4),
                HostName: reader.IsDBNull(5) ? null : reader.GetString(5),
                ApplicationName: reader.IsDBNull(6) ? null : reader.GetString(6)));
        }

        return events;
    }
}
