using Microsoft.Data.SqlClient;
using SqlSc.Core.Apply;
using SqlSc.Core.ChangeTracking;
using SqlSc.Core.Comparison;
using SqlSc.Core.Diagnostics;
using SqlSc.Core.WorkingFolders;

namespace SqlSc.IntegrationTests;

[Collection(SqlServerGroup.Name)]
public class ChangeLogTests(SqlServerFixture sql)
{
    private static WorkingFolder Demo => WorkingFolder.Open(SqlServerFixture.DemoFolder);

    [Fact]
    public void RecordsDdlFromAnySessionAndStatusUsesIt()
    {
        var database = sql.CreateDatabaseFromFolder(SqlServerFixture.DemoFolder);
        ChangeLogTable.Install(sql.ConnectionString(database));
        ChangeLogTable.Install(sql.ConnectionString(database));

        Run(sql.ConnectionString(database), "SET QUOTED_IDENTIFIER OFF; SET ANSI_WARNINGS OFF; SET ARITHABORT OFF;", "ALTER PROCEDURE [Sales].[GetCustomer] @CustomerId int AS SELECT @CustomerId AS CustomerId");
        Run(sql.ConnectionString(database), "BEGIN TRANSACTION;", "DROP VIEW [Sales].[ActiveCustomer]", "COMMIT;");
        Run(sql.ConnectionString(database), "BEGIN TRANSACTION;", "CREATE TABLE [Sales].[RolledBack] ([Id] int NOT NULL)", "ROLLBACK;");
        sql.Execute(database, "CREATE INDEX [IX_Customer_Name] ON [Sales].[Customer] ([Name])");
        var developer = Developer(database);
        Run(developer, "CREATE PROCEDURE [Sales].[ByDeveloper] AS SELECT 1 AS One");

        var report = StatusService.GetStatus(Demo, sql.ConnectionString(database));

        Assert.True(report.ChangeLog.Available, report.ChangeLog.UnavailableReason);
        Assert.Equal(ChangeHistory.ChangeLogSource, report.ChangeLog.Source);
        Assert.True(report.DatabaseModel.FromCatalog, report.DatabaseModel.Describe());
        var changes = report.Changes.ToDictionary(c => c.Name);
        Assert.Equal((ChangeKind.Altered, "sa"), (changes["[Sales].[GetCustomer]"].LastChange?.Kind, changes["[Sales].[GetCustomer]"].LastChange?.LoginName));
        Assert.Equal(ChangeKind.Altered, changes["[Sales].[Customer]"].LastChange?.Kind);
        Assert.Equal(new SqlConnectionStringBuilder(developer).UserID, changes["[Sales].[ByDeveloper]"].LastChange?.LoginName);
        Assert.Equal(ChangeKind.Deleted, report.ChangeLog.Find("Sales", "ActiveCustomer")?.Kind);
        Assert.Null(report.ChangeLog.Find("Sales", "RolledBack"));
        Assert.DoesNotContain(report.Changes, c => c.Name.Contains("sqlsc", StringComparison.OrdinalIgnoreCase));

        var check = Doctor.Run(sql.ConnectionString(database), null, extract: false).Single(c => c.Name == "Change log");
        Assert.Equal(CheckResult.Ok, check.Result);
        Assert.StartsWith("Installed; ", check.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public void ApplyLeavesTheChangeLogAloneAndRemoveDropsIt()
    {
        var database = sql.CreateDatabaseFromFolder(SqlServerFixture.DemoFolder);
        var connection = sql.ConnectionString(database);
        ChangeLogTable.Install(connection);
        sql.Execute(database, "DROP VIEW [Sales].[ActiveCustomer]");

        var applied = ApplyService.Apply(Demo, connection, [], all: true, force: true);

        Assert.True(applied.Applied, string.Join(Environment.NewLine, applied.Problems));
        Assert.Equal([(ApplyAction.Create, "[Sales].[ActiveCustomer]")], applied.Objects.Select(o => (o.Action, o.Name)));
        Assert.True(ChangeLogTable.QueryState(connection).Installed);
        Assert.Contains(ChangeLogTable.Read(connection)!.Events, e => e is { Key: "Sales.ActiveCustomer", Kind: ChangeKind.Created, LoginName: "sa" });

        ChangeLogTable.Remove(connection);

        Assert.True(ChangeLogTable.QueryState(connection).Absent);
        Assert.Null(ChangeLogTable.Read(connection));
        sql.Execute(database, "CREATE TABLE [Sales].[AfterRemove] ([Id] int NOT NULL)");
        Assert.Equal(CheckResult.Ok, Doctor.Run(connection, null, extract: false).Single(c => c.Name == "Change log").Result);
    }

    [Fact]
    public void DdlStillWorksWhenTheTableIsMissing()
    {
        var database = sql.CreateDatabaseFromFolder(SqlServerFixture.DemoFolder);
        var connection = sql.ConnectionString(database);
        ChangeLogTable.Install(connection);
        sql.Execute(database, "DROP TABLE [sqlsc].[ChangeLog]");

        sql.Execute(database, "CREATE TABLE [Sales].[StillWorks] ([Id] int NOT NULL)");

        var state = ChangeLogTable.QueryState(connection);
        Assert.False(state.Installed);
        Assert.Equal(CheckResult.Warning, Doctor.Run(connection, null, extract: false).Single(c => c.Name == "Change log").Result);
    }

    /// <summary>A login that can create procedures in Sales but has no permissions on the change log.</summary>
    private string Developer(string database)
    {
        var login = "dev_" + Guid.NewGuid().ToString("N")[..8];
        const string password = "Dev-Passw0rd!";
        sql.Execute("master", $"CREATE LOGIN [{login}] WITH PASSWORD = '{password}', CHECK_POLICY = OFF");
        sql.Execute(database, $"CREATE USER [{login}] FOR LOGIN [{login}]; GRANT CREATE PROCEDURE TO [{login}]; GRANT ALTER ON SCHEMA::[Sales] TO [{login}]; DENY SELECT, INSERT ON SCHEMA::[sqlsc] TO [{login}];");
        return new SqlConnectionStringBuilder(sql.ConnectionString(database)) { UserID = login, Password = password }.ConnectionString;
    }

    /// <summary>Runs statements one after another on one connection, so session settings and transactions carry over.</summary>
    private static void Run(string connectionString, params string[] statements)
    {
        using var connection = new SqlConnection(connectionString);
        connection.Open();
        foreach (var statement in statements)
        {
            using var command = new SqlCommand(statement, connection);
            command.ExecuteNonQuery();
        }
    }
}
