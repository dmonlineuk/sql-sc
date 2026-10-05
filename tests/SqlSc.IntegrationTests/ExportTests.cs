using System.Diagnostics;
using SqlSc.Core.Comparison;
using SqlSc.Core.Export;
using SqlSc.Core.Modeling;
using SqlSc.Core.WorkingFolders;

namespace SqlSc.IntegrationTests;

[Collection(SqlServerGroup.Name)]
public class ExportTests(SqlServerFixture sql)
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ExportingARichDatabaseIntoAnEmptyFolderRoundTrips(bool fullExtract)
    {
        var database = sql.CreateDatabaseFromScript(SqlServerFixture.RichScript);
        sql.Execute(database, "DROP EXTERNAL DATA SOURCE [Rich Blobs]; DROP DATABASE SCOPED CREDENTIAL [Rich Credential]; DROP MASTER KEY");
        var folder = NewFolder();

        var exported = ExportService.Export(WorkingFolder.Open(folder), sql.ConnectionString(database), new ExportSelection([], All: true), fullExtract);

        Assert.True(exported.Problems.Count == 0, string.Join(Environment.NewLine, exported.Problems));
        Assert.NotEmpty(exported.Files);
        Assert.All(exported.Files, f => Assert.Equal(FileAction.Written, f.Action));
        var report = StatusService.GetStatus(WorkingFolder.Open(folder), sql.ConnectionString(database), includeChangedBy: false, fullExtract);
        Assert.True(report.Changes.Count == 0, string.Join(Environment.NewLine, report.Changes));
        Assert.DoesNotContain(report.LoadIssues, i => i.Severity == IssueSeverity.Error);

        var function = File.ReadAllText(Path.Combine(folder, "Functions", "app.CheckDigits.sql"));
        Assert.StartsWith("SET QUOTED_IDENTIFIER ON\r\nGO\r\nSET ANSI_NULLS ON\r\nGO\r\nCREATE FUNCTION app.CheckDigits", function, StringComparison.Ordinal);
        Assert.Contains("n(n)", function, StringComparison.Ordinal);
        Assert.DoesNotContain("~]", function, StringComparison.Ordinal);
        Assert.DoesNotContain('\n', function.Replace("\r\n", "", StringComparison.Ordinal));
        Assert.EndsWith("\r\nGO\r\n", function, StringComparison.Ordinal);
        Assert.Contains("SET ANSI_NULLS OFF\r\nGO\r\n", File.ReadAllText(Path.Combine(folder, "Views", "app.AnsiOff.sql")), StringComparison.Ordinal);
        var table = File.ReadAllText(Path.Combine(folder, "Tables", "app.Customer.sql"));
        Assert.StartsWith("CREATE TABLE [app].[Customer]", table, StringComparison.Ordinal);
        Assert.Contains("GRANT SELECT", table, StringComparison.Ordinal);
        Assert.Contains("CREATE TRIGGER app.Customer_Audit", table, StringComparison.Ordinal);
        Assert.Contains("sp_addextendedproperty", table, StringComparison.Ordinal);
        Assert.True(File.Exists(Path.Combine(folder, "Security", "Schemas", "app.sql")));
        Assert.True(File.Exists(Path.Combine(folder, "Security", "Roles", "app_role.sql")));
    }

    [Fact]
    public void ExportsOnlyTheNamedObjectsAndDeletesDroppedOnes()
    {
        var folder = CopyDemo();
        var database = sql.CreateDatabaseFromFolder(folder);
        sql.Execute(database, "ALTER PROCEDURE [Sales].[GetCustomer] @CustomerId int AS SELECT Name FROM Sales.Customer WHERE CustomerId = @CustomerId");
        sql.Execute(database, "CREATE TABLE [Sales].[Order] ([OrderId] int NOT NULL CONSTRAINT [PK_Order] PRIMARY KEY)");
        sql.Execute(database, "DROP VIEW [Sales].[ActiveCustomer]");
        var view = Path.Combine(folder, "Views", "Sales.ActiveCustomer.sql");
        var table = Path.Combine(folder, "Tables", "Sales.Customer.sql");
        var tableBefore = File.ReadAllText(table);

        var exported = ExportService.Export(WorkingFolder.Open(folder), sql.ConnectionString(database), new ExportSelection(["Sales.GetCustomer", "[Sales].[ActiveCustomer]"]));

        Assert.True(exported.Problems.Count == 0, string.Join(Environment.NewLine, exported.Problems));
        Assert.Equal(
            [(Path.Combine("Stored Procedures", "Sales.GetCustomer.sql"), FileAction.Written), (Path.Combine("Views", "Sales.ActiveCustomer.sql"), FileAction.Deleted)],
            exported.Files.Select(f => (f.Path, f.Action)).Order());
        Assert.False(File.Exists(view));
        Assert.Contains("SELECT Name FROM Sales.Customer", File.ReadAllText(Path.Combine(folder, "Stored Procedures", "Sales.GetCustomer.sql")), StringComparison.Ordinal);
        Assert.Equal(tableBefore, File.ReadAllText(table));
        Assert.False(File.Exists(Path.Combine(folder, "Tables", "Sales.Order.sql")));

        var all = ExportService.Export(WorkingFolder.Open(folder), sql.ConnectionString(database), new ExportSelection([], All: true));

        Assert.Equal([(Path.Combine("Tables", "Sales.Order.sql"), FileAction.Written)], all.Files.Select(f => (f.Path, f.Action)));
        var report = StatusService.GetStatus(WorkingFolder.Open(folder), sql.ConnectionString(database), includeChangedBy: false);
        Assert.True(report.Changes.Count == 0, string.Join(Environment.NewLine, report.Changes));
    }

    [Fact]
    public void DryRunShowsDiffsAndChangesNothing()
    {
        var folder = CopyDemo();
        var database = sql.CreateDatabaseFromFolder(folder);
        sql.Execute(database, "ALTER PROCEDURE [Sales].[GetCustomer] @CustomerId int AS SELECT Name FROM Sales.Customer WHERE CustomerId = @CustomerId");
        sql.Execute(database, "CREATE TABLE [Sales].[Order] ([OrderId] int NOT NULL CONSTRAINT [PK_Order] PRIMARY KEY)");
        sql.Execute(database, "DROP VIEW [Sales].[ActiveCustomer]");
        var before = Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories).ToDictionary(f => f, File.ReadAllText);

        var exported = ExportService.Export(WorkingFolder.Open(folder), sql.ConnectionString(database), new ExportSelection([], All: true), dryRun: true);

        Assert.True(exported.Problems.Count == 0, string.Join(Environment.NewLine, exported.Problems));
        var files = exported.Files.ToDictionary(f => f.Path.Replace('\\', '/'));
        Assert.Equal(FileAction.Written, files["Stored Procedures/Sales.GetCustomer.sql"].Action);
        Assert.Contains("+++ b/Stored Procedures/Sales.GetCustomer.sql\n", files["Stored Procedures/Sales.GetCustomer.sql"].Diff, StringComparison.Ordinal);
        Assert.Contains("\n+", files["Stored Procedures/Sales.GetCustomer.sql"].Diff, StringComparison.Ordinal);
        Assert.Contains("\n-", files["Stored Procedures/Sales.GetCustomer.sql"].Diff, StringComparison.Ordinal);
        Assert.StartsWith("--- /dev/null\n+++ b/Tables/Sales.Order.sql\n@@ -0,0 ", files["Tables/Sales.Order.sql"].Diff, StringComparison.Ordinal);
        Assert.Contains("+CREATE TABLE [Sales].[Order]", files["Tables/Sales.Order.sql"].Diff, StringComparison.Ordinal);
        Assert.Equal(FileAction.Deleted, files["Views/Sales.ActiveCustomer.sql"].Action);
        Assert.StartsWith("--- a/Views/Sales.ActiveCustomer.sql\n+++ /dev/null\n", files["Views/Sales.ActiveCustomer.sql"].Diff, StringComparison.Ordinal);
        Assert.Equal(before, Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories).ToDictionary(f => f, File.ReadAllText));

        var written = ExportService.Export(WorkingFolder.Open(folder), sql.ConnectionString(database), new ExportSelection([], All: true));

        Assert.Equal(exported.Files.Select(f => (f.Path, f.Action)), written.Files.Select(f => (f.Path, f.Action)));
        Assert.All(written.Files, f => Assert.Null(f.Diff));
    }

    [Fact]
    public void ReportsNamesWithNoDifferences()
    {
        var folder = CopyDemo();
        var database = sql.CreateDatabaseFromFolder(folder);

        var exported = ExportService.Export(WorkingFolder.Open(folder), sql.ConnectionString(database), new ExportSelection(["Sales.Missing"]));

        Assert.Empty(exported.Files);
        Assert.Equal(["Sales.Missing: no differences between the database and the folder."], exported.Problems);
    }

    [Fact]
    public void CommitStagesOnlyTheExportedFiles()
    {
        var folder = CopyDemo();
        RunGit(folder, "init", "--quiet");
        RunGit(folder, "add", "--all");
        RunGit(folder, "commit", "--quiet", "-m", "initial");
        var database = sql.CreateDatabaseFromFolder(folder);
        sql.Execute(database, "ALTER PROCEDURE [Sales].[GetCustomer] @CustomerId int AS SELECT Name FROM Sales.Customer WHERE CustomerId = @CustomerId");
        sql.Execute(database, "DROP VIEW [Sales].[ActiveCustomer]");
        sql.Execute(database, "CREATE TABLE [Sales].[Order] ([OrderId] int NOT NULL CONSTRAINT [PK_Order] PRIMARY KEY)");
        File.WriteAllText(Path.Combine(folder, "notes.txt"), "staged but not part of the commit");
        RunGit(folder, "add", "notes.txt");
        File.AppendAllText(Path.Combine(folder, "Data", "Sales.Customer_Data.sql"), "-- local edit\r\n");

        var result = CommitService.Commit(
            WorkingFolder.Open(folder),
            sql.ConnectionString(database),
            new ExportSelection(["Sales.GetCustomer", "Sales.ActiveCustomer"]),
            "Change GetCustomer, drop ActiveCustomer");

        Assert.True(result.Export.Problems.Count == 0, string.Join(Environment.NewLine, result.Export.Problems));
        Assert.NotNull(result.Commit);
        Assert.Equal(
            ["D\tViews/Sales.ActiveCustomer.sql", "M\tStored Procedures/Sales.GetCustomer.sql"],
            RunGit(folder, "show", "--name-status", "--format=", "HEAD").Split('\n', StringSplitOptions.RemoveEmptyEntries).Order(StringComparer.Ordinal));
        Assert.Equal("Change GetCustomer, drop ActiveCustomer", RunGit(folder, "log", "-1", "--format=%s"));
        Assert.Equal(
            [" M Data/Sales.Customer_Data.sql", "A  notes.txt"],
            RunGit(folder, "status", "--porcelain").Split('\n', StringSplitOptions.RemoveEmptyEntries).Order(StringComparer.Ordinal));

        var again = CommitService.Commit(WorkingFolder.Open(folder), sql.ConnectionString(database), new ExportSelection(["Sales.GetCustomer"]), "again");

        Assert.Null(again.Commit);
    }

    [Fact]
    public void CommitOutsideAGitRepositoryFails()
    {
        var folder = CopyDemo();

        Assert.Throws<InvalidDataException>(() =>
            CommitService.Commit(WorkingFolder.Open(folder), sql.ConnectionString("master"), new ExportSelection([], All: true), "message"));
    }

    private static string RunGit(string folder, params string[] arguments)
    {
        foreach (var (name, value) in new[] { ("GIT_AUTHOR_NAME", "test"), ("GIT_AUTHOR_EMAIL", "test@example.com"), ("GIT_COMMITTER_NAME", "test"), ("GIT_COMMITTER_EMAIL", "test@example.com") })
        {
            Environment.SetEnvironmentVariable(name, value);
        }

        var start = new ProcessStartInfo("git") { WorkingDirectory = folder, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var argument in arguments)
        {
            start.ArgumentList.Add(argument);
        }

        using var process = Process.Start(start)!;
        var output = process.StandardOutput.ReadToEnd();
        var error = process.StandardError.ReadToEnd();
        process.WaitForExit();
        Assert.True(process.ExitCode == 0, $"git {string.Join(' ', arguments)}: {error}");
        return output.TrimEnd();
    }

    private static string NewFolder()
    {
        var target = Path.Combine(Path.GetTempPath(), "sql-sc-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(target);
        return target;
    }

    private static string CopyDemo()
    {
        var target = NewFolder();
        foreach (var file in Directory.EnumerateFiles(SqlServerFixture.DemoFolder, "*", SearchOption.AllDirectories))
        {
            var destination = Path.Combine(target, Path.GetRelativePath(SqlServerFixture.DemoFolder, file));
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            File.Copy(file, destination);
        }

        return target;
    }
}
