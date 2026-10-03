using Microsoft.SqlServer.Dac;
using Microsoft.SqlServer.Dac.Model;
using SqlSc.Core.Comparison;
using SqlSc.Core.Modeling;

namespace SqlSc.Core.Tests;

public sealed class DatabaseReferencesTests : IDisposable
{
    private readonly string directory = Path.Combine(Path.GetTempPath(), $"sql-sc-tests-{Guid.NewGuid():N}");

    public DatabaseReferencesTests() => Directory.CreateDirectory(directory);

    public void Dispose() => Directory.Delete(directory, recursive: true);

    [Fact]
    public void ObjectsWhoseNameTheFolderUsesForAnotherTypeAreNotCopied()
    {
        using var folder = Model("CREATE VIEW dbo.Customer AS SELECT 1 AS Id", "CREATE VIEW dbo.Report AS SELECT Id FROM dbo.Customer");
        using var database = Model("CREATE TABLE dbo.Customer (Id int NOT NULL)", "CREATE TABLE dbo.Orders (Id int NOT NULL)");

        var borrowed = DatabaseReferences.AddMissing(folder, database);

        Assert.Equal(["[dbo].[Orders]"], borrowed.Objects.Select(o => o.Name.ToString()));
        Build(folder);
    }

    [Fact]
    public void ObjectsThatDontResolveAgainstTheFolderAreLeftOut()
    {
        using var folder = Model("CREATE SCHEMA info", "CREATE TABLE info.Instructions (Id int NOT NULL)");
        using var database = Model(
            "CREATE SCHEMA info",
            "CREATE TABLE info.Instructions (Id int NOT NULL, Isdelta bit NULL)",
            "CREATE VIEW info.processing_report AS SELECT i.Isdelta AS is_delta FROM info.Instructions i",
            "CREATE VIEW info.report_summary AS SELECT is_delta FROM info.processing_report",
            "CREATE TABLE dbo.Orders (Id int NOT NULL)");

        var borrowed = DatabaseReferences.AddMissing(folder, database);

        Assert.Equal(["[dbo].[Orders]"], borrowed.Objects.Select(o => o.Name.ToString()));
        Assert.DoesNotContain(folder.GetObjects(DacQueryScopes.UserDefined, ModelSchema.View), v => v.Name.Parts[0] == "info");
        Build(folder);
    }

    [Theory]
    [InlineData("CREATE USER [reader] FOR LOGIN [reader]")]
    [InlineData("CREATE USER [reader] FROM EXTERNAL PROVIDER")]
    public void UsersWhoseLoginIsMissingCanBePackaged(string user)
    {
        using var folder = Model(user);
        using var database = Model();

        DatabaseReferences.AddMissing(folder, database);

        Build(folder);
    }

    private static TSqlModel Model(params string[] scripts)
    {
        var model = SystemDatabase.CreateModel(SqlServerVersion.SqlAzure, new TSqlModelOptions());
        for (var i = 0; i < scripts.Length; i++)
        {
            model.AddOrUpdateObjects(scripts[i], $"{i}.sql", new TSqlObjectOptions());
        }

        return model;
    }

    private void Build(TSqlModel model) =>
        DacPackageExtensions.BuildPackage(Path.Combine(directory, "model.dacpac"), model, new PackageMetadata { Name = "test" });
}
