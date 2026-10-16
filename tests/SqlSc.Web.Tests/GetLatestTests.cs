using SqlSc.Core.Apply;
using SqlSc.Core.StaticData;
using SqlSc.Web.Components.Pages;
using SqlSc.Web.Services;

namespace SqlSc.Web.Tests;

public class GetLatestTests
{
    [Fact]
    public void ThePlanIsTheSameWhateverOrderItComesIn()
    {
        AppliedObject[] objects =
        [
            new(ApplyAction.Alter, "Procedure", "[dbo].[A]", null, null),
            new(ApplyAction.Drop, "Table", "[dbo].[B]", null, null),
        ];
        AppliedData[] data = [new("[dbo].[C]", "Data/dbo.C_Data.sql", 1, 2, 3)];

        Assert.Equal(GetLatest.PlanKey(objects, data), GetLatest.PlanKey([.. objects.Reverse()], data));
        Assert.NotEqual(GetLatest.PlanKey(objects, data), GetLatest.PlanKey(objects, [data[0] with { Deleted = 4 }]));
        Assert.NotEqual(GetLatest.PlanKey(objects, data), GetLatest.PlanKey(objects[..1], data));
    }

    [Fact]
    public void SwappedRowsShowTheNewSideOnTheLeft()
    {
        var row = new DiffRow(3, "old", 4, "new").Swapped();

        Assert.Equal(new DiffRow(4, "new", 3, "old"), row);
        Assert.Equal(new DiffRow(null, "@@ -1 +1 @@", null, "@@ -1 +1 @@", IsHunk: true), new DiffRow(null, "@@ -1 +1 @@", null, "@@ -1 +1 @@", IsHunk: true).Swapped());
    }
}
