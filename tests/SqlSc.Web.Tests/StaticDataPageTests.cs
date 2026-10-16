using SqlSc.Core.Comparison;
using SqlSc.Core.StaticData;
using SqlSc.Web.Components.Pages;

namespace SqlSc.Web.Tests;

public class StaticDataPageTests
{
    [Fact]
    public void TrackedTablesComeFirstIncludingOnesMissingFromTheDatabase()
    {
        DatabaseTable[] tables = [new("dbo", "Alpha", 5, true), new("dbo", "Country", 3, true), new("dbo", "Log", 9, false)];
        TrackedTable[] tracked = [new("dbo", "Country", "Data/dbo.Country_Data.sql"), new("dbo", "Gone", "Data/dbo.Gone_Data.sql")];
        DataChange[] changes = [new("[dbo].[Country]", "Data/dbo.Country_Data.sql", 1, 0, 0, [])];

        var rows = StaticData.Rows(tables, tracked, changes, null).ToList();

        Assert.Equal(["[dbo].[Country]", "[dbo].[Gone]", "[dbo].[Alpha]", "[dbo].[Log]"], rows.Select(r => r.Table));
        Assert.Equal(1, rows[0].Change?.New);
        Assert.Null(rows[1].Rows);
        Assert.False(rows[3].HasPrimaryKey);
        Assert.Equal(["[dbo].[Alpha]"], StaticData.Rows(tables, tracked, changes, "dbo.al").Select(r => r.Table));
    }

    [Fact]
    public void ProblemsMakeTheOutcomeAFailure()
    {
        var ok = StaticData.Outcome(new DataLinkResult([new("dbo", "Country", "f")], ["big"], []), "Linked");
        Assert.Equal("Linked [dbo].[Country].\nbig", ok);

        var error = Assert.Throws<InvalidDataException>(() => StaticData.Outcome(new DataLinkResult([], [], ["no key"]), "Linked"));
        Assert.Equal("no key", error.Message);
    }
}
