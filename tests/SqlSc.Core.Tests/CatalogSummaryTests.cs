using SqlSc.Core.Database;
using SqlSc.Core.Filtering;

namespace SqlSc.Core.Tests;

public class CatalogSummaryTests
{
    private const string Scpf = """
        <?xml version="1.0" encoding="utf-8" standalone="yes"?>
        <NamedFilter version="1" type="SQLCompareFilter">
          <Filter version="1" type="DifferenceFilter">
            <FilterCaseSensitive>False</FilterCaseSensitive>
            <Filters version="1">
              <None version="1">
                <Include>False</Include>
                <Expression>@SCHEMA LIKE 'staging%'</Expression>
              </None>
              <StoredProcedure version="1">
                <Include>True</Include>
                <Expression>@NAME NOT LIKE 'tmp%'</Expression>
              </StoredProcedure>
            </Filters>
          </Filter>
        </NamedFilter>
        """;

    private static readonly CatalogSummary Catalog = new(
        [
            new("Table", "dbo", "Customer"),
            new("Table", "dbo", "Order"),
            new("Table", "staging_load", "Customer"),
            new("Procedure", "dbo", "GetCustomer"),
            new("Procedure", "dbo", "tmpFix"),
            new("Schema", null, "staging_load"),
            new("User", null, "app_reader"),
        ],
        new ChildCounts(10, 2, 3, 0, 0, 1, 0, 0),
        ModuleBytes: 100,
        Elapsed: TimeSpan.Zero);

    [Fact]
    public void TrackedAppliesTheFilterToSchemaScopedAndTopLevelObjects()
    {
        var filter = ObjectFilter.LoadScpf(Path.Combine(FixturePaths.CreateTempFolder(("Filter.scpf", Scpf)), "Filter.scpf"));

        var tracked = Catalog.Tracked(filter).Select(o => $"{o.ObjectType} {o.Schema}.{o.Name}");

        Assert.Equal(["Table dbo.Customer", "Table dbo.Order", "Procedure dbo.GetCustomer", "User .app_reader"], tracked);
    }

    [Fact]
    public void TrackedKeepsEverythingWithoutAFilter() =>
        Assert.Equal(Catalog.Objects.Count, Catalog.Tracked(ObjectFilter.IncludeAll).Count);

    [Fact]
    public void LargestCountsByKeyAndSkipsNulls()
    {
        Assert.Equal([("Table", 3), ("Procedure", 2), ("Schema", 1)], CatalogSummary.Largest(Catalog.Objects, o => o.ObjectType, 3));
        Assert.Equal([("dbo", 4), ("staging_load", 1)], CatalogSummary.Largest(Catalog.Objects, o => o.Schema, 5));
    }
}
