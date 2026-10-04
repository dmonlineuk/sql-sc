using SqlSc.Core.Export;
using SqlSc.Core.WorkingFolders;

namespace SqlSc.Core.Tests;

public class ExportFormattingTests
{
    [Theory]
    [InlineData("Table", "Sales", "Customer", @"Tables\Sales.Customer.sql")]
    [InlineData("Procedure", "Sales", "GetCustomer", @"Stored Procedures\Sales.GetCustomer.sql")]
    [InlineData("TableValuedFunction", "dbo", "f", @"Functions\dbo.f.sql")]
    [InlineData("TableType", "dbo", "IdList", @"Types\User-defined Data Types\dbo.IdList.sql")]
    [InlineData("ExternalTable", "ext", "T", @"Tables\External Tables\ext.T.sql")]
    [InlineData("Table", "a/b", "c:d", @"Tables\a%2Fb.c%3Ad.sql")]
    public void MapsSchemaObjectsToRedgateFolders(string type, string schema, string name, string expected) =>
        Assert.Equal(expected.Replace('\\', Path.DirectorySeparatorChar), RedgateLayout.PathFor(type, [schema, name]));

    [Fact]
    public void MapsDatabaseObjectsAndUsesTheFoldersPrefixes()
    {
        Assert.Equal(Path.Combine("Security", "Roles", "app_role.sql"), RedgateLayout.PathFor("Role", ["app_role"]));
        Assert.Equal(Path.Combine("Procs", "dbo.p.sql"), RedgateLayout.PathFor("Procedure", ["dbo", "p"], new Dictionary<string, string> { ["StoredProcedure"] = "Procs" }));
        Assert.Null(RedgateLayout.PathFor("Column", ["dbo", "t", "c"]));
        Assert.Null(RedgateLayout.PathFor("MasterKey", ["x"]));
    }

    [Fact]
    public void ReadsPrefixesFromRedgateDatabaseInfo()
    {
        var info = RedgateDatabaseInfo.Load(Path.Combine(FixturePaths.Demo, "RedGateDatabaseInfo.xml"));

        Assert.NotNull(info.Prefixes);
        Assert.Equal(Path.Combine("Security", "Users"), info.Prefixes["User"]);
        Assert.False(info.Prefixes.ContainsKey("Trigger"));
    }

    [Theory]
    [InlineData("Sales.Customer", "[Sales].[Customer]")]
    [InlineData("[Sales].[Customer]", "[Sales].[Customer]")]
    [InlineData("[My Schema].[A.B]", "[My Schema].[A.B]")]
    [InlineData("app_role", "[app_role]")]
    public void FormatsObjectNamesAsStatusDoes(string name, string expected) => Assert.Equal(expected, ExportService.FormatName(name));
}
