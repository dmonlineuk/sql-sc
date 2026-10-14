using Microsoft.Data.SqlClient;
using Microsoft.SqlServer.Dac.Model;

namespace SqlSc.Core.ChangeTracking;

/// <summary>Whether the change log's table and trigger exist, and what the log holds.</summary>
public sealed record ChangeLogState(bool TableExists, bool TriggerExists, bool TriggerEnabled, int Events, DateTime? Oldest, DateTime? Newest)
{
    public bool Installed => TableExists && TriggerExists && TriggerEnabled;

    public bool Absent => !TableExists && !TriggerExists;
}

/// <summary>One row of <c>[sqlsc].[ChangeLog]</c>: the fields of a DDL event's EVENTDATA() that identify what changed.</summary>
public sealed record ChangeLogRow(
    DateTime EventTime,
    string EventType,
    string? LoginName,
    string? HostName,
    string? ApplicationName,
    string? SchemaName,
    string? ObjectName,
    string? ObjectType,
    string? TargetObjectName,
    string? NewObjectName);

/// <summary>
/// A database DDL trigger and table that record who changes which object. Azure SQL Database has no default trace,
/// and the default trace on SQL Server and MI only keeps recent changes, so this is how Changed by works there.
/// Installing needs ALTER ANY DATABASE DDL TRIGGER and CREATE TABLE (db_owner), once; after that every login's DDL is logged.
/// </summary>
public static class ChangeLogTable
{
    public const string SchemaName = "sqlsc";

    public const string TableName = "ChangeLog";

    public const string TriggerName = "sqlsc_ChangeLog";

    /// <summary>
    /// The trigger runs as dbo so that logins without INSERT on the table are logged, and records ORIGINAL_LOGIN().
    /// Triggers run with XACT_ABORT ON and a failed INSERT would roll back the user's DDL, so it is turned off and the
    /// INSERT runs through sp_executesql inside TRY/CATCH: a missing table or an error in the trigger never blocks DDL.
    /// The XML methods need QUOTED_IDENTIFIER (fixed when the trigger is created) and the ANSI options set here, whatever the session uses.
    /// Index rebuilds and statistics updates are maintenance, not schema changes.
    /// </summary>
    public static IReadOnlyList<string> InstallBatches { get; } =
    [
        $"IF SCHEMA_ID(N'{SchemaName}') IS NULL EXEC(N'CREATE SCHEMA [{SchemaName}] AUTHORIZATION [dbo]');",
        $"""
        IF OBJECT_ID(N'[{SchemaName}].[{TableName}]', N'U') IS NULL
        CREATE TABLE [{SchemaName}].[{TableName}] (
            [Id]               bigint         IDENTITY (1, 1) NOT NULL,
            [EventTime]        datetime2 (3)  NOT NULL,
            [EventType]        nvarchar (128) NOT NULL,
            [LoginName]        nvarchar (256) NULL,
            [HostName]         nvarchar (128) NULL,
            [ApplicationName]  nvarchar (256) NULL,
            [SchemaName]       nvarchar (128) NULL,
            [ObjectName]       nvarchar (256) NULL,
            [ObjectType]       nvarchar (128) NULL,
            [TargetObjectName] nvarchar (256) NULL,
            [NewObjectName]    nvarchar (256) NULL,
            CONSTRAINT [PK_sqlsc_ChangeLog] PRIMARY KEY CLUSTERED ([Id])
        );
        """,
        $"GRANT SELECT ON [{SchemaName}].[{TableName}] TO [public];",
        "SET QUOTED_IDENTIFIER ON;",
        "SET ANSI_NULLS ON;",
        $"""
        CREATE OR ALTER TRIGGER [{TriggerName}] ON DATABASE WITH EXECUTE AS 'dbo'
        FOR DDL_DATABASE_LEVEL_EVENTS
        AS
        BEGIN
            SET NOCOUNT ON;
            SET XACT_ABORT OFF;
            SET ANSI_PADDING ON;
            SET ANSI_WARNINGS ON;
            SET ARITHABORT ON;
            SET CONCAT_NULL_YIELDS_NULL ON;
            SET NUMERIC_ROUNDABORT OFF;
            BEGIN TRY
                DECLARE @event xml = EVENTDATA();
                DECLARE @type nvarchar(128) = @event.value(N'(/EVENT_INSTANCE/EventType)[1]', N'nvarchar(128)');
                DECLARE @command nvarchar(max) = @event.value(N'(/EVENT_INSTANCE/TSQLCommand/CommandText)[1]', N'nvarchar(max)');
                IF @type = N'UPDATE_STATISTICS' OR (@type = N'ALTER_INDEX' AND (@command LIKE N'%REBUILD%' OR @command LIKE N'%REORGANIZE%'))
                    RETURN;

                DECLARE @schema nvarchar(128) = @event.value(N'(/EVENT_INSTANCE/SchemaName)[1]', N'nvarchar(128)');
                DECLARE @object nvarchar(256) = @event.value(N'(/EVENT_INSTANCE/ObjectName)[1]', N'nvarchar(256)');
                DECLARE @objectType nvarchar(128) = @event.value(N'(/EVENT_INSTANCE/ObjectType)[1]', N'nvarchar(128)');
                DECLARE @target nvarchar(256) = @event.value(N'(/EVENT_INSTANCE/TargetObjectName)[1]', N'nvarchar(256)');
                DECLARE @newName nvarchar(256) = @event.value(N'(/EVENT_INSTANCE/NewObjectName)[1]', N'nvarchar(256)');
                DECLARE @login nvarchar(256) = ORIGINAL_LOGIN();
                DECLARE @host nvarchar(128) = HOST_NAME();
                DECLARE @application nvarchar(256) = APP_NAME();
                EXEC sys.sp_executesql
                    N'INSERT INTO [{SchemaName}].[{TableName}] ([EventTime], [EventType], [LoginName], [HostName], [ApplicationName], [SchemaName], [ObjectName], [ObjectType], [TargetObjectName], [NewObjectName])
                      VALUES (SYSDATETIME(), @type, @login, @host, @application, @schema, @object, @objectType, @target, @newName);',
                    N'@type nvarchar(128), @login nvarchar(256), @host nvarchar(128), @application nvarchar(256), @schema nvarchar(128), @object nvarchar(256), @objectType nvarchar(128), @target nvarchar(256), @newName nvarchar(256)',
                    @type, @login, @host, @application, @schema, @object, @objectType, @target, @newName;
            END TRY
            BEGIN CATCH
            END CATCH
        END
        """,
        $"ENABLE TRIGGER [{TriggerName}] ON DATABASE;",
    ];

    /// <summary>Drops the trigger first, so dropping the table isn't logged; the schema goes too if nothing else is in it.</summary>
    public static IReadOnlyList<string> RemoveBatches { get; } =
    [
        $"DROP TRIGGER IF EXISTS [{TriggerName}] ON DATABASE;",
        $"DROP TABLE IF EXISTS [{SchemaName}].[{TableName}];",
        $"""
        IF SCHEMA_ID(N'{SchemaName}') IS NOT NULL
            AND NOT EXISTS (SELECT 1 FROM sys.objects WHERE schema_id = SCHEMA_ID(N'{SchemaName}'))
            AND NOT EXISTS (SELECT 1 FROM sys.types WHERE schema_id = SCHEMA_ID(N'{SchemaName}'))
            EXEC(N'DROP SCHEMA [{SchemaName}]');
        """,
    ];

    public static string Script(IReadOnlyList<string> batches) => string.Concat(batches.Select(b => b.TrimEnd() + "\r\nGO\r\n"));

    public static void Install(string connectionString) => Run(connectionString, InstallBatches);

    public static void Remove(string connectionString) => Run(connectionString, RemoveBatches);

    /// <summary>Whether <paramref name="obj"/> is the change log's schema, table or trigger, or part of them; sql-sc never tracks these.</summary>
    public static bool Owns(TSqlObject obj) => obj.Name.Parts switch
    {
        [var name] when obj.ObjectType.Name == "DatabaseDdlTrigger" => Is(name, TriggerName),
        [var schema] when obj.ObjectType.Name == "Schema" => Is(schema, SchemaName),
        [var schema, var name, ..] => Is(schema, SchemaName) && (Is(name, TableName) || Is(name, "PK_sqlsc_ChangeLog")),
        _ => false,
    };

    public static ChangeLogState QueryState(string connectionString)
    {
        using var connection = new SqlConnection(connectionString);
        connection.Open();
        var (tableExists, triggerExists, triggerEnabled) = Single(connection, $"""
            SELECT CAST(CASE WHEN OBJECT_ID(N'[{SchemaName}].[{TableName}]', N'U') IS NULL THEN 0 ELSE 1 END AS bit),
                   CAST(CASE WHEN t.object_id IS NULL THEN 0 ELSE 1 END AS bit),
                   CAST(CASE WHEN t.is_disabled = 0 THEN 1 ELSE 0 END AS bit)
            FROM (SELECT 1 AS x) AS one
            LEFT JOIN sys.triggers AS t ON t.parent_class = 0 AND t.name = N'{TriggerName}'
            """, r => (r.GetBoolean(0), r.GetBoolean(1), r.GetBoolean(2)));
        if (!tableExists)
        {
            return new ChangeLogState(false, triggerExists, triggerEnabled, 0, null, null);
        }

        var (events, oldest, newest) = Single(
            connection,
            $"SELECT COUNT(*), MIN([EventTime]), MAX([EventTime]) FROM [{SchemaName}].[{TableName}]",
            r => (r.GetInt32(0), r.IsDBNull(1) ? (DateTime?)null : r.GetDateTime(1), r.IsDBNull(2) ? (DateTime?)null : r.GetDateTime(2)));
        return new ChangeLogState(true, triggerExists, triggerEnabled, events, oldest, newest);
    }

    /// <summary>The logged changes, or null when the change log isn't installed in this database.</summary>
    public static ChangeLog? Read(string connectionString)
    {
        try
        {
            using var connection = new SqlConnection(connectionString);
            connection.Open();
            using var exists = new SqlCommand($"SELECT OBJECT_ID(N'[{SchemaName}].[{TableName}]', N'U')", connection);
            if (exists.ExecuteScalar() is not int)
            {
                return null;
            }

            using var command = new SqlCommand(
                $"""
                SELECT [EventTime], [EventType], [LoginName], [HostName], [ApplicationName], [SchemaName], [ObjectName], [ObjectType], [TargetObjectName], [NewObjectName]
                FROM [{SchemaName}].[{TableName}]
                ORDER BY [Id]
                """,
                connection)
            { CommandTimeout = 300 };
            using var reader = command.ExecuteReader();
            var events = new List<ChangeEvent>();
            while (reader.Read())
            {
                events.AddRange(ToEvents(new ChangeLogRow(
                    reader.GetDateTime(0),
                    reader.GetString(1),
                    Text(reader, 2),
                    Text(reader, 3),
                    Text(reader, 4),
                    Text(reader, 5),
                    Text(reader, 6),
                    Text(reader, 7),
                    Text(reader, 8),
                    Text(reader, 9))));
            }

            return new ChangeLog(true, null, events, ChangeHistory.ChangeLogSource);
        }
        catch (SqlException ex)
        {
            return new ChangeLog(false, $"The change log can't be read: {ex.Message}", [], ChangeHistory.ChangeLogSource);
        }
    }

    /// <summary>
    /// The objects a logged event changed. An index or statistics change alters its table; a rename also changes the object under its new name.
    /// Events without an object (such as ALTER DATABASE) and changes to the change log itself are left out.
    /// </summary>
    public static IEnumerable<ChangeEvent> ToEvents(ChangeLogRow row)
    {
        if (row.ObjectName is null || Is(row.SchemaName, SchemaName))
        {
            yield break;
        }

        var onTarget = row.TargetObjectName is not null && row.ObjectType is "INDEX" or "STATISTICS";
        var kind = onTarget ? ChangeKind.Altered
            : row.EventType.StartsWith("CREATE_", StringComparison.Ordinal) ? ChangeKind.Created
            : row.EventType.StartsWith("DROP_", StringComparison.Ordinal) ? ChangeKind.Deleted
            : ChangeKind.Altered;
        var name = onTarget ? row.TargetObjectName! : row.ObjectName;
        yield return new ChangeEvent(row.EventTime, kind, row.SchemaName, name, row.LoginName, row.HostName, row.ApplicationName);
        if (row.EventType == "RENAME" && row.NewObjectName is { } newName && !onTarget && row.ObjectType is not ("COLUMN" or "INDEX" or "STATISTICS"))
        {
            yield return new ChangeEvent(row.EventTime, ChangeKind.Created, row.SchemaName, newName, row.LoginName, row.HostName, row.ApplicationName);
        }
    }

    private static void Run(string connectionString, IReadOnlyList<string> batches)
    {
        using var connection = new SqlConnection(connectionString);
        connection.Open();
        using var transaction = connection.BeginTransaction();
        foreach (var batch in batches)
        {
            using var command = new SqlCommand(batch, connection, transaction);
            command.ExecuteNonQuery();
        }

        transaction.Commit();
    }

    private static T Single<T>(SqlConnection connection, string sql, Func<SqlDataReader, T> map)
    {
        using var command = new SqlCommand(sql, connection);
        using var reader = command.ExecuteReader();
        reader.Read();
        return map(reader);
    }

    private static string? Text(SqlDataReader reader, int ordinal) => reader.IsDBNull(ordinal) ? null : reader.GetString(ordinal);

    private static bool Is(string? value, string name) => string.Equals(value, name, StringComparison.OrdinalIgnoreCase);
}
