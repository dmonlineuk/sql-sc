using SqlSc.Core.WorkingFolders;

namespace SqlSc.Core.Tests;

public class WorkingFolderTests
{
    [Fact]
    public void ReadsRedgateDatabaseInfo()
    {
        var folder = WorkingFolder.Open(FixturePaths.Demo);

        Assert.NotNull(folder.RedgateInfo);
        Assert.Equal("SQL_Latin1_General_CP1_CI_AS", folder.RedgateInfo.DefaultCollation);
        Assert.Equal(16, folder.RedgateInfo.DatabaseVersion);
        Assert.False(folder.RedgateInfo.IsAzure);
        Assert.Equal("Data", folder.DataFolder);
        Assert.Equal(["Sales.Customer_Data.sql"], folder.RedgateInfo.DataFiles);
    }

    [Fact]
    public void SchemaScriptsExcludeStaticData()
    {
        var scripts = WorkingFolder.Open(FixturePaths.Demo).GetSchemaScripts();

        Assert.Equal(5, scripts.Count);
        Assert.DoesNotContain(scripts, s => s.StartsWith("Data", StringComparison.Ordinal));
        Assert.Contains(Path.Combine("Stored Procedures", "Sales.GetCustomer.sql"), scripts);
    }

    [Fact]
    public void FolderWithoutRedgateInfoUsesDefaults()
    {
        var root = FixturePaths.CreateTempFolder(("Tables/dbo.T.sql", "CREATE TABLE dbo.T (Id int)"));

        var folder = WorkingFolder.Open(root);

        Assert.Null(folder.RedgateInfo);
        Assert.Equal("Data", folder.DataFolder);
        Assert.Single(folder.GetSchemaScripts());
    }

    [Fact]
    public void MissingFolderThrows()
    {
        Assert.Throws<DirectoryNotFoundException>(() => WorkingFolder.Open(Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"))));
    }
}
