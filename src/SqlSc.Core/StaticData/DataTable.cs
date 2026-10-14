using System.Globalization;
using Microsoft.Data.SqlClient;

namespace SqlSc.Core.StaticData;

/// <summary>
/// A table column as static data sees it. <see cref="Type"/> is the system type, with alias types resolved.
/// Computed and rowversion columns aren't <see cref="Insertable"/>, so static data ignores them.
/// </summary>
internal sealed record DataColumn(string Name, string Type, int Precision, int Scale, bool IsIdentity, bool Insertable)
{
    public string Quoted => DataTable.Quote(Name);

    /// <summary>
    /// An expression for this column of <paramref name="alias"/> that compares exactly: strings as their bytes (so case and
    /// trailing spaces count), and types EXCEPT can't compare (xml, text, spatial) converted to comparable ones.
    /// </summary>
    public string Comparable(string alias) => Type switch
    {
        "char" or "varchar" or "nchar" or "nvarchar" or "text" or "ntext" or "image" or "geography" or "geometry"
            => $"CAST({alias}.{Quoted} AS varbinary(max))",
        "xml" => $"CAST({alias}.{Quoted} AS nvarchar(max))",
        _ => $"{alias}.{Quoted}",
    };

    /// <summary>An expression showing the value of this key column of <paramref name="alias"/> as text.</summary>
    public string Display(string alias) => Type switch
    {
        "date" or "datetime" or "datetime2" or "smalldatetime" or "datetimeoffset" or "time" => $"CONVERT(nvarchar(max), {alias}.{Quoted}, 126)",
        "binary" or "varbinary" => $"CONVERT(nvarchar(max), {alias}.{Quoted}, 1)",
        _ => $"CONVERT(nvarchar(max), {alias}.{Quoted})",
    };

    /// <summary>The column as export selects it: SqlClient can't read CLR types, so they are read as their bytes.</summary>
    public string Select => Type is "geography" or "geometry" or "hierarchyid" ? $"CAST({Quoted} AS varbinary(max))" : Quoted;
}

/// <summary>A table tracked for static data, read from the database's catalog.</summary>
internal sealed record DataTable(string Schema, string Name, IReadOnlyList<DataColumn> Columns, IReadOnlyList<DataColumn> Key, long Rows)
{
    public string Quoted => $"{Quote(Schema)}.{Quote(Name)}";

    public IReadOnlyList<DataColumn> Insertable => [.. Columns.Where(c => c.Insertable)];

    public bool HasIdentity => Columns.Any(c => c.IsIdentity && c.Insertable);

    public static string Quote(string name) => "[" + name.Replace("]", "]]", StringComparison.Ordinal) + "]";

    /// <summary>The table's columns, primary key and row count, or null if the database has no such table.</summary>
    public static DataTable? Read(SqlConnection connection, string schema, string name, SqlTransaction? transaction = null)
    {
        using var command = new SqlCommand(
            """
            DECLARE @id int = (SELECT t.object_id FROM sys.tables t WHERE t.name = @name AND SCHEMA_NAME(t.schema_id) = @schema);
            SELECT SCHEMA_NAME(t.schema_id), t.name, (SELECT SUM(p.rows) FROM sys.partitions p WHERE p.object_id = t.object_id AND p.index_id IN (0, 1))
            FROM sys.tables t WHERE t.object_id = @id;
            SELECT c.name, ISNULL(TYPE_NAME(c.system_type_id), TYPE_NAME(c.user_type_id)), c.precision, c.scale, c.is_identity,
                CAST(CASE WHEN c.is_computed = 1 OR c.system_type_id = 189 OR c.graph_type IS NOT NULL THEN 0 ELSE 1 END AS bit),
                (SELECT ic.key_ordinal FROM sys.indexes i JOIN sys.index_columns ic ON ic.object_id = i.object_id AND ic.index_id = i.index_id
                 WHERE i.object_id = c.object_id AND i.is_primary_key = 1 AND ic.column_id = c.column_id)
            FROM sys.columns c WHERE c.object_id = @id ORDER BY c.column_id;
            """,
            connection,
            transaction);
        command.Parameters.AddWithValue("@schema", schema);
        command.Parameters.AddWithValue("@name", name);
        using var reader = command.ExecuteReader();
        if (!reader.Read())
        {
            return null;
        }

        var (actualSchema, actualName, rows) = (reader.GetString(0), reader.GetString(1), reader.IsDBNull(2) ? 0L : Convert.ToInt64(reader.GetValue(2), CultureInfo.InvariantCulture));
        reader.NextResult();
        var columns = new List<(DataColumn Column, int? KeyOrdinal)>();
        while (reader.Read())
        {
            columns.Add((
                new DataColumn(reader.GetString(0), reader.GetString(1), reader.GetByte(2), reader.GetByte(3), reader.GetBoolean(4), reader.GetBoolean(5)),
                reader.IsDBNull(6) ? null : Convert.ToInt32(reader.GetValue(6), CultureInfo.InvariantCulture)));
        }

        return new DataTable(
            actualSchema,
            actualName,
            [.. columns.Select(c => c.Column)],
            [.. columns.Where(c => c.KeyOrdinal is not null).OrderBy(c => c.KeyOrdinal).Select(c => c.Column)],
            rows);
    }
}
