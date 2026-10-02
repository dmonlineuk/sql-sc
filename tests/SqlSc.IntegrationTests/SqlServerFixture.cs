using System.Text.RegularExpressions;
using Microsoft.Data.SqlClient;
using Microsoft.SqlServer.Dac;
using SqlSc.Core.Modeling;
using SqlSc.Core.WorkingFolders;
using Testcontainers.MsSql;

namespace SqlSc.IntegrationTests;

/// <summary>One SQL Server 2022 container per test run. Each test creates its own database.</summary>
public sealed class SqlServerFixture : IAsyncLifetime
{
    private readonly MsSqlContainer container = new MsSqlBuilder("mcr.microsoft.com/mssql/server:2022-latest").Build();

    public static string DemoFolder => Path.Combine(AppContext.BaseDirectory, "Fixtures", "Demo");

    public static string RichScript => Path.Combine(AppContext.BaseDirectory, "Fixtures", "Rich.sql");

    public Task InitializeAsync() => container.StartAsync();

    public Task DisposeAsync() => container.DisposeAsync().AsTask();

    public string ConnectionString(string database) =>
        new SqlConnectionStringBuilder(container.GetConnectionString()) { InitialCatalog = database }.ConnectionString;

    /// <summary>Creates a database from a working folder by publishing the folder model.</summary>
    public string CreateDatabaseFromFolder(string folderPath)
    {
        var database = "SqlSc_" + Guid.NewGuid().ToString("N")[..8];
        var dacpac = Path.Combine(Path.GetTempPath(), database + ".dacpac");
        using (var model = FolderModelLoader.Load(WorkingFolder.Open(folderPath)))
        {
            model.BuildPackage(dacpac, database);
        }

        using (var package = DacPackage.Load(dacpac))
        {
            new DacServices(ConnectionString("master")).Deploy(package, database, upgradeExisting: false);
        }

        File.Delete(dacpac);
        return database;
    }

    /// <summary>Creates a database and runs a script in it, one batch per GO.</summary>
    public string CreateDatabaseFromScript(string scriptPath)
    {
        var database = "SqlSc_" + Guid.NewGuid().ToString("N")[..8];
        Execute("master", $"CREATE DATABASE [{database}]");
        using var connection = new SqlConnection(ConnectionString(database));
        connection.Open();
        foreach (var batch in Regex.Split(File.ReadAllText(scriptPath), @"^\s*GO\s*$", RegexOptions.Multiline).Where(b => !string.IsNullOrWhiteSpace(b)))
        {
            using var command = connection.CreateCommand();
            command.CommandText = batch;
            command.ExecuteNonQuery();
        }

        return database;
    }

    /// <summary>
    /// Waits until the default trace shows an event for <paramref name="objectName"/>. SQL Server buffers default trace
    /// writes, so events can take a few seconds to become readable.
    /// </summary>
    public void WaitForDefaultTrace(string database, string objectName)
    {
        using var connection = new SqlConnection(ConnectionString(database));
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            DECLARE @path nvarchar(260) = (SELECT path FROM sys.traces WHERE is_default = 1);
            SELECT COUNT(*) FROM sys.fn_trace_gettable(@path, DEFAULT) WHERE DatabaseID = DB_ID() AND ObjectName = @name
            """;
        command.Parameters.AddWithValue("@name", objectName);
        var deadline = DateTime.UtcNow.AddSeconds(30);
        while ((int)command.ExecuteScalar()! == 0)
        {
            Assert.True(DateTime.UtcNow < deadline, $"No default trace event for {objectName} after 30 seconds.");
            Thread.Sleep(250);
        }
    }

    public void Execute(string database, string sql)
    {
        using var connection = new SqlConnection(ConnectionString(database));
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }
}

[CollectionDefinition(Name)]
public sealed class SqlServerGroup : ICollectionFixture<SqlServerFixture>
{
    public const string Name = "SQL Server";
}
