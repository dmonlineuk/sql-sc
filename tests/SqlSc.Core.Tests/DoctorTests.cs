using SqlSc.Core.Diagnostics;

namespace SqlSc.Core.Tests;

public class DoctorTests
{
    [Fact]
    public void DifferenceKindsAreCountedMostCommonFirst()
    {
        string[] differences =
        [
            "New Permission [Deny.Insert.Schema].[reader].[dbo].[db_owner]",
            "Changed Table [app].[Customer]",
            "New Permission [Deny.Delete.Schema].[reader].[dbo].[db_owner]",
        ];

        Assert.Equal("New Permission 2, Changed Table 1", Doctor.DescribeDifferenceKinds(differences));
    }

    [Theory]
    [InlineData("CREATE USER [a]\r\n    FOR LOGIN [a];", "CREATE USER [a]\nFOR LOGIN [b];", "line 2 is `FOR LOGIN [a];` in the full extract, `FOR LOGIN [b];` from the catalog")]
    [InlineData("SELECT 1\nGO", "SELECT 1", "line 2 is `GO` in the full extract, `(end)` from the catalog")]
    [InlineData("SELECT 1\n\n", "  SELECT 1", "scripts are the same")]
    public void FirstDifferenceIgnoresIndentation(string fullExtract, string catalog, string expected)
    {
        Assert.Equal(expected, Doctor.FirstDifference(fullExtract, catalog));
    }
}
