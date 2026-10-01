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
