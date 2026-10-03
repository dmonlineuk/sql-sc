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
}
