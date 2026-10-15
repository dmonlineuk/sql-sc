using SqlSc.Core.Apply;
using SqlSc.Core.Comparison;
using SqlSc.Core.Export;
using SqlSc.Core.WorkingFolders;

namespace SqlSc.IntegrationTests;

[Collection(SqlServerGroup.Name)]
public class BaselineTests(SqlServerFixture sql)
{
    private const string ServerVersion = "ALTER PROCEDURE [Sales].[GetCustomer] @CustomerId int AS SELECT Name FROM Sales.Customer WHERE CustomerId = @CustomerId";

    private const string FolderVersion = "CREATE PROCEDURE [Sales].[GetCustomer]\r\n    @CustomerId int\r\nAS\r\nSELECT CustomerId FROM Sales.Customer WHERE CustomerId = @CustomerId;\r\nGO\r\n";

    private readonly BaselineStore store = new(Path.Combine(Path.GetTempPath(), "sql-sc-tests", Guid.NewGuid().ToString("N")));

    [Fact]
    public void DifferencesAreUnknownUntilTheSidesHaveMatched()
    {
        var (folder, connection) = Synced();
        sql.Execute(Database(connection), ServerVersion);

        var fresh = new BaselineStore(Path.Combine(Path.GetTempPath(), "sql-sc-tests", Guid.NewGuid().ToString("N")));
        Assert.Equal(ChangeSide.Unknown, Side(StatusService.GetStatus(Open(folder), connection, includeChangedBy: false, baseline: fresh), "[Sales].[GetCustomer]"));
        Assert.Equal(ChangeSide.Database, Side(Status(folder, connection), "[Sales].[GetCustomer]"));
    }

    [Fact]
    public void ApplyDoesNotUndoANewerDatabaseChange()
    {
        var (folder, connection) = Synced();
        sql.Execute(Database(connection), ServerVersion);

        var all = ApplyService.Apply(Open(folder), connection, [], all: true, baseline: store);

        Assert.Empty(all.Problems);
        Assert.DoesNotContain(all.Objects, o => o.Name == "[Sales].[GetCustomer]");
        Assert.Contains(all.Skipped!, s => s.StartsWith("[Sales].[GetCustomer] changed in the database", StringComparison.Ordinal));

        var named = ApplyService.Apply(Open(folder), connection, ["Sales.GetCustomer"], baseline: store);
        Assert.False(named.Applied);
        Assert.Contains(named.Problems, p => p.Contains("--force", StringComparison.Ordinal));

        var exported = ExportService.Export(Open(folder), connection, new ExportSelection([], All: true), baseline: store);
        Assert.Empty(exported.Problems);
        Assert.Contains(exported.Files, f => f.Action == FileAction.Written);
        Assert.Empty(Status(folder, connection).Changes);
    }

    [Fact]
    public void ExportDoesNotUndoAFolderChange()
    {
        var (folder, connection) = Synced();
        var path = Path.Combine(folder, "Stored Procedures", "Sales.GetCustomer.sql");
        File.WriteAllText(path, FolderVersion);
        File.Delete(Path.Combine(folder, "Views", "Sales.ActiveCustomer.sql"));

        var status = Status(folder, connection);
        Assert.Equal(ChangeSide.Folder, Side(status, "[Sales].[GetCustomer]"));
        Assert.Equal(ChangeSide.Folder, Side(status, "[Sales].[ActiveCustomer]"));

        var exported = ExportService.Export(Open(folder), connection, new ExportSelection([], All: true), baseline: store);

        Assert.Empty(exported.Problems);
        Assert.Empty(exported.Files);
        Assert.Equal(2, exported.Skipped.Count);
        Assert.Equal(FolderVersion, File.ReadAllText(path));

        var applied = ApplyService.Apply(Open(folder), connection, [], all: true, baseline: store);
        Assert.True(applied.Applied, string.Join(Environment.NewLine, applied.Problems));
        Assert.Equal(["[Sales].[GetCustomer]"], applied.Objects.Select(o => o.Name));

        sql.Execute(Database(connection), "ALTER PROCEDURE [Sales].[GetCustomer] @CustomerId int AS SELECT 2");
        Assert.Equal(ChangeSide.Database, Side(Status(folder, connection), "[Sales].[GetCustomer]"));
    }

    [Fact]
    public void ChangesOnBothSidesAreAConflict()
    {
        var (folder, connection) = Synced();
        sql.Execute(Database(connection), ServerVersion);
        File.WriteAllText(Path.Combine(folder, "Stored Procedures", "Sales.GetCustomer.sql"), FolderVersion);

        Assert.Equal(ChangeSide.Both, Side(Status(folder, connection), "[Sales].[GetCustomer]"));
        Assert.Contains(ExportService.Export(Open(folder), connection, new ExportSelection([], All: true), baseline: store).Problems, p => p.Contains("conflict", StringComparison.Ordinal));
        var applied = ApplyService.Apply(Open(folder), connection, [], all: true, baseline: store);
        Assert.False(applied.Applied);
        Assert.Contains(applied.Problems, p => p.Contains("conflict", StringComparison.Ordinal));

        var resolved = ExportService.Export(Open(folder), connection, new ExportSelection(["Sales.GetCustomer"], Force: true), baseline: store);

        Assert.Empty(resolved.Problems);
        Assert.Empty(Status(folder, connection).Changes);
        sql.Execute(Database(connection), "ALTER PROCEDURE [Sales].[GetCustomer] @CustomerId int AS SELECT 3");
        Assert.Equal(ChangeSide.Database, Side(Status(folder, connection), "[Sales].[GetCustomer]"));
    }

    [Fact]
    public void ObjectsCreatedOnOneSideAfterTheBaselineAreAttributedToIt()
    {
        var (folder, connection) = Synced();
        sql.Execute(Database(connection), "CREATE TABLE [Sales].[Order] ([OrderId] int NOT NULL CONSTRAINT [PK_Order] PRIMARY KEY)");
        File.WriteAllText(
            Path.Combine(folder, "Stored Procedures", "Sales.CountCustomers.sql"),
            "CREATE PROCEDURE [Sales].[CountCustomers]\r\nAS\r\nSELECT COUNT(*) FROM Sales.Customer;\r\nGO\r\n");

        var status = Status(folder, connection);

        Assert.Equal(ChangeSide.Database, Side(status, "[Sales].[Order]"));
        Assert.Equal(ChangeSide.Folder, Side(status, "[Sales].[CountCustomers]"));
        var exported = ExportService.Export(Open(folder), connection, new ExportSelection([], All: true), baseline: store);
        Assert.Empty(exported.Problems);
        Assert.Equal([Path.Combine("Tables", "Sales.Order.sql")], exported.Files.Select(f => f.Path));
        Assert.True(File.Exists(Path.Combine(folder, "Stored Procedures", "Sales.CountCustomers.sql")));
    }

    private (string Folder, string Connection) Synced()
    {
        var folder = SqlServerFixture.CopyDemo();
        var connection = sql.ConnectionString(sql.CreateDatabaseFromFolder(folder));
        Assert.Empty(Status(folder, connection).Changes);
        return (folder, connection);
    }

    private StatusReport Status(string folder, string connection) =>
        StatusService.GetStatus(Open(folder), connection, includeChangedBy: false, baseline: store);

    private static WorkingFolder Open(string folder) => WorkingFolder.Open(folder);

    private static string Database(string connection) => new Microsoft.Data.SqlClient.SqlConnectionStringBuilder(connection).InitialCatalog;

    private static ChangeSide Side(StatusReport report, string name) => Assert.Single(report.Changes, c => c.Name == name).Side;
}
