using SqlSc.Core.Filtering;
using SqlSc.Core.WorkingFolders;

namespace SqlSc.Core.Tests;

public class ObjectFilterTests
{
    private const string Scpf = """
        <?xml version="1.0" encoding="utf-8" standalone="yes"?>
        <NamedFilter version="1" type="SQLCompareFilter">
          <Filter version="1" type="DifferenceFilter">
            <FilterCaseSensitive>False</FilterCaseSensitive>
            <Filters version="1">
              <None version="1">
                <Include>False</Include>
                <Expression>(@SCHEMA = 'test') OR (@SCHEMA LIKE 'staging%') OR (@SCHEMA LIKE 'INFORMATION[_]SCHEMA%') OR ((@SCHEMA LIKE 'Reference%') AND (@NAME != 'Customers'))</Expression>
              </None>
              <StoredProcedure version="1">
                <Include>True</Include>
                <Expression>@NAME NOT LIKE 'tmp%'</Expression>
              </StoredProcedure>
              <Table version="1">
                <Include>True</Include>
                <Expression>TRUE</Expression>
              </Table>
              <ExternalTable version="1">
                <Include>True</Include>
                <Expression />
              </ExternalTable>
              <Synonym version="1">
                <Include>False</Include>
                <Expression>TRUE</Expression>
              </Synonym>
            </Filters>
          </Filter>
        </NamedFilter>
        """;

    private static ObjectFilter Filter => ObjectFilter.LoadScpf(Path.Combine(FixturePaths.CreateTempFolder(("Filter.scpf", Scpf)), "Filter.scpf"));

    [Theory]
    [InlineData("Table", "dbo", "Customer", true)]
    [InlineData("Table", "test", "Customer", false)]
    [InlineData("Table", "TEST", "Customer", false)]
    [InlineData("View", "staging_2024", "Load", false)]
    [InlineData("View", "stagingX", "Load", false)]
    [InlineData("Table", "INFORMATION_SCHEMA_X", "T", false)]
    [InlineData("Table", "INFORMATIONxSCHEMA", "T", true)]
    [InlineData("Table", "Reference", "Customers", true)]
    [InlineData("Table", "ReferenceData", "Other", false)]
    [InlineData("Procedure", "dbo", "GetCustomer", true)]
    [InlineData("Procedure", "dbo", "tmpFix", false)]
    [InlineData("ExternalTable", "dbo", "Ext", true)]
    [InlineData("Synonym", "dbo", "S", false)]
    public void AppliesGlobalAndTypeRules(string type, string schema, string name, bool included)
    {
        Assert.Equal(included, Filter.Includes(type, [schema, name]));
    }

    [Fact]
    public void SchemaObjectsAreFilteredByTheirOwnName()
    {
        Assert.False(Filter.Includes("Schema", ["test"]));
        Assert.True(Filter.Includes("Schema", ["dbo"]));
        Assert.True(Filter.Includes("User", ["app_reader"]));
    }

    [Theory]
    [InlineData("@SCHEMA = 'O''Brien'", "O'Brien", "x", true)]
    [InlineData("NOT (@SCHEMA = 'a')", "a", "x", false)]
    [InlineData("@SCHEMA <> 'a' AND @NAME LIKE '[ab]_c'", "b", "axc", true)]
    [InlineData("@SCHEMA <> 'a' AND @NAME LIKE '[^ab]%'", "b", "axc", false)]
    [InlineData("FALSE OR @NAME = 'x'", "s", "x", true)]
    public void ParsesExpressions(string expression, string schema, string name, bool expected)
    {
        Assert.Equal(expected, FilterExpression.Parse(expression).Matches(schema, name));
    }

    [Theory]
    [InlineData("@OWNER = 'x'")]
    [InlineData("@SCHEMA = 'x")]
    [InlineData("(@SCHEMA = 'x'")]
    [InlineData("@SCHEMA > 'x'")]
    public void RejectsUnsupportedExpressions(string expression)
    {
        Assert.Throws<FormatException>(() => FilterExpression.Parse(expression));
    }

    [Fact]
    public void WorkingFolderUsesFilterScpfByDefault()
    {
        var root = FixturePaths.CreateTempFolder(("Filter.scpf", Scpf));

        var folder = WorkingFolder.Open(root);

        Assert.Equal("Filter.scpf", folder.FilterPath);
        Assert.False(folder.Filter.Includes("Table", ["test", "T"]));
    }

    [Fact]
    public void FolderWithoutFilterIncludesEverything()
    {
        var folder = WorkingFolder.Open(FixturePaths.CreateTempFolder());

        Assert.Null(folder.FilterPath);
        Assert.True(folder.Filter.IsEmpty);
    }
}
