using Microsoft.SqlServer.Dac;
using Microsoft.SqlServer.Dac.Model;
using SqlSc.Core.Modeling;

namespace SqlSc.Core.Tests;

public sealed class SystemDatabaseTests : IDisposable
{
    private readonly string directory = Path.Combine(Path.GetTempPath(), $"sql-sc-tests-{Guid.NewGuid():N}");

    public SystemDatabaseTests() => Directory.CreateDirectory(directory);

    public void Dispose() => Directory.Delete(directory, recursive: true);

    [Theory]
    [InlineData(SqlServerVersion.Sql160)]
    [InlineData(SqlServerVersion.SqlAzure)]
    public void ModelsResolveSystemViews(SqlServerVersion platform)
    {
        using var model = SystemDatabase.CreateModel(platform, new TSqlModelOptions());
        model.AddOrUpdateObjects(
            "CREATE VIEW dbo.ColumnInfo AS SELECT cols.COLUMN_NAME, c.column_id FROM INFORMATION_SCHEMA.COLUMNS AS cols JOIN sys.columns AS c ON c.name = cols.COLUMN_NAME",
            "a.sql",
            new TSqlObjectOptions());
        model.AddOrUpdateObjects(
            "CREATE VIEW dbo.SelfReference AS SELECT t.TABLE_NAME FROM [This-Db].INFORMATION_SCHEMA.TABLES AS t",
            "b.sql",
            new TSqlObjectOptions());

        Assert.DoesNotContain(model.Validate(), m => m.MessageType == DacMessageType.Error);
        var path = Path.Combine(directory, "model.dacpac");
        DacPackageExtensions.BuildPackage(path, model, new PackageMetadata { Name = "test" });
        Assert.True(File.Exists(path));
    }

    [Fact]
    public void UsersWithoutTheirLoginGetOne()
    {
        using var model = SystemDatabase.CreateModel(SqlServerVersion.SqlAzure, new TSqlModelOptions());
        model.AddOrUpdateObjects("CREATE USER [app]] reader] FOR LOGIN [app]] reader]", "a.sql", new TSqlObjectOptions());

        SystemDatabase.AddMissingLogins(model);

        Assert.NotNull(model.GetObject(ModelSchema.Login, new ObjectIdentifier("app] reader"), DacQueryScopes.UserDefined));
        DacPackageExtensions.BuildPackage(Path.Combine(directory, "model.dacpac"), model, new PackageMetadata { Name = "test" });
    }
}
