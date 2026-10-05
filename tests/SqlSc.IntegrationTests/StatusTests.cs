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
    public void CommentsAroundAProcedureMatch()
    {
        const string definition = """
            -- =============================================
            -- Author:      x
            -- =============================================
            create   procedure [Sales].[GetCustomer]
                @CustomerId int
            AS
            SELECT CustomerId, Name, IsActive FROM Sales.Customer WHERE CustomerId = @CustomerId;
            -- trailing comment
            """;
        var folder = SqlServerFixture.CopyDemo();
        var database = sql.CreateDatabaseFromFolder(folder);
        sql.Execute(database, "DROP PROCEDURE [Sales].[GetCustomer]");
        sql.Execute(database, definition);
        File.WriteAllText(Path.Combine(folder, "Stored Procedures", "Sales.GetCustomer.sql"), $"SET QUOTED_IDENTIFIER ON\r\nGO\r\nSET ANSI_NULLS ON\r\nGO\r\n{definition}\r\nGO\r\n");

        var report = StatusService.GetStatus(WorkingFolder.Open(folder), sql.ConnectionString(database), includeChangedBy: false);

        Assert.True(report.Changes.Count == 0, string.Join(Environment.NewLine, report.Changes));
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
    public void DiffShowsWhereModifiedScriptsFirstDiffer()
    {
        var database = sql.CreateDatabaseFromFolder(SqlServerFixture.DemoFolder);
        sql.Execute(database, """
            ALTER PROCEDURE [Sales].[GetCustomer]
                @CustomerId int
            AS
            BEGIN
                SET NOCOUNT ON;
                SELECT CustomerId, Name FROM Sales.Customer WHERE CustomerId = @CustomerId;
            END
            """);
        sql.Execute(database, "CREATE INDEX [IX_Customer_Name] ON [Sales].[Customer] ([Name])");

        var report = StatusService.GetStatus(WorkingFolder.Open(SqlServerFixture.DemoFolder), sql.ConnectionString(database), includeChangedBy: false, includeDifferences: true);

        var changes = report.Changes.ToDictionary(c => c.Name);
        Assert.Equal(
            "line 6 is `SELECT CustomerId, Name FROM Sales.Customer WHERE CustomerId = @CustomerId;` in the database, `SELECT CustomerId, Name, IsActive FROM Sales.Customer WHERE CustomerId = @CustomerId;` in the folder",
            changes["[Sales].[GetCustomer]"].Difference);
        Assert.Null(changes["[Sales].[Customer]"].Difference);
        Assert.All(
            StatusService.GetStatus(WorkingFolder.Open(SqlServerFixture.DemoFolder), sql.ConnectionString(database), includeChangedBy: false).Changes,
            c => Assert.Null(c.Difference));
    }

    [Fact]
    public void DiffShowsANewTableColumn()
    {
        var database = sql.CreateDatabaseFromFolder(SqlServerFixture.DemoFolder);
        sql.Execute(database, "ALTER TABLE [Sales].[Customer] ADD [Region] nvarchar(50) NULL");

        var report = StatusService.GetStatus(WorkingFolder.Open(SqlServerFixture.DemoFolder), sql.ConnectionString(database), includeChangedBy: false, includeDifferences: true);

        var difference = report.Changes.Single(c => c.Name == "[Sales].[Customer]").Difference;
        Assert.NotNull(difference);
        Assert.Contains("[Region]", difference, StringComparison.Ordinal);
        Assert.Contains("in the database", difference, StringComparison.Ordinal);
    }

    [Fact]
    public void DiffShowsWhereAModifiedChildFirstDiffers()
    {
        var database = sql.CreateDatabaseFromFolder(SqlServerFixture.DemoFolder);
        sql.Execute(database, "ALTER TABLE [Sales].[Customer] DROP CONSTRAINT [PK_Customer]; ALTER TABLE [Sales].[Customer] ADD CONSTRAINT [PK_Customer] PRIMARY KEY NONCLUSTERED ([CustomerId])");

        var report = StatusService.GetStatus(WorkingFolder.Open(SqlServerFixture.DemoFolder), sql.ConnectionString(database), includeChangedBy: false, includeDifferences: true);

        var child = report.Changes.Single(c => c.Name == "[Sales].[Customer]").Children.Single();
        Assert.Equal(ObjectStatus.Modified, child.Status);
        Assert.NotNull(child.Difference);
        Assert.Contains("NONCLUSTERED", child.Difference, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void NewObjectsUsingColumnsTheFolderLacksAreReportedAsNew(bool fullExtract)
    {
        var database = sql.CreateDatabaseFromFolder(SqlServerFixture.DemoFolder);
        sql.Execute(database, "ALTER TABLE [Sales].[Customer] ADD [Region] nvarchar(50) NULL");
        sql.Execute(database, "CREATE VIEW [Sales].[CustomerRegion] AS SELECT c.Region AS region FROM [Sales].[Customer] c");

        var report = StatusService.GetStatus(WorkingFolder.Open(SqlServerFixture.DemoFolder), sql.ConnectionString(database), includeChangedBy: false, fullExtract: fullExtract);

        var changes = report.Changes.ToDictionary(c => c.Name);
        Assert.Equal(ObjectStatus.Modified, changes["[Sales].[Customer]"].Status);
        Assert.Equal(ObjectStatus.New, changes["[Sales].[CustomerRegion]"].Status);
    }

    [Theory]
    [InlineData("PK__Region__3213E83F9AEF8E7A", false)]
    [InlineData("PK__Region__1111111122222222", true)]
    public void ExplicitConstraintNamesThatLookSystemGeneratedMatch(string folderName, bool modified)
    {
        var folder = SqlServerFixture.CopyDemo();
        var database = sql.CreateDatabaseFromFolder(folder);
        sql.Execute(database, "CREATE TABLE [Sales].[Region] ([RegionId] int NOT NULL CONSTRAINT [PK__Region__3213E83F9AEF8E7A] PRIMARY KEY CLUSTERED, [Active] bit NOT NULL CONSTRAINT [DF__Region__Active__42501F7D] DEFAULT ((1)))");
        File.WriteAllText(Path.Combine(folder, "Tables", "Sales.Region.sql"), $"""
            CREATE TABLE [Sales].[Region]
            (
            [RegionId] [int] NOT NULL,
            [Active] [bit] NOT NULL CONSTRAINT [DF__Region__Active__42501F7D] DEFAULT ((1))
            )
            GO
            ALTER TABLE [Sales].[Region] ADD CONSTRAINT [{folderName}] PRIMARY KEY CLUSTERED ([RegionId])
            GO
            """);

        var report = StatusService.GetStatus(WorkingFolder.Open(folder), sql.ConnectionString(database), includeChangedBy: false);

        Assert.Equal(modified, report.Changes.Any(c => c.Name == "[Sales].[Region]"));
        Assert.DoesNotContain(report.Changes, c => c.Name != "[Sales].[Region]");
    }

    [Fact]
    public void SystemNamedConstraintsScriptedWithTheirNamesMatch()
    {
        var folder = SqlServerFixture.CopyDemo();
        var database = sql.CreateDatabaseFromFolder(folder);
        sql.Execute(database, "CREATE TABLE [Sales].[Region] ([RegionId] int NOT NULL PRIMARY KEY, [Code] nchar(2) NOT NULL UNIQUE, [Active] bit NOT NULL DEFAULT ((1)))");
        var names = new Dictionary<string, string>();
        using (var connection = new SqlConnection(sql.ConnectionString(database)))
        {
            connection.Open();
            using var command = new SqlCommand("SELECT type, name FROM sys.objects WHERE parent_object_id = OBJECT_ID('Sales.Region') AND is_ms_shipped = 0", connection);
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                names[reader.GetString(0).Trim()] = reader.GetString(1);
            }
        }

        File.WriteAllText(Path.Combine(folder, "Tables", "Sales.Region.sql"), $"""
            CREATE TABLE [Sales].[Region]
            (
            [RegionId] [int] NOT NULL,
            [Code] [nchar] (2) NOT NULL,
            [Active] [bit] NOT NULL CONSTRAINT [{names["D"]}] DEFAULT ((1))
            )
            GO
            ALTER TABLE [Sales].[Region] ADD CONSTRAINT [{names["PK"]}] PRIMARY KEY CLUSTERED ([RegionId])
            GO
            ALTER TABLE [Sales].[Region] ADD CONSTRAINT [{names["UQ"]}] UNIQUE NONCLUSTERED ([Code])
            GO
            """);

        var report = StatusService.GetStatus(WorkingFolder.Open(folder), sql.ConnectionString(database), includeChangedBy: false);

        Assert.True(report.Changes.Count == 0, string.Join(Environment.NewLine, Describe(report)));
    }

    [Fact]
    public void ChangedByComesFromTheDefaultTrace()
    {
        var database = sql.CreateDatabaseFromFolder(SqlServerFixture.DemoFolder);
        sql.Execute(database, "ALTER PROCEDURE [Sales].[GetCustomer] @CustomerId int AS SELECT @CustomerId AS CustomerId");
        sql.Execute(database, "DROP VIEW [Sales].[ActiveCustomer]");
        sql.WaitForDefaultTrace(database, "ActiveCustomer");

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
        var folder = SqlServerFixture.CopyDemo();
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

        Assert.Equal(["Working folder", "Connection", "Server", "Permissions", "Default trace", "Latency", "Catalog", "Filter", "Catalog scripting", "Schema extract"], checks.Select(c => c.Name));
        Assert.All(checks, c => Assert.True(c.Result == CheckResult.Ok, $"{c.Name}: {c.Result} {c.Detail}"));
        Assert.Contains("SQL (sa)", checks[1].Detail, StringComparison.Ordinal);
        Assert.DoesNotContain(checks, c => c.Detail.Contains("Password", StringComparison.OrdinalIgnoreCase));
        Assert.StartsWith("No filter: all 5 objects", checks[7].Detail, StringComparison.Ordinal);
        Assert.EndsWith("catalog scripting matches it", checks[^1].Detail, StringComparison.Ordinal);
    }

    [Fact]
    public void CatalogScriptingMatchesTheFullExtractOfARichDatabase()
    {
        var database = sql.CreateDatabaseFromScript(SqlServerFixture.RichScript);

        var checks = Doctor.Run(sql.ConnectionString(database), folder: null).ToDictionary(c => c.Name);

        Assert.True(checks["Catalog scripting"].Result == CheckResult.Ok, checks["Catalog scripting"].Detail);
        Assert.True(checks["Schema extract"].Result == CheckResult.Ok, checks["Schema extract"].Detail);
        Assert.EndsWith("catalog scripting matches it", checks["Schema extract"].Detail, StringComparison.Ordinal);
    }

    [Fact]
    public void CatalogScriptingAndFullExtractReportTheSameChanges()
    {
        var folder = SqlServerFixture.CopyDemo();
        File.WriteAllText(Path.Combine(folder, "Filter.scpf"), ScratchExcluded);
        var database = sql.CreateDatabaseFromFolder(folder);
        sql.Execute(database, "ALTER PROCEDURE [Sales].[GetCustomer] @CustomerId int AS SELECT @CustomerId AS CustomerId");
        sql.Execute(database, "DROP VIEW [Sales].[ActiveCustomer]");
        sql.Execute(database, "CREATE INDEX [IX_Customer_Name] ON [Sales].[Customer] ([Name])");
        sql.Execute(database, "GRANT SELECT ON [Sales].[Customer] TO [app_reader]");
        sql.Execute(database, "CREATE SCHEMA [scratch]");
        sql.Execute(database, "CREATE TABLE [scratch].[Lookup] ([Id] int NOT NULL CONSTRAINT [PK_Lookup] PRIMARY KEY)");
        sql.Execute(database, "CREATE VIEW [Sales].[UsesScratch] AS SELECT [Id] FROM [scratch].[Lookup]");

        var catalog = StatusService.GetStatus(WorkingFolder.Open(folder), sql.ConnectionString(database), includeChangedBy: false);
        var full = StatusService.GetStatus(WorkingFolder.Open(folder), sql.ConnectionString(database), includeChangedBy: false, fullExtract: true);

        Assert.True(catalog.DatabaseModel.FromCatalog, catalog.DatabaseModel.Describe());
        Assert.True(catalog.DatabaseModel.ScriptedCount > catalog.DatabaseModel.TrackedCount, catalog.DatabaseModel.Describe());
        Assert.False(full.DatabaseModel.FromCatalog);
        Assert.Equal(Describe(full), Describe(catalog));
        Assert.Equal(["[Sales].[ActiveCustomer]", "[Sales].[Customer]", "[Sales].[GetCustomer]", "[Sales].[UsesScratch]"], catalog.Changes.Select(c => c.Name).Order(StringComparer.Ordinal));
    }

    [Fact]
    public void UnsupportedFeaturesFallBackToTheFullExtract()
    {
        var database = sql.CreateDatabaseFromFolder(SqlServerFixture.DemoFolder);
        sql.Execute(database, "CREATE TABLE [Sales].[Price] ([Id] int NOT NULL CONSTRAINT [PK_Price] PRIMARY KEY) AS NODE");

        var report = StatusService.GetStatus(WorkingFolder.Open(SqlServerFixture.DemoFolder), sql.ConnectionString(database), includeChangedBy: false);

        Assert.False(report.DatabaseModel.FromCatalog);
        Assert.Contains(report.DatabaseModel.Unsupported, u => u.Contains("graph", StringComparison.Ordinal));
        Assert.Contains(report.Changes, c => c.Name == "[Sales].[Price]" && c.Status == ObjectStatus.New);
    }

    [Fact]
    public void FullExtractLeavesOutUntrackedObjectsWithUnresolvedReferences()
    {
        var folder = SqlServerFixture.CopyDemo();
        File.WriteAllText(Path.Combine(folder, "Filter.scpf"), ScratchExcluded);
        var database = sql.CreateDatabaseFromFolder(folder);
        sql.Execute(database, "CREATE SCHEMA [scratch]");
        sql.Execute(database, "CREATE TABLE [scratch].[Dropped] ([Id] int NOT NULL)");
        sql.Execute(database, "CREATE VIEW [scratch].[OverDropped] AS SELECT [src].[Id] FROM [scratch].[Dropped] AS [src]");
        sql.Execute(database, "CREATE VIEW [scratch].[OnTop] AS SELECT [Id] FROM [scratch].[OverDropped]");
        sql.Execute(database, "GRANT SELECT ON [scratch].[OverDropped] TO [app_reader]");
        sql.Execute(database, "DROP TABLE [scratch].[Dropped]");

        var report = StatusService.GetStatus(WorkingFolder.Open(folder), sql.ConnectionString(database), includeChangedBy: false, fullExtract: true);
        var checks = Doctor.Run(sql.ConnectionString(database), WorkingFolder.Open(folder)).ToDictionary(c => c.Name);

        Assert.True(report.Changes.Count == 0, string.Join(Environment.NewLine, report.Changes));
        Assert.Contains("left out 2 untracked objects with unresolved references: [scratch].[OnTop], [scratch].[OverDropped]", report.DatabaseModel.Describe(), StringComparison.Ordinal);
        Assert.True(checks["Schema extract"].Result == CheckResult.Ok, checks["Schema extract"].Detail);
        Assert.EndsWith("catalog scripting matches it", checks["Schema extract"].Detail, StringComparison.Ordinal);
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

    private const string ScratchExcluded = """
        <?xml version="1.0" encoding="utf-8"?>
        <NamedFilter version="1" type="SQLCompareFilter">
          <Filter version="1" type="DifferenceFilter">
            <Filters version="1">
              <None version="1"><Include>False</Include><Expression>(@SCHEMA LIKE 'scratch%')</Expression></None>
            </Filters>
          </Filter>
        </NamedFilter>
        """;

    [Fact]
    public void TableTypeColumnsWithTheDefaultCollationMatch()
    {
        var folder = SqlServerFixture.CopyDemo();
        var database = sql.CreateDatabaseFromFolder(folder);
        sql.Execute(database, "CREATE TYPE [dbo].[ColumnList] AS TABLE ([TABLE_NAME] sysname NOT NULL, [IS_NULLABLE] varchar(3) NOT NULL)");
        Directory.CreateDirectory(Path.Combine(folder, "Types", "User-defined Data Types"));
        File.WriteAllText(Path.Combine(folder, "Types", "User-defined Data Types", "dbo.ColumnList.sql"), """
            CREATE TYPE [dbo].[ColumnList] AS TABLE
            (
            [TABLE_NAME] [sys].[sysname] NOT NULL,
            [IS_NULLABLE] [varchar] (3) COLLATE SQL_Latin1_General_CP1_CI_AS NOT NULL
            )
            GO

            """);

        var report = StatusService.GetStatus(WorkingFolder.Open(folder), sql.ConnectionString(database), includeChangedBy: false);

        Assert.True(report.Changes.Count == 0, string.Join(Environment.NewLine, report.Changes));
    }

    [Fact]
    public void TableVariableColumnsAreGroupedUnderTheirProcedure()
    {
        var folder = SqlServerFixture.CopyDemo();
        File.WriteAllText(Path.Combine(folder, "Tables", "Sales.DataControl.sql"), "CREATE TABLE [Sales].[DataControl] ([Id] uniqueidentifier NOT NULL DEFAULT (newid()), [Filename] varchar(100) NULL)\r\nGO\r\n");
        var database = sql.CreateDatabaseFromFolder(folder);
        File.WriteAllText(Path.Combine(folder, "Stored Procedures", "Sales.NewDataControl.sql"), """
            CREATE PROCEDURE [Sales].[NewDataControl] @filename varchar(100)
            AS BEGIN
                DECLARE @ControlId TABLE (DataId uniqueidentifier NOT NULL);
                INSERT [Sales].[DataControl] (Filename) OUTPUT inserted.Id INTO @ControlId SELECT @filename;
                SELECT DataId FROM @ControlId;
            END
            GO

            """);

        var report = StatusService.GetStatus(WorkingFolder.Open(folder), sql.ConnectionString(database), includeChangedBy: false);

        Assert.Equal(["Deleted Procedure [Sales].[NewDataControl] Stored Procedures/Sales.NewDataControl.sql: "], Describe(report));
    }

    private static List<string> Describe(StatusReport report) =>
        report.Changes
            .Select(c => $"{c.Status} {c.ObjectType} {c.Name} {c.File}: {string.Join(", ", c.Children.Select(child => $"{child.Status} {child.ObjectType} {child.Name}").Order(StringComparer.Ordinal))}")
            .Order(StringComparer.Ordinal)
            .ToList();
}
