using Microsoft.Data.SqlClient;
using SqlSc.Core.Apply;
using SqlSc.Core.Comparison;
using SqlSc.Core.Export;
using SqlSc.Core.StaticData;
using SqlSc.Core.WorkingFolders;

namespace SqlSc.IntegrationTests;

[Collection(SqlServerGroup.Name)]
public class StaticDataTests(SqlServerFixture sql)
{
    private const string LookupTable = """
        CREATE SCHEMA app;
        GO
        CREATE TABLE app.Lookup (
            Code varchar(10) NOT NULL, Seq int NOT NULL, Id int IDENTITY(1, 1) NOT NULL,
            Name nvarchar(50) NULL, Note varchar(20) NULL, Fixed char(5) NULL,
            Amount decimal(38, 10) NULL, Ratio float NULL, Single real NULL, Price money NULL,
            Flag bit NULL, Tiny tinyint NULL, Big bigint NULL,
            Born date NULL, Stamp datetime NULL, Precise datetime2(7) NULL, Whole datetime2(0) NULL, Minute smalldatetime NULL,
            At datetimeoffset(3) NULL, Clock time(4) NULL,
            Uid uniqueidentifier NULL, Bytes varbinary(20) NULL, Doc xml NULL, Place geography NULL, Node hierarchyid NULL,
            Shout AS UPPER(Name), Version rowversion,
            CONSTRAINT PK_Lookup PRIMARY KEY (Code, Seq));
        GO
        INSERT INTO app.Lookup (Code, Seq, Name, Note, Fixed, Amount, Ratio, Single, Price, Flag, Tiny, Big, Born, Stamp, Precise, Whole, Minute, At, Clock, Uid, Bytes, Doc, Place, Node)
        VALUES ('b', 2, N'O''Brien ünï ' + NCHAR(10) + N'line 2', 'trail  ', 'ab', 1234567890123456789012345678.0123456789, 0.1, 1.5, 12.34, 1, 255, -9223372036854775808,
            '2024-02-29', '2024-01-02T03:04:05.997', '2024-01-02T03:04:05.1234567', '2024-01-02T03:04:05', '2024-01-02T03:04:00', '2024-01-02T03:04:05.123+05:30', '13:14:15.1234',
            NEWID(), 0x00FF, N'<a b="1">x</a>', geography::Point(51.5, -0.1, 4326), hierarchyid::Parse('/1/2/'));
        INSERT INTO app.Lookup (Code, Seq) VALUES ('a', 1);
        CREATE TABLE app.NoKey (Id int NOT NULL);
        GO
        """;

    [Fact]
    public void TablesListsUserTablesWithRowsAndPrimaryKeys()
    {
        var database = CreateDatabase(LookupTable);

        var tables = StaticDataService.Tables(sql.ConnectionString(database));

        Assert.Equal(
            [("app", "Lookup", 2L, true), ("app", "NoKey", 0L, false)],
            tables.Select(t => (t.Schema, t.Name, t.Rows, t.HasPrimaryKey)));
    }

    [Fact]
    public void StatusReportsRowsByKeyAndApplyMakesTheDatabaseMatch()
    {
        var database = sql.CreateDatabaseFromFolder(SqlServerFixture.DemoFolder);
        sql.Execute(database, "SET IDENTITY_INSERT [Sales].[Customer] ON; INSERT INTO [Sales].[Customer] ([CustomerId], [Name], [IsActive]) VALUES (1, N'Contoso', 0), (3, N'Northwind', 1);");
        var folder = WorkingFolder.Open(SqlServerFixture.DemoFolder);

        var data = Assert.Single(Status(folder, database).Data);

        Assert.Null(data.Problem);
        Assert.Equal("[Sales].[Customer]", data.Table);
        Assert.Equal((1, 1, 1), (data.New, data.Modified, data.Deleted));
        Assert.Equal(
            [(ObjectStatus.New, "3", ""), (ObjectStatus.Modified, "1", "IsActive"), (ObjectStatus.Deleted, "2", "")],
            data.Rows.Select(r => (r.Status, r.Key, string.Join(",", r.Columns))));

        var scripted = ApplyService.Apply(folder, sql.ConnectionString(database), [], all: true, data: true, scriptOnly: true);

        Assert.True(scripted.Problems.Count == 0, string.Join(Environment.NewLine, scripted.Problems));
        Assert.False(scripted.Applied);
        Assert.Contains("SET IDENTITY_INSERT [Sales].[Customer] ON", scripted.DataScript, StringComparison.Ordinal);
        Assert.DoesNotContain("DELETE", scripted.DataScript, StringComparison.Ordinal);
        Assert.Equal(2, Scalar(database, "SELECT COUNT(*) FROM [Sales].[Customer]"));

        var schemaOnly = ApplyService.Apply(folder, sql.ConnectionString(database), ["Sales.Customer"]);

        Assert.False(schemaOnly.Applied);
        Assert.Empty(schemaOnly.Data);
        Assert.Contains(schemaOnly.Problems, p => p.Contains("pass --data", StringComparison.Ordinal));
        Assert.Equal(2, Scalar(database, "SELECT COUNT(*) FROM [Sales].[Customer]"));

        var all = ApplyService.Apply(folder, sql.ConnectionString(database), [], all: true, data: true);

        Assert.True(all.Applied, string.Join(Environment.NewLine, all.Problems));
        Assert.Equal([("[Sales].[Customer]", 1, 1, 0)], all.Data.Select(d => (d.Table, d.Inserted, d.Updated, d.Deleted)));
        var afterAll = Assert.Single(Status(folder, database).Data);
        Assert.Equal((1, 0, 0), (afterAll.New, afterAll.Modified, afterAll.Deleted));

        var named = ApplyService.Apply(folder, sql.ConnectionString(database), ["Sales.Customer"], data: true);

        Assert.True(named.Applied, string.Join(Environment.NewLine, named.Problems));
        Assert.Equal([("[Sales].[Customer]", 0, 0, 1)], named.Data.Select(d => (d.Table, d.Inserted, d.Updated, d.Deleted)));
        Assert.False(Assert.Single(Status(folder, database).Data).HasChanges);
        Assert.Equal(1, Scalar(database, "SELECT COUNT(*) FROM [Sales].[Customer] WHERE CustomerId = 1 AND IsActive = 1"));
    }

    [Fact]
    public void DataNamesPickATablesRowsSeparatelyFromItsSchema()
    {
        var database = sql.CreateDatabaseFromFolder(SqlServerFixture.DemoFolder);
        sql.Execute(database, "ALTER TABLE [Sales].[Customer] ADD [Email] nvarchar(200) NULL");
        sql.Execute(database, "SET IDENTITY_INSERT [Sales].[Customer] ON; INSERT INTO [Sales].[Customer] ([CustomerId], [Name], [IsActive]) VALUES (3, N'Northwind', 1);");
        var folder = WorkingFolder.Open(SqlServerFixture.DemoFolder);

        var exported = ExportService.Export(folder, sql.ConnectionString(database), new ExportSelection([], Data: true, DataNames: ["Sales.Customer"]), dryRun: true);

        Assert.Empty(exported.Selected);
        Assert.Equal(["[Sales].[Customer]"], exported.SelectedData.Select(d => d.Table));
        Assert.All(exported.Files, f => Assert.StartsWith("Data", f.Path, StringComparison.Ordinal));

        var rowsOnly = ApplyService.Apply(folder, sql.ConnectionString(database), [], dataNames: ["Sales.Customer"]);

        Assert.True(rowsOnly.Applied, string.Join(Environment.NewLine, rowsOnly.Problems));
        Assert.Empty(rowsOnly.Objects);
        Assert.Equal([("[Sales].[Customer]", 2, 0, 1)], rowsOnly.Data.Select(d => (d.Table, d.Inserted, d.Updated, d.Deleted)));
        Assert.Equal(1, Scalar(database, "SELECT COUNT(*) FROM sys.columns WHERE name = 'Email'"));

        var schemaOnly = ApplyService.Apply(folder, sql.ConnectionString(database), ["Sales.Customer"], allowDataLoss: true, dataNames: []);

        Assert.True(schemaOnly.Applied, string.Join(Environment.NewLine, schemaOnly.Problems));
        Assert.Empty(schemaOnly.Data);
        Assert.Equal(0, Scalar(database, "SELECT COUNT(*) FROM sys.columns WHERE name = 'Email'"));
        Assert.Equal(2, Scalar(database, "SELECT COUNT(*) FROM [Sales].[Customer]"));
    }

    [Fact]
    public void LinkedTablesExportInKeyOrderAndRoundTripEveryType()
    {
        var database = CreateDatabase(LookupTable);
        var path = NewFolder();

        var linked = StaticDataService.Link(WorkingFolder.Open(path), sql.ConnectionString(database), ["app.Lookup", "app.NoKey", "app.Missing"]);

        Assert.Equal(["[app].[Lookup]"], linked.Tables.Select(t => t.Quoted));
        Assert.Equal(2, linked.Problems.Count);
        Assert.Contains(linked.Problems, p => p.Contains("primary key", StringComparison.Ordinal));
        var folder = WorkingFolder.Open(path);
        Assert.Equal(["app.Lookup_Data.sql"], folder.RedgateInfo!.DataFiles);
        var before = Assert.Single(Status(folder, database).Data);
        Assert.Equal((2, 0, 0), (before.New, before.Modified, before.Deleted));

        var exported = ExportService.Export(folder, sql.ConnectionString(database), new ExportSelection(["app.Lookup"], Data: true));

        Assert.True(exported.Problems.Count == 0, string.Join(Environment.NewLine, exported.Problems));
        Assert.Contains(exported.Files, f => f.Path == Path.Combine("Data", "app.Lookup_Data.sql") && f.Action == FileAction.Written);
        var script = File.ReadAllText(Path.Combine(path, "Data", "app.Lookup_Data.sql"));
        var lines = script.Split("\r\n");
        Assert.Equal("SET IDENTITY_INSERT [app].[Lookup] ON", lines[0]);
        Assert.StartsWith("INSERT INTO [app].[Lookup] ([Code], [Seq], [Id], [Name],", lines[1], StringComparison.Ordinal);
        Assert.Contains("VALUES ('a', 1, 2, NULL,", lines[1], StringComparison.Ordinal);
        Assert.Contains("VALUES ('b', 2, 1, N'O''Brien ünï \nline 2', 'trail  ', 'ab   ', 1234567890123456789012345678.0123456789, 0.1E0, 1.5E0, 12.3400, 1, 255, -9223372036854775808, '2024-02-29', '2024-01-02T03:04:05.997', '2024-01-02 03:04:05.1234567', '2024-01-02 03:04:05', '2024-01-02T03:04:00', '2024-01-02 03:04:05.123 +05:30', '13:14:15.1234', '", script, StringComparison.Ordinal);
        Assert.DoesNotContain("Shout", script, StringComparison.Ordinal);
        Assert.DoesNotContain("Version", script, StringComparison.Ordinal);
        Assert.EndsWith("SET IDENTITY_INSERT [app].[Lookup] OFF\r\n", script, StringComparison.Ordinal);
        Assert.False(Assert.Single(Status(folder, database).Data).HasChanges);

        sql.Execute(database, "DELETE FROM app.Lookup WHERE Code = 'b'; UPDATE app.Lookup SET Name = N'changed', Note = 'TRAIL' WHERE Code = 'a';");
        var changed = Assert.Single(Status(folder, database).Data);
        Assert.Equal((0, 1, 1), (changed.New, changed.Modified, changed.Deleted));
        Assert.Equal(["Name", "Note"], changed.Rows.Single(r => r.Status == ObjectStatus.Modified).Columns);

        var applied = ApplyService.Apply(folder, sql.ConnectionString(database), ["app.Lookup"], data: true);

        Assert.True(applied.Problems.Count == 0, string.Join(Environment.NewLine, applied.Problems));
        Assert.False(Assert.Single(Status(folder, database).Data).HasChanges);
        Assert.Equal(1, Scalar(database, "SELECT COUNT(*) FROM app.Lookup WHERE Code = 'b' AND Id = 1 AND Place.Lat = 51.5 AND Node.ToString() = '/1/2/'"));

        var unlinked = StaticDataService.Unlink(WorkingFolder.Open(path), ["[app].[Lookup]"]);

        Assert.Empty(unlinked.Problems);
        Assert.Empty(WorkingFolder.Open(path).RedgateInfo!.DataFiles);
        Assert.False(File.Exists(Path.Combine(path, "Data", "app.Lookup_Data.sql")));
    }

    [Fact]
    public void RowsAreAppliedInForeignKeyOrderAndAFailureRollsBackAllData()
    {
        var database = CreateDatabase("""
            CREATE TABLE dbo.Child (Id int NOT NULL CONSTRAINT PK_Child PRIMARY KEY, ParentId int NOT NULL);
            CREATE TABLE dbo.Parent (Id int NOT NULL CONSTRAINT PK_Parent PRIMARY KEY, Name nvarchar(10) NOT NULL);
            ALTER TABLE dbo.Child ADD CONSTRAINT FK_Child_Parent FOREIGN KEY (ParentId) REFERENCES dbo.Parent (Id);
            INSERT INTO dbo.Parent VALUES (1, N'one'), (2, N'two');
            INSERT INTO dbo.Child VALUES (10, 1), (20, 2);
            """);
        var path = NewFolder();
        Assert.Empty(StaticDataService.Link(WorkingFolder.Open(path), sql.ConnectionString(database), ["Child", "Parent"]).Problems);
        Assert.Empty(ExportService.Export(WorkingFolder.Open(path), sql.ConnectionString(database), new ExportSelection(["dbo.Child", "dbo.Parent"], Data: true)).Problems);
        var folder = WorkingFolder.Open(path);

        sql.Execute(database, "DELETE FROM dbo.Child; DELETE FROM dbo.Parent; INSERT INTO dbo.Parent VALUES (3, N'three'); INSERT INTO dbo.Child VALUES (30, 3);");
        var applied = ApplyService.Apply(folder, sql.ConnectionString(database), ["dbo.Child", "dbo.Parent"], data: true);

        Assert.True(applied.Applied, string.Join(Environment.NewLine, applied.Problems));
        Assert.Equal(
            [("[dbo].[Child]", 2, 0, 1), ("[dbo].[Parent]", 2, 0, 1)],
            applied.Data.Select(d => (d.Table, d.Inserted, d.Updated, d.Deleted)).Order());
        Assert.Equal(1, Scalar(database, "SELECT COUNT(*) FROM dbo.Child c JOIN dbo.Parent p ON p.Id = c.ParentId WHERE c.Id = 20 AND p.Name = N'two'"));

        File.AppendAllText(Path.Combine(path, "Data", "dbo.Child_Data.sql"), "INSERT INTO [dbo].[Child] ([Id], [ParentId]) VALUES (99, 99)\r\n");
        sql.Execute(database, "UPDATE dbo.Parent SET Name = N'renamed' WHERE Id = 1");
        var failed = ApplyService.Apply(folder, sql.ConnectionString(database), [], all: true, data: true);

        Assert.False(failed.Applied);
        Assert.Contains(failed.Problems, p => p.Contains("rolled back", StringComparison.Ordinal) && p.Contains("FK_Child_Parent", StringComparison.Ordinal));
        Assert.Equal(1, Scalar(database, "SELECT COUNT(*) FROM dbo.Parent WHERE Name = N'renamed'"));

        File.AppendAllText(Path.Combine(path, "Data", "dbo.Child_Data.sql"), "DELETE FROM [dbo].[Child]\r\n");
        var broken = Status(folder, database).Data.Single(d => d.Table == "[dbo].[Child]");
        Assert.Contains("only INSERT INTO", broken.Problem, StringComparison.Ordinal);
    }

    private StatusReport Status(WorkingFolder folder, string database) =>
        StatusService.GetStatus(folder, sql.ConnectionString(database), includeChangedBy: false);

    private string CreateDatabase(string script)
    {
        var file = Path.Combine(Path.GetTempPath(), $"sql-sc-{Guid.NewGuid():N}.sql");
        File.WriteAllText(file, script);
        try
        {
            return sql.CreateDatabaseFromScript(file);
        }
        finally
        {
            File.Delete(file);
        }
    }

    private static string NewFolder()
    {
        var path = Path.Combine(Path.GetTempPath(), "sql-sc-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    private int Scalar(string database, string query)
    {
        using var connection = new SqlConnection(sql.ConnectionString(database));
        connection.Open();
        using var command = new SqlCommand(query, connection);
        return (int)command.ExecuteScalar();
    }
}
