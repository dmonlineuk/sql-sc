using Microsoft.Data.SqlClient;
using SqlSc.Core.ChangeTracking;
using SqlSc.Core.Comparison;
using SqlSc.Core.Database;
using SqlSc.Core.Diagnostics;
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
        Assert.Empty(changes["[Sales].[Order]"].Children);
        Assert.Empty(changes["[Sales].[ActiveCustomer]"].Children);
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

    [Fact]
    public void ChildChangesAreGroupedUnderTheirOwner()
    {
        var database = sql.CreateDatabaseFromFolder(SqlServerFixture.DemoFolder);
        sql.Execute(database, "CREATE INDEX [IX_Customer_Name] ON [Sales].[Customer] ([Name])");
        sql.Execute(database, "ALTER TABLE [Sales].[Customer] ADD CONSTRAINT [CK_Customer_Name] CHECK (LEN([Name]) > 0)");
        sql.Execute(database, "EXEC sp_updateextendedproperty N'MS_Description', N'Changed', 'SCHEMA', N'Sales', 'TABLE', N'Customer'");
        sql.Execute(database, "GRANT SELECT ON [Sales].[Customer] TO [app_reader]");

        var report = StatusService.GetStatus(WorkingFolder.Open(SqlServerFixture.DemoFolder), sql.ConnectionString(database));

        var change = Assert.Single(report.Changes);
        Assert.Equal(ObjectStatus.Modified, change.Status);
        Assert.Equal("Table", change.ObjectType);
        Assert.Equal("[Sales].[Customer]", change.Name);
        Assert.Equal(Path.Combine("Tables", "Sales.Customer.sql"), change.File);
        var children = change.Children.Select(c => (c.Status, c.ObjectType)).ToList();
        Assert.Contains((ObjectStatus.New, "Index"), children);
        Assert.Contains((ObjectStatus.New, "CheckConstraint"), children);
        Assert.Contains((ObjectStatus.Modified, "ExtendedProperty"), children);
        Assert.Contains((ObjectStatus.New, "Permission"), children);
    }

    [Fact]
    public void FilteredObjectsAreNotReported()
    {
        var folder = CopyDemo();
        File.WriteAllText(Path.Combine(folder, "Filter.scpf"), """
            <?xml version="1.0" encoding="utf-8"?>
            <NamedFilter version="1" type="SQLCompareFilter">
              <Filter version="1" type="DifferenceFilter">
                <Filters version="1">
                  <None version="1"><Include>False</Include><Expression>(@SCHEMA LIKE 'scratch%')</Expression></None>
                </Filters>
              </Filter>
            </NamedFilter>
            """);
        var database = sql.CreateDatabaseFromFolder(folder);
        sql.Execute(database, "CREATE SCHEMA [scratch]");
        sql.Execute(database, "CREATE TABLE [scratch].[Temp] ([Id] int NOT NULL CONSTRAINT [PK_Temp] PRIMARY KEY)");
        sql.Execute(database, "CREATE TABLE [Sales].[Kept] ([Id] int NOT NULL)");

        var report = StatusService.GetStatus(WorkingFolder.Open(folder), sql.ConnectionString(database));

        Assert.Equal("Filter.scpf", report.FilterPath);
        var change = Assert.Single(report.Changes);
        Assert.Equal("[Sales].[Kept]", change.Name);
    }

    [Fact]
    public void DoctorPassesAgainstSqlServer2022()
    {
        var database = sql.CreateDatabaseFromFolder(SqlServerFixture.DemoFolder);

        var checks = Doctor.Run(sql.ConnectionString(database), WorkingFolder.Open(SqlServerFixture.DemoFolder));

        Assert.Equal(["Working folder", "Connection", "Server", "Permissions", "Default trace", "Latency", "Catalog", "Filter", "Schema extract"], checks.Select(c => c.Name));
        Assert.All(checks, c => Assert.True(c.Result == CheckResult.Ok, $"{c.Name}: {c.Result} {c.Detail}"));
        Assert.Contains("SQL (sa)", checks[1].Detail, StringComparison.Ordinal);
        Assert.DoesNotContain(checks, c => c.Detail.Contains("Password", StringComparison.OrdinalIgnoreCase));
        Assert.StartsWith("No filter: all 5 objects", checks[7].Detail, StringComparison.Ordinal);
    }

    [Fact]
    public void CatalogSummaryCountsTheDemoDatabase()
    {
        var database = sql.CreateDatabaseFromFolder(SqlServerFixture.DemoFolder);

        var catalog = CatalogSummary.Query(sql.ConnectionString(database));

        Assert.Equal(
            ["Procedure Sales.GetCustomer", "Schema .Sales", "Table Sales.Customer", "User .app_reader", "View Sales.ActiveCustomer"],
            catalog.Objects.Select(o => $"{o.ObjectType} {o.Schema}.{o.Name}").Order(StringComparer.Ordinal));
        Assert.True(catalog.Children.Columns > 0);
        Assert.True(catalog.Children.Indexes > 0);
        Assert.True(catalog.ModuleBytes > 0);
    }

    [Fact]
    public void DoctorCanSkipTheExtract()
    {
        var database = sql.CreateDatabaseFromFolder(SqlServerFixture.DemoFolder);

        var checks = Doctor.Run(sql.ConnectionString(database), folder: null, extract: false);

        Assert.Equal(CheckResult.Skipped, checks[^1].Result);
        Assert.DoesNotContain(checks, c => c.Name == "Filter");
    }

    [Fact]
    public void DoctorReportsConnectionFailures()
    {
        var connection = new SqlConnectionStringBuilder(sql.ConnectionString("master")) { Password = "wrong", ConnectTimeout = 5 }.ConnectionString;

        var checks = Doctor.Run(connection, folder: null);

        var check = Assert.Single(checks);
        Assert.Equal(CheckResult.Failed, check.Result);
        Assert.DoesNotContain("wrong", check.Detail, StringComparison.Ordinal);
    }

    private static string CopyDemo()
    {
        var target = Path.Combine(Path.GetTempPath(), "sql-sc-tests", Guid.NewGuid().ToString("N"));
        foreach (var file in Directory.EnumerateFiles(SqlServerFixture.DemoFolder, "*", SearchOption.AllDirectories))
        {
            var destination = Path.Combine(target, Path.GetRelativePath(SqlServerFixture.DemoFolder, file));
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            File.Copy(file, destination);
        }

        return target;
    }
}
