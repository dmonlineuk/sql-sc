using Microsoft.Data.SqlClient;
using Microsoft.SqlServer.Dac.Model;
using SqlSc.Core.Comparison;
using SqlSc.Core.Export;

namespace SqlSc.Core.ChangeTracking;

/// <summary>A soft lock on an object: <see cref="LockedBy"/> is working on it. <see cref="Mine"/> is whether that is the current login.</summary>
public sealed record ObjectLock(string Name, string LockedBy, DateTime LockedAt, string? Reason, bool Mine);

/// <summary>The locks taken or released, and why others weren't.</summary>
public sealed record LockResult(IReadOnlyList<ObjectLock> Locks, IReadOnlyList<string> Problems);

/// <summary>
/// Soft locks kept in the database's <c>[sqlsc].[Lock]</c> table, so everyone on a shared database sees who is working on which object.
/// A lock doesn't stop anyone changing the object (SSMS ignores it); sql-sc skips or refuses to export or apply objects locked by
/// another login unless forced. The table is created the first time anyone locks an object, which needs CREATE TABLE.
/// </summary>
public static class ObjectLocks
{
    public const string TableName = "Lock";

    private const string Table = $"[{ChangeLogTable.SchemaName}].[{TableName}]";

    private const string Columns = "[Name], [LockedBy], [LockedAt], [Reason], CAST(CASE WHEN [LockedBy] = ORIGINAL_LOGIN() THEN 1 ELSE 0 END AS bit)";

    public static IReadOnlyList<string> CreateBatches { get; } =
    [
        $"IF SCHEMA_ID(N'{ChangeLogTable.SchemaName}') IS NULL EXEC(N'CREATE SCHEMA [{ChangeLogTable.SchemaName}] AUTHORIZATION [dbo]');",
        $"""
        IF OBJECT_ID(N'{Table}', N'U') IS NULL
        CREATE TABLE {Table} (
            [Name]     nvarchar (300) NOT NULL,
            [LockedBy] nvarchar (256) NOT NULL,
            [LockedAt] datetime2 (0)  NOT NULL,
            [Reason]   nvarchar (400) NULL,
            CONSTRAINT [PK_sqlsc_Lock] PRIMARY KEY CLUSTERED ([Name])
        );
        """,
        $"GRANT SELECT, INSERT, UPDATE, DELETE ON {Table} TO [public];",
    ];

    /// <summary>Whether <paramref name="obj"/> is the lock table or part of it; sql-sc never tracks it.</summary>
    public static bool Owns(TSqlObject obj) => obj.Name.Parts is [var schema, var name, ..]
        && Is(schema, ChangeLogTable.SchemaName) && (Is(name, TableName) || Is(name, "PK_sqlsc_Lock"));

    /// <summary>Every lock, or none if nobody has locked anything yet or the table can't be read.</summary>
    public static IReadOnlyList<ObjectLock> Read(string connectionString)
    {
        try
        {
            using var connection = new SqlConnection(connectionString);
            connection.Open();
            return Exists(connection) ? Query(connection, null, $"SELECT {Columns} FROM {Table} ORDER BY [Name]") : [];
        }
        catch (SqlException)
        {
            return [];
        }
    }

    /// <summary>
    /// Locks objects for the current login, creating the lock table if needed. Locking an object again updates its reason.
    /// An object locked by another login is left alone and reported as a problem. Objects don't have to exist yet.
    /// </summary>
    public static LockResult Lock(string connectionString, IReadOnlyList<string> names, string? reason)
    {
        using var connection = new SqlConnection(connectionString);
        connection.Open();
        if (!Exists(connection))
        {
            try
            {
                using var transaction = connection.BeginTransaction();
                foreach (var batch in CreateBatches)
                {
                    using var command = new SqlCommand(batch, connection, transaction);
                    command.ExecuteNonQuery();
                }

                transaction.Commit();
            }
            catch (SqlException ex)
            {
                throw new InvalidOperationException(
                    $"Nobody has locked an object in this database yet, and the lock table {Table} can't be created: {ex.Message} "
                        + "A db_owner can create it with the script from `sql-sc locks --script-only`.",
                    ex);
            }
        }

        var locks = new List<ObjectLock>();
        var problems = new List<string>();
        foreach (var (name, formatted) in Names(names, problems))
        {
            var current = Query(connection, formatted, $"""
                UPDATE {Table} SET [LockedAt] = SYSDATETIME(), [Reason] = @reason WHERE [Name] = @name AND [LockedBy] = ORIGINAL_LOGIN();
                IF @@ROWCOUNT = 0
                    INSERT INTO {Table} ([Name], [LockedBy], [LockedAt], [Reason])
                    SELECT @name, ORIGINAL_LOGIN(), SYSDATETIME(), @reason
                    WHERE NOT EXISTS (SELECT 1 FROM {Table} WITH (UPDLOCK, HOLDLOCK) WHERE [Name] = @name);
                SELECT {Columns} FROM {Table} WHERE [Name] = @name;
                """, reason).Single();
            if (current.Mine)
            {
                locks.Add(current);
            }
            else
            {
                problems.Add($"{name} is already locked by {Describe(current)}.");
            }
        }

        return new LockResult(locks, problems);
    }

    /// <summary>Releases the current login's locks on objects, or anyone's with <paramref name="force"/>.</summary>
    public static LockResult Unlock(string connectionString, IReadOnlyList<string> names, bool force)
    {
        using var connection = new SqlConnection(connectionString);
        connection.Open();
        var locks = new List<ObjectLock>();
        var problems = new List<string>();
        var exists = Exists(connection);
        foreach (var (name, formatted) in Names(names, problems))
        {
            var removed = exists
                ? Query(connection, formatted, $"DELETE FROM {Table} OUTPUT {Columns.Replace("[", "deleted.[", StringComparison.Ordinal)} WHERE [Name] = @name AND ([LockedBy] = ORIGINAL_LOGIN() OR @force = 1)", force: force)
                : [];
            if (removed.Count > 0)
            {
                locks.AddRange(removed);
            }
            else if (exists && Query(connection, formatted, $"SELECT {Columns} FROM {Table} WHERE [Name] = @name") is [var other])
            {
                problems.Add($"{name} is locked by {Describe(other)}; pass --force to unlock it anyway.");
            }
            else
            {
                problems.Add($"{name} isn't locked.");
            }
        }

        return new LockResult(locks, problems);
    }

    public static string Describe(ObjectLock objectLock) =>
        string.Create(System.Globalization.CultureInfo.InvariantCulture, $"{objectLock.LockedBy} since {objectLock.LockedAt:yyyy-MM-dd HH:mm}")
        + (string.IsNullOrWhiteSpace(objectLock.Reason) ? "" : $" ({objectLock.Reason})");

    /// <summary>Each change with the lock on it, if any.</summary>
    internal static IReadOnlyList<ObjectChange> Attach(IReadOnlyList<ObjectChange> changes, IReadOnlyList<ObjectLock> locks)
    {
        if (locks.Count == 0)
        {
            return changes;
        }

        var byName = locks.ToDictionary(l => l.Name, StringComparer.OrdinalIgnoreCase);
        return changes.Select(c => byName.TryGetValue(c.Name, out var l) ? c with { Lock = l } : c).ToList();
    }

    /// <summary>
    /// Leaves out objects locked by another login: named ones are problems, others picked by <c>--all</c> or <c>--mine</c> are skipped.
    /// </summary>
    /// <param name="verb">What the command does to the object, e.g. "export".</param>
    internal static List<ObjectChange> Filter(IEnumerable<ObjectChange> selected, string verb, IReadOnlySet<string> named, bool force, List<string> problems, List<string> skipped)
    {
        var kept = new List<ObjectChange>();
        foreach (var change in selected)
        {
            if (force || change.Lock is not { Mine: false } other)
            {
                kept.Add(change);
                continue;
            }

            var reason = $"{change.Name} is locked by {Describe(other)}; {verb} it with --force to go ahead anyway.";
            (named.Contains(change.Name) ? problems : skipped).Add(reason);
        }

        return kept;
    }

    private static IEnumerable<(string Name, string Formatted)> Names(IReadOnlyList<string> names, List<string> problems)
    {
        foreach (var name in names.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (ExportService.NameParts(name) is { Count: 1 or 2 })
            {
                yield return (name, ExportService.FormatName(name));
            }
            else
            {
                problems.Add($"{name} isn't an object name such as Sales.Customer.");
            }
        }
    }

    private static bool Exists(SqlConnection connection)
    {
        using var command = new SqlCommand($"SELECT OBJECT_ID(N'{Table}', N'U')", connection);
        return command.ExecuteScalar() is int;
    }

    private static List<ObjectLock> Query(SqlConnection connection, string? name, string sql, string? reason = null, bool force = false)
    {
        using var command = new SqlCommand(sql, connection);
        command.Parameters.AddWithValue("@name", (object?)name ?? DBNull.Value);
        command.Parameters.AddWithValue("@reason", string.IsNullOrWhiteSpace(reason) ? DBNull.Value : reason.Trim());
        command.Parameters.AddWithValue("@force", force);
        using var reader = command.ExecuteReader();
        var locks = new List<ObjectLock>();
        do
        {
            while (reader.FieldCount == 5 && reader.Read())
            {
                locks.Add(new ObjectLock(reader.GetString(0), reader.GetString(1), reader.GetDateTime(2), reader.IsDBNull(3) ? null : reader.GetString(3), reader.GetBoolean(4)));
            }
        }
        while (reader.NextResult());
        return locks;
    }

    private static bool Is(string? value, string name) => string.Equals(value, name, StringComparison.OrdinalIgnoreCase);
}
