using SqlSc.Core.Apply;
using SqlSc.Core.ChangeTracking;
using SqlSc.Core.Comparison;
using SqlSc.Core.Scripting;
using SqlSc.Core.StaticData;

namespace SqlSc.Core.Tests;

public class ApplyPromptTests
{
    private static readonly StatusReport Status = new(
        "dev-server",
        "Shared",
        "Sql160",
        0,
        null,
        [],
        [],
        new ChangeLog(false, null, []),
        new Dictionary<string, TimeSpan>(),
        new DatabaseModelInfo(false, 0, 0, [], TimeSpan.Zero),
        []);

    private static readonly AppliedObject[] Objects =
    [
        new(ApplyAction.Alter, "Procedure", "[Sales].[GetCustomer]", null, null),
        new(ApplyAction.Create, "View", "[Sales].[ActiveCustomer]", null, null),
        new(ApplyAction.Drop, "Table", "[Sales].[Order]", null, null),
    ];

    private static readonly AppliedData[] Data = [new("[Sales].[Customer]", "Data/Sales.Customer_Data.sql", 0, 1, 0)];

    [Fact]
    public void AsksAboutEachChangeAndKeepsOnlyTheAcceptedOnes()
    {
        var output = new StringWriter();

        var choice = new ApplyPrompt(new StringReader("y\nn\nmaybe\nyes\nN\n"), output).Confirm(Status, Objects, Data);

        Assert.Equal(["[Sales].[GetCustomer]", "[Sales].[Order]"], choice.Objects.Select(o => o.Name));
        Assert.Empty(choice.Data);
        var text = output.ToString();
        Assert.Contains("dev-server/Shared is a shared database", text, StringComparison.Ordinal);
        Assert.Equal(5, text.Split("[y/n/a/q]").Length - 1);
        Assert.Contains("[Sales].[Customer]?", text, StringComparison.Ordinal);
    }

    [Fact]
    public void AllAcceptsTheRestAndQuitDeclinesTheRest()
    {
        var all = new ApplyPrompt(new StringReader("n\na\n"), new StringWriter()).Confirm(Status, Objects, Data);
        var quit = new ApplyPrompt(new StringReader("y\nq\n"), new StringWriter()).Confirm(Status, Objects, Data);

        Assert.Equal(["[Sales].[ActiveCustomer]", "[Sales].[Order]"], all.Objects.Select(o => o.Name));
        Assert.Single(all.Data);
        Assert.Equal(["[Sales].[GetCustomer]"], quit.Objects.Select(o => o.Name));
        Assert.Empty(quit.Data);
    }

    [Fact]
    public void EndOfInputDeclinesEverythingLeft()
    {
        var choice = new ApplyPrompt(new StringReader("y\n"), new StringWriter()).Confirm(Status, Objects, Data);

        Assert.Equal(["[Sales].[GetCustomer]"], choice.Objects.Select(o => o.Name));
        Assert.Empty(choice.Data);
    }
}
