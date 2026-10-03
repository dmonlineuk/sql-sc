using Microsoft.SqlServer.Dac.Model;
using SqlSc.Core.Diagnostics;
using SqlSc.Core.Filtering;
using SqlSc.Core.Modeling;

namespace SqlSc.Core.Tests;

public class BrokenObjectsTests
{
    private const string Scpf = """
        <?xml version="1.0" encoding="utf-8" standalone="yes"?>
        <NamedFilter version="1" type="SQLCompareFilter">
          <Filter version="1" type="DifferenceFilter">
            <Filters version="1">
              <None version="1">
                <Include>False</Include>
                <Expression>@SCHEMA LIKE 'staging%'</Expression>
              </None>
            </Filters>
          </Filter>
        </NamedFilter>
        """;

    private static ObjectFilter StagingExcluded => ObjectFilter.LoadScpf(Path.Combine(FixturePaths.CreateTempFolder(("Filter.scpf", Scpf)), "Filter.scpf"));

    [Fact]
    public void RemovesUntrackedObjectsWithUnresolvedReferencesAndTheirUntrackedUsers()
    {
        using var model = SystemDatabase.CreateModel(SqlServerVersion.Sql160, new TSqlModelOptions());
        foreach (var (source, sql) in new[]
        {
            ("1", "CREATE SCHEMA staging"),
            ("2", "CREATE TABLE staging.Fine (Id int NOT NULL)"),
            ("3", "CREATE VIEW staging.Broken AS SELECT src.Id FROM staging.Missing AS src"),
            ("4", "CREATE VIEW staging.OnBroken AS SELECT Id FROM staging.Broken"),
            ("5", "CREATE VIEW staging.UsedByTracked AS SELECT src.Id FROM staging.AlsoMissing AS src"),
            ("6", "CREATE VIEW dbo.Tracked AS SELECT Id FROM staging.UsedByTracked"),
            ("7", "CREATE VIEW dbo.TrackedBroken AS SELECT src.Id FROM dbo.Missing AS src"),
            ("8", "CREATE VIEW dbo.Fine AS SELECT Id FROM staging.Fine"),
        })
        {
            model.AddOrUpdateObjects(sql, source, new TSqlObjectOptions());
        }

        var removed = BrokenObjects.RemoveUntracked(model, StagingExcluded);

        Assert.Equal(["[staging].[Broken]", "[staging].[OnBroken]"], removed);
        Assert.Equal(
            ["[dbo].[Fine]", "[dbo].[TrackedBroken]", "[dbo].[Tracked]", "[staging].[UsedByTracked]"],
            model.GetObjects(DacQueryScopes.UserDefined, ModelSchema.View).Select(v => v.Name.ToString()).Order(StringComparer.Ordinal));
        Assert.Single(model.GetObjects(DacQueryScopes.UserDefined, ModelSchema.Table));
    }

    [Fact]
    public void DoctorNamesTheObjectsThatStopAPackageBeingSaved()
    {
        const string message = "Cannot save package to file. The model has build blocking errors:\n"
            + "Error SQL71501: Error validating element [test].[V]: View: [test].[V] has an unresolved reference to object [test].[T].\n"
            + "Error SQL71501: Error validating element [test].[V].[Id]: Computed Column: [test].[V].[Id] contains an unresolved reference to an object.\n"
            + "Error SQL71501: Error validating element [test].[V]: View: [test].[V] has an unresolved reference to object [test].[U].\n"
            + "Error SQL71589: Error validating element [NE_Credential]: Master Key must be created before a database scoped credential\n";

        var detail = Doctor.DescribeErrors(message);

        Assert.Equal(
            "Cannot save package to file. The model has build blocking errors: 4 errors in 3 objects: "
            + "[test].[V] SQL71501: View: [test].[V] has an unresolved reference to object [test].[T].; "
            + "[test].[V].[Id] SQL71501: Computed Column: [test].[V].[Id] contains an unresolved reference to an object.; "
            + "[NE_Credential] SQL71589: Master Key must be created before a database scoped credential",
            detail);
    }
}
