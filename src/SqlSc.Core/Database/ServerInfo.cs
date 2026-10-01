using Microsoft.Data.SqlClient;
using Microsoft.SqlServer.Dac.Model;
using SqlSc.Core.Modeling;

namespace SqlSc.Core.Database;

public sealed record ServerInfo(string ServerName, string DatabaseName, int EngineEdition, int MajorVersion, string ProductVersion)
{
    public SqlServerVersion Platform => TargetPlatform.FromServer(EngineEdition, MajorVersion);

    public static ServerInfo Query(string connectionString)
    {
        using var connection = new SqlConnection(connectionString);
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT CAST(SERVERPROPERTY('ServerName') AS nvarchar(256)),
                   DB_NAME(),
                   CAST(SERVERPROPERTY('EngineEdition') AS int),
                   CAST(SERVERPROPERTY('ProductVersion') AS nvarchar(128))
            """;
        using var reader = command.ExecuteReader();
        reader.Read();
        var productVersion = reader.GetString(3);
        var major = int.Parse(productVersion.AsSpan(0, productVersion.IndexOf('.', StringComparison.Ordinal)), System.Globalization.CultureInfo.InvariantCulture);
        return new ServerInfo(reader.GetString(0), reader.GetString(1), reader.GetInt32(2), major, productVersion);
    }
}
