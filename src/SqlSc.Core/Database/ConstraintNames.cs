using Microsoft.Data.SqlClient;

namespace SqlSc.Core.Database;

public static class ConstraintNames
{
    /// <summary>
    /// Constraint names that look system-generated (<c>PK__T__3213E83F9AEF8E7A</c>) but were given explicitly, e.g. by a
    /// deployment tool copying them from another database. DacFx keeps these names, so the folder's copies must keep them too.
    /// </summary>
    public static HashSet<string> ExplicitWithGeneratedStyle(string connectionString)
    {
        using var connection = new SqlConnection(connectionString);
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT name FROM sys.key_constraints WHERE is_system_named = 0 AND name LIKE '%[_][_]%'
            UNION SELECT name FROM sys.default_constraints WHERE is_system_named = 0 AND name LIKE '%[_][_]%'
            UNION SELECT name FROM sys.check_constraints WHERE is_system_named = 0 AND name LIKE '%[_][_]%'
            UNION SELECT name FROM sys.foreign_keys WHERE is_system_named = 0 AND name LIKE '%[_][_]%'
            """;
        using var reader = command.ExecuteReader();
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        while (reader.Read())
        {
            names.Add(reader.GetString(0));
        }

        return names;
    }
}
