using Microsoft.SqlServer.Dac;
using Microsoft.SqlServer.Dac.Model;
using SqlSc.Core.Modeling;

namespace SqlSc.Core.Tests;

public class ColumnNamedAliasesTests
{
    private const string NhsCheck = """
        CREATE FUNCTION dq.InvalidNumber (@No varchar(20))
        RETURNS TABLE
        AS RETURN (
            WITH num AS (
                SELECT n.n FROM (VALUES (1),(2),(3),(4),(5),(6),(7),(8),(9)) n(n)
            )
            SELECT CASE WHEN COUNT(1) <> 9 THEN 1 ELSE 0 END AS isInvalid
            FROM num
            WHERE CAST(SUBSTRING(@No, num.n, 1) AS tinyint) * (11 - num.n) >= 0
        )
        """;

    [Theory]
    [InlineData(
        "SELECT n.n FROM (VALUES (1),(2)) n(n)",
        "SELECT [n~].n FROM (VALUES (1),(2)) [n~](n)")]
    [InlineData(
        "SELECT N.a, n.n, n.* FROM (VALUES (1, 2)) AS [n](a, n) WHERE n.n > 1",
        "SELECT [n~].a, [n~].n, [n~].* FROM (VALUES (1, 2)) AS [n~](a, n) WHERE [n~].n > 1")]
    [InlineData(
        "SELECT j.j FROM OPENJSON(@x) WITH (j int) AS j",
        "SELECT [j~].j FROM OPENJSON(@x) WITH (j int) AS [j~]")]
    [InlineData(
        "WITH n AS (SELECT 1 AS a) SELECT n.a, v.n FROM n CROSS JOIN (VALUES (1)) n2(n) CROSS JOIN (VALUES (2)) v(v)",
        "WITH n AS (SELECT 1 AS a) SELECT n.a, [v~].n FROM n CROSS JOIN (VALUES (1)) n2(n) CROSS JOIN (VALUES (2)) [v~](v)")]
    [InlineData(
        "WITH n AS (SELECT 1 AS n) SELECT n.n, n2.n2 FROM n CROSS JOIN (VALUES (1)) n2(n2)",
        "WITH n AS (SELECT 1 AS n) SELECT n.n, [n2~].n2 FROM n CROSS JOIN (VALUES (1)) [n2~](n2)")]
    [InlineData(
        "WITH n AS (SELECT 1 AS a) SELECT n.a FROM n CROSS JOIN (VALUES (1)) x(n) CROSS JOIN (VALUES (2)) AS n2(n) WHERE EXISTS (SELECT n.n FROM (VALUES (3)) n(n))",
        "WITH [n~] AS (SELECT 1 AS a) SELECT [n~].a FROM [n~] CROSS JOIN (VALUES (1)) x(n) CROSS JOIN (VALUES (2)) AS n2(n) WHERE EXISTS (SELECT [n~].n FROM (VALUES (3)) [n~](n))")]
    [InlineData(
        "SELECT n.n FROM (VALUES (1)) n(n)\nGO\nSELECT n.x FROM dbo.T AS n",
        "SELECT [n~].n FROM (VALUES (1)) [n~](n)\nGO\nSELECT n.x FROM dbo.T AS n")]
    public void RenamesAliasesThatMatchAColumnName(string script, string expected)
    {
        Assert.Equal(expected, ColumnNamedAliases.Rewrite(script));
    }

    [Theory]
    [InlineData("SELECT x.n FROM (VALUES (1)) x(n)")]
    [InlineData("SELECT n.n FROM dbo.T AS n")]
    [InlineData("SELECT n.n FROM n CROSS JOIN (VALUES (1)) n(n)")]
    [InlineData("SELECT dbo.n.n FROM (VALUES (1)) n(n)")]
    [InlineData("SELECT n.n FROM (VALUES (1)) n(n")]
    public void LeavesOtherScriptsAlone(string script)
    {
        Assert.Same(script, ColumnNamedAliases.Rewrite(script));
    }

    [Fact]
    public void ValuesAliasedLikeTheirColumnCanBePackaged()
    {
        using var model = SystemDatabase.CreateModel(SqlServerVersion.SqlAzure, new TSqlModelOptions());
        model.AddOrUpdateObjects("CREATE SCHEMA dq", "a.sql", new TSqlObjectOptions());
        model.AddOrUpdateObjects(ColumnNamedAliases.Rewrite(NhsCheck), "b.sql", new TSqlObjectOptions());

        Assert.DoesNotContain(model.Validate(), m => m.MessageType == DacMessageType.Error);
        var package = Path.Combine(FixturePaths.CreateTempFolder(), "model.dacpac");
        DacPackageExtensions.BuildPackage(package, model, new PackageMetadata { Name = "test" });
        Assert.True(File.Exists(package));
    }

    [Fact]
    public void ApplyRewritesModulesInAPackage()
    {
        var folder = FixturePaths.CreateTempFolder();
        using (var model = SystemDatabase.CreateModel(SqlServerVersion.SqlAzure, new TSqlModelOptions()))
        {
            model.AddOrUpdateObjects("CREATE SCHEMA dq", "a.sql", new TSqlObjectOptions());
            model.AddOrUpdateObjects(NhsCheck, "[dq].[InvalidNumber]", new TSqlObjectOptions());
            ColumnNamedAliases.Apply(model);

            Assert.DoesNotContain(model.Validate(), m => m.MessageType == DacMessageType.Error);
            var function = Assert.Single(model.GetObjects(DacQueryScopes.UserDefined, ModelSchema.TableValuedFunction));
            Assert.Contains("[n~](n)", function.GetScript(), StringComparison.Ordinal);
            DacPackageExtensions.BuildPackage(Path.Combine(folder, "model.dacpac"), model, new PackageMetadata { Name = "test" });
        }
    }
}
