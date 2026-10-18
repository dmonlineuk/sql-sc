using System.Text;
using SqlSc.Core.Database;
using SqlSc.Core.Filtering;

namespace SqlSc.Core.Tests;

public class FilterFileTests
{
    [Fact]
    public void ANewFileRoundTripsItsRules()
    {
        var folder = FixturePaths.CreateTempFolder();
        IReadOnlyList<FilterRule> rules = [];
        rules = FilterFile.With(rules, FilterRuleParts.Build("Table", FilterMode.Except, [new(ConditionField.Name, ConditionOperator.Like, "tmp%")], matchAll: false));
        rules = FilterFile.With(rules, FilterRuleParts.Build(ObjectFilter.AllObjects, FilterMode.Except, [new(ConditionField.Schema, ConditionOperator.Equal, "o'brien"), new(ConditionField.Schema, ConditionOperator.Like, "staging%")], matchAll: false));
        rules = FilterFile.With(rules, FilterRuleParts.Build("Synonym", FilterMode.Nothing, ""));

        Assert.Equal("Filter.scpf", FilterFile.Save(folder, rules));

        var text = File.ReadAllText(Path.Combine(folder, "Filter.scpf"));
        Assert.StartsWith("<?xml version=\"1.0\" encoding=\"utf-8\" standalone=\"yes\"?>\r\n<NamedFilter", text, StringComparison.Ordinal);
        Assert.DoesNotContain("\r\r", text, StringComparison.Ordinal);
        var loaded = FilterFile.Load(folder);
        Assert.Equal(
            [
                new FilterRule("None", false, "(@SCHEMA = 'o''brien') OR (@SCHEMA LIKE 'staging%')"),
                new FilterRule("Table", false, "@NAME LIKE 'tmp%'"),
                new FilterRule("Synonym", false, "TRUE"),
            ],
            loaded.Rules);
        Assert.False(loaded.Includes("Table", ["o'brien", "Customer"]));
        Assert.False(loaded.Includes("Table", ["dbo", "tmpOrders"]));
        Assert.True(loaded.Includes("Table", ["dbo", "Orders"]));
        Assert.False(loaded.Includes("Synonym", ["dbo", "Anything"]));
    }

    [Fact]
    public void AnExistingFileKeepsItsLayoutAndOtherSettings()
    {
        const string original = "<?xml version=\"1.0\" encoding=\"utf-16\" standalone=\"yes\"?>\n<NamedFilter version=\"1\" type=\"SQLCompareFilter\">\n  <Filter version=\"1\" type=\"DifferenceFilter\">\n    <FilterCaseSensitive>True</FilterCaseSensitive>\n    <Other>kept</Other>\n    <Filters version=\"1\">\n      <Queue version=\"1\"><Include>True</Include><Expression>TRUE</Expression></Queue>\n      <FutureType version=\"1\"><Include>False</Include><Expression>TRUE</Expression></FutureType>\n    </Filters>\n  </Filter>\n</NamedFilter>\n";
        var folder = FixturePaths.CreateTempFolder();
        var path = Path.Combine(folder, "Filter.scpf");
        File.WriteAllText(path, original, new UTF8Encoding(true));

        var rules = FilterFile.Without(FilterFile.Load(folder).Rules, "Queue");
        FilterFile.Save(folder, FilterFile.With(rules, FilterRuleParts.Build("View", FilterMode.Only, "@SCHEMA = 'dbo'")));

        var bytes = File.ReadAllBytes(path);
        Assert.Equal(Encoding.UTF8.Preamble.ToArray(), bytes[..3]);
        var text = File.ReadAllText(path);
        Assert.StartsWith("<?xml version=\"1.0\" encoding=\"utf-16\" standalone=\"yes\"?>\n", text, StringComparison.Ordinal);
        Assert.DoesNotContain("\r", text, StringComparison.Ordinal);
        Assert.Contains("<Other>kept</Other>", text, StringComparison.Ordinal);
        var loaded = FilterFile.Load(folder);
        Assert.True(loaded.CaseSensitive);
        Assert.Equal([new FilterRule("FutureType", false, "TRUE"), new FilterRule("View", true, "@SCHEMA = 'dbo'")], loaded.Rules);
    }

    [Fact]
    public void InvalidRulesAreRefusedAndNothingIsWritten()
    {
        var folder = FixturePaths.CreateTempFolder();

        Assert.Contains("isn't an object type", Assert.Throws<InvalidDataException>(() => FilterFile.Save(folder, [new FilterRule("Tables", true, "TRUE")])).Message, StringComparison.Ordinal);
        Assert.Contains("<View>", Assert.Throws<InvalidDataException>(() => FilterFile.Save(folder, [new FilterRule("View", true, "@OWNER = 'x'")])).Message, StringComparison.Ordinal);
        Assert.Contains("more than one rule", Assert.Throws<InvalidDataException>(() => FilterFile.Save(folder, [new FilterRule("View", true, "TRUE"), new FilterRule("View", false, "TRUE")])).Message, StringComparison.Ordinal);
        Assert.Throws<InvalidDataException>(() => FilterRuleParts.Build("View", FilterMode.Only, Array.Empty<FilterCondition>(), matchAll: false));
        Assert.False(File.Exists(Path.Combine(folder, "Filter.scpf")));
    }

    [Theory]
    [InlineData("TRUE", FilterMode.Everything, true, 0, false)]
    [InlineData("@NAME NOT LIKE 'tmp%'", FilterMode.Only, true, 1, false)]
    [InlineData("(@SCHEMA = 'a') AND ((@NAME != 'b'))", FilterMode.Except, false, 2, true)]
    [InlineData("(@SCHEMA = 'a') OR (@SCHEMA = 'b') AND (@NAME = 'c')", FilterMode.Except, false, -1, false)]
    [InlineData("NOT (@SCHEMA = 'a')", FilterMode.Only, true, -1, false)]
    public void RulesSplitIntoModesAndSimpleConditions(string expression, FilterMode mode, bool include, int conditions, bool matchAll)
    {
        var parts = FilterRuleParts.Of(new FilterRule("View", include, expression));

        Assert.Equal(mode, parts.Mode);
        Assert.Equal(conditions, parts.Conditions?.Count ?? -1);
        Assert.Equal(matchAll, parts.MatchAll);
    }

    [Fact]
    public void SplitConditionsKeepTheirOperatorsAndValues()
    {
        var conditions = FilterExpression.Split("(@NAME NOT LIKE 'tmp[_]%') OR (@SCHEMA != 'it''s')", out var matchAll);

        Assert.False(matchAll);
        Assert.Equal([new FilterCondition(ConditionField.Name, ConditionOperator.NotLike, "tmp[_]%"), new FilterCondition(ConditionField.Schema, ConditionOperator.NotEqual, "it's")], conditions);
        Assert.Equal("@SCHEMA != 'it''s'", conditions![1].ToExpression());
    }

    [Fact]
    public void CountsAreByRedgateTypeAndLeaveOutSqlScsOwnObjects()
    {
        var catalog = new CatalogSummary(
            [new("Table", "dbo", "A"), new("Table", "staging", "B"), new("ScalarFunction", "dbo", "F"), new("TableValuedFunction", "dbo", "G"), new("Table", "sqlsc", "Lock"), new("Schema", null, "sqlsc")],
            new ChildCounts(0, 0, 0, 0, 0, 0, 0, 0),
            0,
            TimeSpan.Zero);
        var filter = ObjectFilter.FromRules([new FilterRule("Table", false, "@SCHEMA = 'staging'")]);

        Assert.Equal([new FilterCount("Function", 2, 2), new FilterCount("Table", 2, 1)], catalog.CountByType(filter));
    }
}
