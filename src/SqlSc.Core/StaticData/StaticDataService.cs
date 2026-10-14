using System.Data.SqlTypes;
using System.Globalization;
using System.Text;
using Microsoft.Data.SqlClient;
using SqlSc.Core.Comparison;
using SqlSc.Core.Export;
using SqlSc.Core.WorkingFolders;

namespace SqlSc.Core.StaticData;

/// <summary>A table tracked for static data, and its data script relative to the working folder.</summary>
public sealed record TrackedTable(string Schema, string Name, string File)
{
    public string Quoted => DataTable.Quote(Schema) + "." + DataTable.Quote(Name);
}

/// <summary>What linking or unlinking tables did: the tables changed, warnings, and why some couldn't be.</summary>
public sealed record DataLinkResult(IReadOnlyList<TrackedTable> Tables, IReadOnlyList<string> Messages, IReadOnlyList<string> Problems);

/// <summary>Rows changed in a static data table. On a script-only apply these are the rows the script would change.</summary>
public sealed record AppliedData(string Table, string File, int Inserted, int Updated, int Deleted);

internal enum RowAction
{
    Insert,
    Update,
    Delete,
}

/// <summary>One batch of a static data deployment, and which table's rows it changes, if any.</summary>
internal sealed record DataStep(string Sql, string? Table = null, RowAction? Action = null);

/// <summary>
/// Static data: the rows of tracked tables, kept in the working folder as one <c>INSERT</c> per row. DacFx doesn't compare data,
/// so the folder's rows are loaded into a <c>#temp</c> copy of each table and compared with the live table on its primary key.
/// Only temporary tables are written to compare, so no extra permissions are needed.
/// </summary>
public static class StaticDataService
{
    /// <summary>Tables with more rows than this get a warning when linked: static data is meant for small lookup tables.</summary>
    public const int LargeTableRows = 10_000;

    private const int StatementsPerBatch = 500;

    private const string Suffix = "_Data.sql";

    /// <summary>The tables the folder's <c>RedGateDatabaseInfo.xml</c> tracks, from their data file names (<c>Schema.Table_Data.sql</c>).</summary>
    public static IReadOnlyList<TrackedTable> Tracked(WorkingFolder folder) =>
        (folder.RedgateInfo?.DataFiles ?? [])
            .Select(file => TableOf(file) is var (schema, name)
                ? new TrackedTable(schema, name, Path.Combine(folder.DataFolder, file))
                : throw new InvalidDataException($"{RedgateDatabaseInfo.FileName} lists data file '{file}', which isn't named Schema.Table{Suffix}."))
            .ToList();

    /// <summary>Starts tracking the data of <paramref name="names"/>. Each must exist in the database and have a primary key.</summary>
    public static DataLinkResult Link(WorkingFolder folder, string connectionString, IReadOnlyList<string> names)
    {
        var tracked = Tracked(folder);
        var linked = new List<TrackedTable>();
        var messages = new List<string>();
        var problems = new List<string>();
        using var connection = new SqlConnection(connectionString);
        connection.Open();
        foreach (var name in names)
        {
            if (Parse(name, DefaultSchema(folder)) is not var (schema, table))
            {
                problems.Add($"{name} isn't a table name.");
                continue;
            }

            var info = DataTable.Read(connection, schema, table);
            if (info is null)
            {
                problems.Add($"{name}: the database has no such table.");
            }
            else if (info.Key.Count == 0)
            {
                problems.Add($"{info.Quoted} has no primary key, which static data needs to match rows.");
            }
            else if (tracked.Concat(linked).Any(t => Same(t, info.Schema, info.Name)))
            {
                messages.Add($"{info.Quoted} is already tracked.");
            }
            else
            {
                if (info.Rows > LargeTableRows)
                {
                    messages.Add(Invariant($"{info.Quoted} has {info.Rows:N0} rows. Static data is meant for small lookup tables, and every row goes into one script."));
                }

                linked.Add(new TrackedTable(info.Schema, info.Name, Path.Combine(folder.DataFolder, FileName(info.Schema, info.Name))));
            }
        }

        if (linked.Count > 0)
        {
            RedgateDatabaseInfo.SetDataFiles(
                Path.Combine(folder.RootPath, RedgateDatabaseInfo.FileName),
                (folder.RedgateInfo?.DataFiles ?? []).Concat(linked.Select(t => FileName(t.Schema, t.Name))));
        }

        return new DataLinkResult(linked, messages, problems);
    }

    /// <summary>Stops tracking the data of <paramref name="names"/> and deletes their data scripts.</summary>
    public static DataLinkResult Unlink(WorkingFolder folder, IReadOnlyList<string> names)
    {
        var tracked = Tracked(folder);
        var unlinked = new List<TrackedTable>();
        var messages = new List<string>();
        var problems = new List<string>();
        foreach (var name in names)
        {
            var match = Parse(name, DefaultSchema(folder)) is var (schema, table) ? tracked.FirstOrDefault(t => Same(t, schema, table)) : null;
            if (match is null)
            {
                problems.Add($"{name} isn't tracked for static data.");
            }
            else if (!unlinked.Contains(match))
            {
                unlinked.Add(match);
            }
        }

        if (unlinked.Count > 0)
        {
            RedgateDatabaseInfo.SetDataFiles(
                Path.Combine(folder.RootPath, RedgateDatabaseInfo.FileName),
                tracked.Except(unlinked).Select(t => Path.GetFileName(t.File)));
            foreach (var table in unlinked.Where(t => File.Exists(Path.Combine(folder.RootPath, t.File))))
            {
                File.Delete(Path.Combine(folder.RootPath, table.File));
                messages.Add($"Deleted {table.File}.");
            }
        }

        return new DataLinkResult(unlinked, messages, problems);
    }

    /// <summary>How each tracked table's rows differ between the database and the folder.</summary>
    internal static IReadOnlyList<DataChange> Compare(WorkingFolder folder, string connectionString)
    {
        var tracked = Tracked(folder);
        if (tracked.Count == 0)
        {
            return [];
        }

        using var connection = new SqlConnection(connectionString);
        connection.Open();
        var changes = new List<DataChange>();
        foreach (var table in tracked)
        {
            var display = table.Quoted;
            try
            {
                var info = Describe(connection, null, table, out var problem);
                if (info is null)
                {
                    changes.Add(new DataChange(display, table.File, 0, 0, 0, [], problem));
                    continue;
                }

                const string temp = "#sqlsc_data";
                foreach (var step in Load(folder, table, info, temp))
                {
                    Execute(connection, null, step.Sql);
                }

                var rows = new List<RowChange>();
                var counts = new Dictionary<ObjectStatus, int>();
                foreach (var (status, sql) in Differences(info, temp))
                {
                    using var command = new SqlCommand(sql, connection) { CommandTimeout = 0 };
                    using var reader = command.ExecuteReader();
                    while (reader.Read())
                    {
                        counts[status] = reader.GetInt32(2);
                        var columns = reader.IsDBNull(1) ? [] : reader.GetString(1).Split('\u001f', StringSplitOptions.RemoveEmptyEntries);
                        rows.Add(new RowChange(status, reader.GetString(0), columns));
                    }
                }

                Execute(connection, null, $"DROP TABLE {temp};");
                changes.Add(new DataChange(
                    info.Quoted,
                    table.File,
                    counts.GetValueOrDefault(ObjectStatus.New),
                    counts.GetValueOrDefault(ObjectStatus.Modified),
                    counts.GetValueOrDefault(ObjectStatus.Deleted),
                    rows));
            }
            catch (Exception ex) when (ex is SqlException or InvalidDataException)
            {
                Execute(connection, null, "IF OBJECT_ID('tempdb..#sqlsc_data') IS NOT NULL DROP TABLE #sqlsc_data;");
                changes.Add(new DataChange(display, table.File, 0, 0, 0, [], $"{table.File}: {ex.Message}"));
            }
        }

        return changes;
    }

    /// <summary>The data script export writes for a tracked table: the database's rows in primary key order.</summary>
    internal static string Script(SqlConnection connection, WorkingFolder folder, TrackedTable table)
    {
        var info = Describe(connection, null, table, out var problem) ?? throw new InvalidDataException(problem);
        var columns = info.Insertable;
        if (columns.FirstOrDefault(c => c.Type == "sql_variant") is { } variant)
        {
            throw new InvalidDataException($"{info.Quoted}.{variant.Quoted}: sql-sc can't write sql_variant values into a static data script.");
        }

        using var command = new SqlCommand(
            $"SELECT {string.Join(", ", columns.Select(c => c.Select))} FROM {info.Quoted} ORDER BY {string.Join(", ", info.Key.Select(c => c.Quoted))};",
            connection)
        {
            CommandTimeout = 0,
        };
        using var reader = command.ExecuteReader();
        return DataScript.Write(info, Rows(reader, columns));
    }

    /// <summary>
    /// The batches that make the database's rows of <paramref name="tables"/> match the folder: deletes (only for tables with
    /// <c>DeleteRows</c>) in reverse foreign key order, then updates and inserts with referenced tables first.
    /// </summary>
    internal static IReadOnlyList<DataStep> Plan(SqlConnection connection, SqlTransaction? transaction, WorkingFolder folder, IReadOnlyList<(TrackedTable Table, bool DeleteRows)> tables)
    {
        var described = new List<(DataTable Info, TrackedTable Table, bool DeleteRows)>();
        foreach (var (table, deleteRows) in tables)
        {
            var info = Describe(connection, transaction, table, out var problem) ?? throw new InvalidDataException(problem);
            described.Add((info, table, deleteRows));
        }

        var ordered = ForeignKeyOrder(connection, transaction, described.Select(d => d.Info).ToList())
            .Select(info => described.Single(d => d.Info == info))
            .ToList();
        var steps = new List<DataStep>();
        for (var i = 0; i < ordered.Count; i++)
        {
            steps.AddRange(Load(folder, ordered[i].Table, ordered[i].Info, Temp(i)));
        }

        for (var i = ordered.Count - 1; i >= 0; i--)
        {
            var info = ordered[i].Info;
            if (ordered[i].DeleteRows)
            {
                steps.Add(new DataStep(
                    $"DELETE d FROM {info.Quoted} d WHERE NOT EXISTS (SELECT 1 FROM {Temp(i)} f WHERE {Join(info)});",
                    info.Quoted,
                    RowAction.Delete));
            }
        }

        for (var i = 0; i < ordered.Count; i++)
        {
            var info = ordered[i].Info;
            var keyNames = info.Key.Select(k => k.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var updatable = info.Insertable.Where(c => !c.IsIdentity && !keyNames.Contains(c.Name)).ToList();
            if (updatable.Count > 0)
            {
                steps.Add(new DataStep(
                    $"UPDATE d SET {string.Join(", ", updatable.Select(c => $"{c.Quoted} = f.{c.Quoted}"))} FROM {info.Quoted} d JOIN {Temp(i)} f ON {Join(info)} WHERE {Differs(info)};",
                    info.Quoted,
                    RowAction.Update));
            }

            var columns = string.Join(", ", info.Insertable.Select(c => c.Quoted));
            var insert = $"INSERT INTO {info.Quoted} ({columns}) SELECT {string.Join(", ", info.Insertable.Select(c => "f." + c.Quoted))} FROM {Temp(i)} f WHERE NOT EXISTS (SELECT 1 FROM {info.Quoted} d WHERE {Join(info)});";
            steps.Add(new DataStep(
                info.HasIdentity ? $"SET IDENTITY_INSERT {info.Quoted} ON;\r\n{insert}\r\nSET IDENTITY_INSERT {info.Quoted} OFF;" : insert,
                info.Quoted,
                RowAction.Insert));
        }

        for (var i = 0; i < ordered.Count; i++)
        {
            steps.Add(new DataStep($"DROP TABLE {Temp(i)};"));
        }

        return steps;
    }

    /// <summary>A script of <paramref name="steps"/> as one transaction, for <c>--script-only</c>.</summary>
    internal static string ScriptOf(IReadOnlyList<DataStep> steps) =>
        "SET XACT_ABORT ON;\r\nBEGIN TRANSACTION;\r\nGO\r\n"
        + string.Concat(steps.Select(s => s.Sql + "\r\nGO\r\n"))
        + "COMMIT TRANSACTION;\r\nGO\r\n";

    /// <summary>Runs <paramref name="steps"/> in one transaction, which is rolled back if any fails. Returns the rows changed in each table.</summary>
    internal static IReadOnlyList<AppliedData> Run(string connectionString, WorkingFolder folder, IReadOnlyList<(TrackedTable Table, bool DeleteRows)> tables)
    {
        using var connection = new SqlConnection(connectionString);
        connection.Open();
        using var transaction = connection.BeginTransaction();
        var steps = Plan(connection, transaction, folder, tables);
        var counts = new Dictionary<(string Table, RowAction Action), int>();
        foreach (var step in steps)
        {
            var affected = Execute(connection, transaction, step.Sql);
            if (step is { Table: { } table, Action: { } action })
            {
                counts[(table, action)] = counts.GetValueOrDefault((table, action)) + Math.Max(affected, 0);
            }
        }

        transaction.Commit();
        return tables
            .Select(t => new AppliedData(
                t.Table.Quoted,
                t.Table.File,
                counts.Where(c => c.Key.Action == RowAction.Insert && string.Equals(c.Key.Table, t.Table.Quoted, StringComparison.OrdinalIgnoreCase)).Sum(c => c.Value),
                counts.Where(c => c.Key.Action == RowAction.Update && string.Equals(c.Key.Table, t.Table.Quoted, StringComparison.OrdinalIgnoreCase)).Sum(c => c.Value),
                counts.Where(c => c.Key.Action == RowAction.Delete && string.Equals(c.Key.Table, t.Table.Quoted, StringComparison.OrdinalIgnoreCase)).Sum(c => c.Value)))
            .ToList();
    }

    internal static string FileName(string schema, string table) => $"{schema}.{table}{Suffix}";

    /// <summary>The schema and table a data file is for: <c>Sales.Customer_Data.sql</c> is <c>Sales</c>, <c>Customer</c>.</summary>
    internal static (string Schema, string Table)? TableOf(string file)
    {
        var name = Path.GetFileName(file);
        var dot = name.IndexOf('.', StringComparison.Ordinal);
        return name.EndsWith(Suffix, StringComparison.OrdinalIgnoreCase) && dot > 0 && dot < name.Length - Suffix.Length - 1
            ? (name[..dot], name[(dot + 1)..^Suffix.Length])
            : null;
    }

    internal static (string Schema, string Table)? Parse(string name, string defaultSchema) =>
        ExportService.NameParts(name) switch
        {
            [var table] => (defaultSchema, table),
            [var schema, var table] => (schema, table),
            _ => null,
        };

    internal static string DefaultSchema(WorkingFolder folder) => folder.RedgateInfo?.DefaultSchema ?? "dbo";

    /// <summary>Batches that create <paramref name="temp"/> with the table's columns, but no identity or constraints, and load the folder's rows into it.</summary>
    internal static IEnumerable<DataStep> Load(WorkingFolder folder, TrackedTable table, DataTable info, string temp)
    {
        var columns = string.Join(", ", info.Insertable.Select(c => c.Quoted));
        // A UNION stops SELECT INTO copying the identity property, so the folder's identity values load as they are.
        yield return new DataStep($"SELECT TOP (0) {columns} INTO {temp} FROM {info.Quoted} UNION ALL SELECT TOP (0) {columns} FROM {info.Quoted};");
        var path = Path.Combine(folder.RootPath, table.File);
        if (!File.Exists(path))
        {
            yield break;
        }

        var statements = DataScript.Retarget(File.ReadAllText(path), info.Schema, info.Name, temp);
        foreach (var chunk in statements.Chunk(StatementsPerBatch))
        {
            yield return new DataStep(string.Join("\r\n", chunk));
        }
    }

    /// <summary>Queries for each status of row that differs: key text, differing columns (separated by U+001F) and the total count.</summary>
    private static IEnumerable<(ObjectStatus Status, string Sql)> Differences(DataTable info, string temp)
    {
        var key = "CONCAT(N''" + string.Concat(info.Key.Select((k, i) => (i == 0 ? ", " : ", N', ', ") + k.Display("d"))) + ")";
        var folderKey = key.Replace("d.[", "f.[", StringComparison.Ordinal);
        var orderD = string.Join(", ", info.Key.Select(k => "d." + k.Quoted));
        var orderF = string.Join(", ", info.Key.Select(k => "f." + k.Quoted));
        var top = DataChange.MaxRows.ToString(CultureInfo.InvariantCulture);
        yield return (ObjectStatus.New,
            $"SELECT TOP ({top}) {key}, NULL, COUNT(*) OVER () FROM {info.Quoted} d WHERE NOT EXISTS (SELECT 1 FROM {temp} f WHERE {Join(info)}) ORDER BY {orderD};");
        var compared = NonKey(info);
        if (compared.Count > 0)
        {
            var changed = "CONCAT(N''" + string.Concat(compared.Select(c =>
                $", CASE WHEN EXISTS (SELECT {c.Comparable("d")} EXCEPT SELECT {c.Comparable("f")}) THEN N'{c.Name.Replace("'", "''", StringComparison.Ordinal)}' + NCHAR(31) END")) + ")";
            yield return (ObjectStatus.Modified,
                $"SELECT TOP ({top}) {key}, {changed}, COUNT(*) OVER () FROM {info.Quoted} d JOIN {temp} f ON {Join(info)} WHERE {Differs(info)} ORDER BY {orderD};");
        }

        yield return (ObjectStatus.Deleted,
            $"SELECT TOP ({top}) {folderKey}, NULL, COUNT(*) OVER () FROM {temp} f WHERE NOT EXISTS (SELECT 1 FROM {info.Quoted} d WHERE {Join(info)}) ORDER BY {orderF};");
    }

    private static List<DataColumn> NonKey(DataTable info)
    {
        var keyNames = info.Key.Select(k => k.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        return info.Insertable.Where(c => !keyNames.Contains(c.Name)).ToList();
    }

    private static string Join(DataTable info) => string.Join(" AND ", info.Key.Select(k => $"d.{k.Quoted} = f.{k.Quoted}"));

    private static string Differs(DataTable info)
    {
        var compared = NonKey(info);
        return compared.Count == 0
            ? "1 = 0"
            : $"EXISTS (SELECT {string.Join(", ", compared.Select(c => c.Comparable("d")))} EXCEPT SELECT {string.Join(", ", compared.Select(c => c.Comparable("f")))})";
    }

    private static string Temp(int index) => "#sqlsc_data_" + index.ToString(CultureInfo.InvariantCulture);

    private static DataTable? Describe(SqlConnection connection, SqlTransaction? transaction, TrackedTable table, out string? problem)
    {
        var info = DataTable.Read(connection, table.Schema, table.Name, transaction);
        problem = info is null
            ? $"{table.Quoted} is tracked for static data but isn't in the database."
            : info.Key.Count == 0 ? $"{info.Quoted} is tracked for static data but has no primary key." : null;
        return problem is null ? info : null;
    }

    /// <summary>Tables in an order where each comes after the tables it references, as far as foreign keys between them allow.</summary>
    private static List<DataTable> ForeignKeyOrder(SqlConnection connection, SqlTransaction? transaction, List<DataTable> tables)
    {
        var references = new List<(string From, string To)>();
        using (var command = new SqlCommand(
            """
            SELECT QUOTENAME(OBJECT_SCHEMA_NAME(parent_object_id)) + '.' + QUOTENAME(OBJECT_NAME(parent_object_id)),
                QUOTENAME(OBJECT_SCHEMA_NAME(referenced_object_id)) + '.' + QUOTENAME(OBJECT_NAME(referenced_object_id))
            FROM sys.foreign_keys WHERE parent_object_id <> referenced_object_id;
            """,
            connection,
            transaction))
        using (var reader = command.ExecuteReader())
        {
            while (reader.Read())
            {
                references.Add((reader.GetString(0), reader.GetString(1)));
            }
        }

        var remaining = tables.OrderBy(t => t.Quoted, StringComparer.OrdinalIgnoreCase).ToList();
        var ordered = new List<DataTable>();
        while (remaining.Count > 0)
        {
            var next = remaining.FirstOrDefault(t => !remaining.Any(other => other != t && references.Contains((t.Quoted, other.Quoted)))) ?? remaining[0];
            ordered.Add(next);
            remaining.Remove(next);
        }

        return ordered;
    }

    private static IEnumerable<object?[]> Rows(SqlDataReader reader, IReadOnlyList<DataColumn> columns)
    {
        while (reader.Read())
        {
            var row = new object?[columns.Count];
            for (var i = 0; i < columns.Count; i++)
            {
                row[i] = reader.IsDBNull(i) ? null
                    : columns[i].Type is "decimal" or "numeric" ? reader.GetSqlDecimal(i)
                    : reader.GetValue(i);
            }

            yield return row;
        }
    }

    private static int Execute(SqlConnection connection, SqlTransaction? transaction, string sql)
    {
        using var command = new SqlCommand(sql, connection, transaction) { CommandTimeout = 0 };
        return command.ExecuteNonQuery();
    }

    private static bool Same(TrackedTable table, string schema, string name) =>
        string.Equals(table.Schema, schema, StringComparison.OrdinalIgnoreCase) && string.Equals(table.Name, name, StringComparison.OrdinalIgnoreCase);

    private static string Invariant(FormattableString value) => value.ToString(CultureInfo.InvariantCulture);
}
