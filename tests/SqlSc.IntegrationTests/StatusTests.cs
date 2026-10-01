using SqlSc.Core.ChangeTracking;
using SqlSc.Core.Comparison;
using SqlSc.Core.WorkingFolders;

namespace SqlSc.IntegrationTests;

[Collection(SqlServerGroup.Name)]
public class StatusTests(SqlServerFixture sql)
{
    [Fact]
    public void DatabaseBuiltFromFolderHasNoDifferences()
    {
        var database = sql.CreateDatabaseFromFolder(SqlServerFixture.DemoFolder);

        var report = StatusService.GetStatus(WorkingFolder.Open(SqlServerFixture.DemoFolder), sql.ConnectionString(database));

        Assert.True(report.Changes.Count == 0, string.Join(Environment.NewLine, report.Changes));
        Assert.Equal("Sql160", report.Platform);
    }

    [Fact]
    public void ReportsNewModifiedAndDeletedObjects()
    {
        var database = sql.CreateDatabaseFromFolder(SqlServerFixture.DemoFolder);
        sql.Execute(database, """
            ALTER PROCEDURE [Sales].[GetCustomer] @CustomerId int AS
            BEGIN
                SET NOCOUNT ON;
                SELECT CustomerId, Name FROM Sales.Customer WHERE CustomerId = @CustomerId;
            END
            """);
        sql.Execute(database, "CREATE TABLE [Sales].[Order] ([OrderId] int NOT NULL CONSTRAINT [PK_Order] PRIMARY KEY, [CustomerId] int NOT NULL)");
        sql.Execute(database, "DROP VIEW [Sales].[ActiveCustomer]");

        var report = StatusService.GetStatus(WorkingFolder.Open(SqlServerFixture.DemoFolder), sql.ConnectionString(database));

        var changes = report.Changes.ToDictionary(c => c.Name);
        Assert.Equal(ObjectStatus.Modified, changes["[Sales].[GetCustomer]"].Status);
        Assert.Equal(Path.Combine("Stored Procedures", "Sales.GetCustomer.sql"), changes["[Sales].[GetCustomer]"].File);
        Assert.Equal(ObjectStatus.New, changes["[Sales].[Order]"].Status);
        Assert.Equal(ObjectStatus.Deleted, changes["[Sales].[ActiveCustomer]"].Status);
        Assert.Equal(3, report.Changes.Count(c => c.ObjectType is "Procedure" or "Table" or "View"));
    }

    [Fact]
    public void ChangedByComesFromTheDefaultTrace()
    {
        var database = sql.CreateDatabaseFromFolder(SqlServerFixture.DemoFolder);
        sql.Execute(database, "ALTER PROCEDURE [Sales].[GetCustomer] @CustomerId int AS SELECT @CustomerId AS CustomerId");
        sql.Execute(database, "DROP VIEW [Sales].[ActiveCustomer]");

        var report = StatusService.GetStatus(WorkingFolder.Open(SqlServerFixture.DemoFolder), sql.ConnectionString(database));

        Assert.True(report.ChangeLog.Available, report.ChangeLog.UnavailableReason);
        var altered = report.Changes.Single(c => c.Name == "[Sales].[GetCustomer]").LastChange;
        Assert.NotNull(altered);
        Assert.Equal(ChangeKind.Altered, altered.Kind);
        Assert.Equal("sa", altered.LoginName);

        var dropped = report.ChangeLog.Find(null, "ActiveCustomer");
        Assert.NotNull(dropped);
        Assert.Equal(ChangeKind.Deleted, dropped.Kind);
    }
}
