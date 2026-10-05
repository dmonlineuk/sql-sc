using Microsoft.Data.SqlClient;
using SqlSc.Core.Apply;
using SqlSc.Core.Comparison;
using SqlSc.Core.WorkingFolders;

namespace SqlSc.IntegrationTests;

[Collection(SqlServerGroup.Name)]
public class ApplyTests(SqlServerFixture sql)
{
    private static WorkingFolder Demo => WorkingFolder.Open(SqlServerFixture.DemoFolder);

    [Fact]
    public void AllAppliesTheFolderButNeverDropsDatabaseOnlyObjects()
    {
        var database = ChangedDatabase();

        var applied = ApplyService.Apply(Demo, sql.ConnectionString(database), [], all: true);

        Assert.True(applied.Problems.Count == 0, string.Join(Environment.NewLine, applied.Problems));
        Assert.True(applied.Applied);
        Assert.Equal(
            [(ApplyAction.Create, "[Sales].[ActiveCustomer]"), (ApplyAction.Alter, "[Sales].[GetCustomer]")],
            applied.Objects.Select(o => (o.Action, o.Name)).Order());
        var report = StatusService.GetStatus(Demo, sql.ConnectionString(database), includeChangedBy: false);
        Assert.Equal([(ObjectStatus.New, "[Sales].[Order]")], report.Changes.Select(c => (c.Status, c.Name)));
    }

    [Fact]
    public void ScriptOnlyChangesNothingAndNamedObjectsAreTheOnlyOnesScripted()
    {
        var database = ChangedDatabase();

        var applied = ApplyService.Apply(Demo, sql.ConnectionString(database), ["Sales.GetCustomer"], scriptOnly: true);

        Assert.True(applied.Problems.Count == 0, string.Join(Environment.NewLine, applied.Problems));
        Assert.False(applied.Applied);
        Assert.Contains("ALTER PROCEDURE [Sales].[GetCustomer]", applied.Script, StringComparison.Ordinal);
        Assert.DoesNotContain("ActiveCustomer", applied.Script, StringComparison.Ordinal);
        Assert.DoesNotContain("[Order]", applied.Script, StringComparison.Ordinal);
        Assert.Equal(3, StatusService.GetStatus(Demo, sql.ConnectionString(database), includeChangedBy: false).Changes.Count);

        var named = ApplyService.Apply(Demo, sql.ConnectionString(database), ["Sales.GetCustomer"]);

        Assert.True(named.Applied, string.Join(Environment.NewLine, named.Problems));
        Assert.Equal(
            ["[Sales].[ActiveCustomer]", "[Sales].[Order]"],
            StatusService.GetStatus(Demo, sql.ConnectionString(database), includeChangedBy: false).Changes.Select(c => c.Name).Order());
    }

    [Fact]
    public void NamingADatabaseOnlyObjectDropsIt()
    {
        var database = ChangedDatabase();

        var applied = ApplyService.Apply(Demo, sql.ConnectionString(database), ["Sales.Order"]);

        Assert.True(applied.Applied, string.Join(Environment.NewLine, applied.Problems));
        Assert.Equal([(ApplyAction.Drop, "[Sales].[Order]")], applied.Objects.Select(o => (o.Action, o.Name)));
        Assert.Equal(0, Scalar(database, "SELECT COUNT(*) FROM sys.tables WHERE name = 'Order'"));
    }

    [Fact]
    public void ChangesThatLoseDataAreBlockedUnlessAllowed()
    {
        var database = sql.CreateDatabaseFromFolder(SqlServerFixture.DemoFolder);
        sql.Execute(database, "ALTER TABLE [Sales].[Customer] ADD [Email] nvarchar(200) NULL");
        sql.Execute(database, "INSERT INTO [Sales].[Customer] ([Name], [Email]) VALUES (N'Ann', N'ann@example.com')");

        var blocked = ApplyService.Apply(Demo, sql.ConnectionString(database), ["Sales.Customer"]);

        Assert.False(blocked.Applied);
        Assert.Contains(blocked.Problems, p => p.Contains("rolled back", StringComparison.Ordinal));
        Assert.Equal(1, Scalar(database, "SELECT COUNT(*) FROM sys.columns WHERE name = 'Email'"));

        var allowed = ApplyService.Apply(Demo, sql.ConnectionString(database), ["Sales.Customer"], allowDataLoss: true);

        Assert.True(allowed.Applied, string.Join(Environment.NewLine, allowed.Problems));
        Assert.Equal(0, Scalar(database, "SELECT COUNT(*) FROM sys.columns WHERE name = 'Email'"));
        Assert.Equal(1, Scalar(database, "SELECT COUNT(*) FROM [Sales].[Customer]"));
    }

    [Fact]
    public void ObjectsLastChangedByAnotherLoginNeedForce()
    {
        var database = sql.CreateDatabaseFromFolder(SqlServerFixture.DemoFolder);
        var login = "other_" + database;
        sql.Execute("master", $"CREATE LOGIN [{login}] WITH PASSWORD = N'Other#Passw0rd', CHECK_POLICY = OFF");
        sql.Execute(database, $"CREATE USER [{login}] FOR LOGIN [{login}]; ALTER ROLE db_owner ADD MEMBER [{login}]");
        var other = new SqlConnectionStringBuilder(sql.ConnectionString(database)) { UserID = login, Password = "Other#Passw0rd" }.ConnectionString;
        using (var connection = new SqlConnection(other))
        {
            connection.Open();
            using var command = new SqlCommand("ALTER PROCEDURE [Sales].[GetCustomer] @CustomerId int AS SELECT 1", connection);
            command.ExecuteNonQuery();
        }

        sql.WaitForDefaultTrace(database, "GetCustomer");

        var refused = ApplyService.Apply(Demo, sql.ConnectionString(database), ["Sales.GetCustomer"]);

        Assert.False(refused.Applied);
        Assert.Contains(refused.Problems, p => p.Contains(login, StringComparison.Ordinal) && p.Contains("--force", StringComparison.Ordinal));

        var forced = ApplyService.Apply(Demo, sql.ConnectionString(database), ["Sales.GetCustomer"], force: true);

        Assert.True(forced.Applied, string.Join(Environment.NewLine, forced.Problems));
        Assert.DoesNotContain("[Sales].[GetCustomer]", StatusService.GetStatus(Demo, sql.ConnectionString(database), includeChangedBy: false).Changes.Select(c => c.Name));
    }

    [Fact]
    public void ModulesGetTheFolderTextIncludingAliasesSqlScRenamesForDacFx()
    {
        const string definition = "CREATE PROCEDURE [Sales].[Numbers]\r\nAS\r\nSELECT n.n FROM (VALUES (1), (2)) n(n)";
        var folder = SqlServerFixture.CopyDemo();
        var database = sql.CreateDatabaseFromFolder(folder);
        File.WriteAllText(Path.Combine(folder, "Stored Procedures", "Sales.Numbers.sql"), $"SET QUOTED_IDENTIFIER ON\r\nGO\r\nSET ANSI_NULLS ON\r\nGO\r\n{definition}\r\nGO\r\n");

        var applied = ApplyService.Apply(WorkingFolder.Open(folder), sql.ConnectionString(database), ["Sales.Numbers"]);

        Assert.True(applied.Applied, string.Join(Environment.NewLine, applied.Problems));
        using (var connection = new SqlConnection(sql.ConnectionString(database)))
        {
            connection.Open();
            using var command = new SqlCommand("SELECT OBJECT_DEFINITION(OBJECT_ID(N'Sales.Numbers'))", connection);
            Assert.Equal(definition, (string)command.ExecuteScalar());
        }

        Assert.Empty(StatusService.GetStatus(WorkingFolder.Open(folder), sql.ConnectionString(database), includeChangedBy: false).Changes);
    }

    private string ChangedDatabase()
    {
        var database = sql.CreateDatabaseFromFolder(SqlServerFixture.DemoFolder);
        sql.Execute(database, "ALTER PROCEDURE [Sales].[GetCustomer] @CustomerId int AS SELECT Name FROM Sales.Customer WHERE CustomerId = @CustomerId");
        sql.Execute(database, "CREATE TABLE [Sales].[Order] ([OrderId] int NOT NULL CONSTRAINT [PK_Order] PRIMARY KEY)");
        sql.Execute(database, "DROP VIEW [Sales].[ActiveCustomer]");
        return database;
    }

    private int Scalar(string database, string query)
    {
        using var connection = new SqlConnection(sql.ConnectionString(database));
        connection.Open();
        using var command = new SqlCommand(query, connection);
        return (int)command.ExecuteScalar();
    }
}
