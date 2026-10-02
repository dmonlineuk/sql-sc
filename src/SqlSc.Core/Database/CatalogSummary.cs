using System.Diagnostics;
using Microsoft.Data.SqlClient;
using SqlSc.Core.Filtering;

namespace SqlSc.Core.Database;

/// <summary>A top-level database object as listed by the catalog views, typed with its DacFx object type name.</summary>
public sealed record CatalogObject(string ObjectType, string? Schema, string Name)
{
    public IList<string> NameParts => Schema is null ? [Name] : [Schema, Name];
}

public sealed record ChildCounts(int Columns, int Indexes, int Constraints, int Triggers, int Statistics, int Permissions, int ExtendedProperties, int RoleMembers);

/// <summary>
/// What a database contains, read cheaply from the catalog views, to explain how big a DacFx extract will be
/// and how much of it the working folder's filter keeps.
/// </summary>
public sealed record CatalogSummary(IReadOnlyList<CatalogObject> Objects, ChildCounts Children, long ModuleBytes, TimeSpan Elapsed)
{
    private const string ObjectsSql = """
        SELECT CASE o.type
                   WHEN 'U' THEN 'Table' WHEN 'V' THEN 'View' WHEN 'P' THEN 'Procedure' WHEN 'PC' THEN 'Procedure'
                   WHEN 'FN' THEN 'ScalarFunction' WHEN 'FS' THEN 'ScalarFunction'
                   WHEN 'IF' THEN 'TableValuedFunction' WHEN 'TF' THEN 'TableValuedFunction' WHEN 'FT' THEN 'TableValuedFunction'
                   WHEN 'AF' THEN 'Aggregate' WHEN 'SN' THEN 'Synonym' WHEN 'SO' THEN 'Sequence' WHEN 'ET' THEN 'ExternalTable'
               END, s.name, o.name
        FROM sys.objects AS o JOIN sys.schemas AS s ON s.schema_id = o.schema_id
        WHERE o.is_ms_shipped = 0 AND o.parent_object_id = 0
          AND o.type IN ('U', 'V', 'P', 'PC', 'FN', 'FS', 'IF', 'TF', 'FT', 'AF', 'SN', 'SO', 'ET')
        UNION ALL
        SELECT CASE WHEN t.is_table_type = 1 THEN 'TableType' ELSE 'UserDefinedDataType' END, s.name, t.name
        FROM sys.types AS t JOIN sys.schemas AS s ON s.schema_id = t.schema_id
        WHERE t.is_user_defined = 1
        UNION ALL
        SELECT 'Schema', NULL, name FROM sys.schemas WHERE schema_id BETWEEN 5 AND 16383
        UNION ALL
        SELECT CASE type WHEN 'R' THEN 'Role' WHEN 'A' THEN 'ApplicationRole' ELSE 'User' END, NULL, name
        FROM sys.database_principals
        WHERE principal_id > 4 AND is_fixed_role = 0 AND type IN ('S', 'U', 'G', 'E', 'X', 'C', 'K', 'R', 'A')
        """;

    private const string ChildrenSql = """
        SELECT (SELECT COUNT_BIG(*) FROM sys.columns AS c JOIN sys.objects AS o ON o.object_id = c.object_id
                WHERE o.is_ms_shipped = 0 AND o.type IN ('U', 'V', 'IF', 'TF', 'ET')),
               (SELECT COUNT_BIG(*) FROM sys.indexes AS i JOIN sys.objects AS o ON o.object_id = i.object_id
                WHERE o.is_ms_shipped = 0 AND i.type > 0),
               (SELECT COUNT_BIG(*) FROM sys.objects WHERE is_ms_shipped = 0 AND type IN ('PK', 'UQ', 'F', 'C', 'D')),
               (SELECT COUNT_BIG(*) FROM sys.triggers WHERE is_ms_shipped = 0),
               (SELECT COUNT_BIG(*) FROM sys.stats AS st JOIN sys.objects AS o ON o.object_id = st.object_id
                WHERE o.is_ms_shipped = 0 AND st.user_created = 1),
               (SELECT COUNT_BIG(*) FROM sys.database_permissions WHERE grantee_principal_id > 0),
               (SELECT COUNT_BIG(*) FROM sys.extended_properties),
               (SELECT COUNT_BIG(*) FROM sys.database_role_members),
               (SELECT ISNULL(SUM(CAST(DATALENGTH(m.definition) AS bigint)), 0) FROM sys.sql_modules AS m
                JOIN sys.objects AS o ON o.object_id = m.object_id WHERE o.is_ms_shipped = 0)
        """;

    public static CatalogSummary Query(string connectionString)
    {
        var stopwatch = Stopwatch.StartNew();
        using var connection = new SqlConnection(connectionString);
        connection.Open();
        var objects = new List<CatalogObject>();
        using (var command = new SqlCommand(ObjectsSql, connection))
        using (var reader = command.ExecuteReader())
        {
            while (reader.Read())
            {
                objects.Add(new CatalogObject(reader.GetString(0), reader.IsDBNull(1) ? null : reader.GetString(1), reader.GetString(2)));
            }
        }

        using (var command = new SqlCommand(ChildrenSql, connection))
        using (var reader = command.ExecuteReader())
        {
            reader.Read();
            int Count(int ordinal) => (int)reader.GetInt64(ordinal);
            var children = new ChildCounts(Count(0), Count(1), Count(2), Count(3), Count(4), Count(5), Count(6), Count(7));
            return new CatalogSummary(objects, children, reader.GetInt64(8), stopwatch.Elapsed);
        }
    }

    /// <summary>The objects the filter keeps. Children (columns, indexes...) follow their owner.</summary>
    public IReadOnlyList<CatalogObject> Tracked(ObjectFilter filter) =>
        Objects.Where(o => filter.Includes(o.ObjectType, o.NameParts)).ToList();

    public static IEnumerable<(string Key, int Count)> Largest(IEnumerable<CatalogObject> objects, Func<CatalogObject, string?> key, int top) =>
        objects.Select(key)
            .OfType<string>()
            .GroupBy(k => k, StringComparer.Ordinal)
            .Select(g => (g.Key, Count: g.Count()))
            .OrderByDescending(g => g.Count)
            .ThenBy(g => g.Key, StringComparer.Ordinal)
            .Take(top);
}
