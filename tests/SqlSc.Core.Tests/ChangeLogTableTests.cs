using SqlSc.Core.ChangeTracking;

namespace SqlSc.Core.Tests;

public class ChangeLogTableTests
{
    private static readonly DateTime Time = new(2026, 10, 1, 12, 0, 0, DateTimeKind.Unspecified);

    private static ChangeLogRow Row(string type, string? schema, string? name, string? objectType, string? target = null, string? newName = null) =>
        new(Time, type, "dan", "PC1", "SSMS", schema, name, objectType, target, newName);

    [Theory]
    [InlineData("CREATE_PROCEDURE", ChangeKind.Created)]
    [InlineData("ALTER_PROCEDURE", ChangeKind.Altered)]
    [InlineData("DROP_PROCEDURE", ChangeKind.Deleted)]
    [InlineData("GRANT_DATABASE", ChangeKind.Altered)]
    public void EventTypesMapToKinds(string type, ChangeKind kind)
    {
        var e = Assert.Single(ChangeLogTable.ToEvents(Row(type, "Sales", "GetCustomer", "PROCEDURE")));

        Assert.Equal((kind, "Sales.GetCustomer", "dan"), (e.Kind, e.Key, e.LoginName));
    }

    [Fact]
    public void IndexChangesAlterTheirTable()
    {
        var e = Assert.Single(ChangeLogTable.ToEvents(Row("CREATE_INDEX", "Sales", "IX_Customer_Name", "INDEX", target: "Customer")));

        Assert.Equal((ChangeKind.Altered, "Sales.Customer"), (e.Kind, e.Key));
    }

    [Fact]
    public void RenamesAlsoChangeTheNewName() =>
        Assert.Equal(
            [(ChangeKind.Altered, "Sales.Old"), (ChangeKind.Created, "Sales.New")],
            ChangeLogTable.ToEvents(Row("RENAME", "Sales", "Old", "PROCEDURE", newName: "New")).Select(e => (e.Kind, e.Key)));

    [Fact]
    public void EventsWithoutAnObjectAndOnTheChangeLogAreLeftOut()
    {
        Assert.Empty(ChangeLogTable.ToEvents(Row("ALTER_DATABASE", null, null, null)));
        Assert.Empty(ChangeLogTable.ToEvents(Row("ALTER_TABLE", "sqlsc", "ChangeLog", "TABLE")));
    }

    [Fact]
    public void ScriptSeparatesBatchesWithGo()
    {
        var script = ChangeLogTable.Script(ChangeLogTable.InstallBatches);

        Assert.Equal(ChangeLogTable.InstallBatches.Count, script.Split("\r\nGO\r\n").Length - 1);
        Assert.Contains("CREATE OR ALTER TRIGGER [sqlsc_ChangeLog] ON DATABASE", script, StringComparison.Ordinal);
    }
}
