using SqlSc.Core.Filtering;
using SqlSc.Core.Scripting;

namespace SqlSc.Core.Tests;

public class CatalogScripterTests
{
    private const string Collation = "SQL_Latin1_General_CP1_CI_AS";

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
            </Filters>
          </Filter>
        </NamedFilter>
        """;

    private static ObjectFilter StagingExcluded => ObjectFilter.LoadScpf(Path.Combine(FixturePaths.CreateTempFolder(("Filter.scpf", Scpf)), "Filter.scpf"));

    [Fact]
    public void ScriptsOnlyTrackedObjectsAndWhatTheyDependOn()
    {
        var catalog = Snapshot(
            objects: [Object(1, "dbo", "Customer"), Object(2, "staging", "Load"), Object(3, "staging", "Lookup"), Object(4, "dbo", "CustomerView", "V")],
            modules: [new ModuleRow(4, "CREATE VIEW [dbo].[CustomerView] AS SELECT Id FROM [staging].[Lookup]", true, true, false, false)],
            dependencies: [new DependencyRow(4, 3, false)]);

        var plan = new CatalogScripter(catalog).Plan(StagingExcluded, []);

        Assert.Empty(plan.Unsupported);
        Assert.Equal(2, plan.TrackedCount);
        Assert.Equal(3, plan.ScriptedCount);
        var sources = plan.Units.Select(u => u.Source).ToList();
        Assert.Contains("o:[dbo].[Customer]", sources);
        Assert.Contains("o:[dbo].[CustomerView]", sources);
        Assert.Contains("o:[staging].[Lookup]", sources);
        Assert.DoesNotContain("o:[staging].[Load]", sources);
    }

    [Fact]
    public void ScriptsExtraNamesTheFolderReferences()
    {
        var catalog = Snapshot(objects: [Object(1, "dbo", "Customer"), Object(2, "staging", "Load")]);

        var plan = new CatalogScripter(catalog).Plan(StagingExcluded, [("STAGING", "load")]);

        Assert.Equal(1, plan.TrackedCount);
        Assert.Equal(2, plan.ScriptedCount);
        Assert.Contains("o:[staging].[Load]", plan.Units.Select(u => u.Source));
    }

    [Fact]
    public void UnsupportedFeaturesOnlyCountForScriptedObjects()
    {
        var catalog = Snapshot(
            objects: [Object(1, "dbo", "Customer"), Object(2, "staging", "History")],
            tables: [Table(1), Table(2) with { TemporalType = 2 }]);

        Assert.Empty(new CatalogScripter(catalog).Plan(StagingExcluded, []).Unsupported);
        Assert.Equal(["table [staging].[History] (system-versioned)"], new CatalogScripter(catalog).Plan(ObjectFilter.IncludeAll, []).Unsupported);
    }

    [Fact]
    public void ModulesWithoutDefinitionsAreUnsupported()
    {
        var catalog = Snapshot(
            objects: [Object(1, "dbo", "Secret", "P")],
            modules: [new ModuleRow(1, null, true, true, false, false)]);

        Assert.Equal(["encrypted module [dbo].[Secret]"], new CatalogScripter(catalog).Plan(ObjectFilter.IncludeAll, []).Unsupported);
    }

    [Fact]
    public void DatabaseFeaturesAreUnsupported()
    {
        var catalog = Snapshot(objects: [Object(1, "dbo", "Customer")], unsupportedFeatures: [("partition schemes", 2)]);

        Assert.Equal(["2 partition schemes"], new CatalogScripter(catalog).Plan(ObjectFilter.IncludeAll, []).Unsupported);
    }

    [Fact]
    public void TableScriptOmitsTheDefaultCollationAndKeepsOthers()
    {
        var catalog = Snapshot(
            objects: [Object(1, "dbo", "Customer")],
            columns:
            [
                Column(1, 1, "Id", "int") with { Identity = true, Seed = "1", Increment = "1" },
                Column(1, 2, "Name", "nvarchar", 100, collation: Collation) with { Nullable = true },
                Column(1, 3, "Code", "varchar", -1, collation: "Latin1_General_BIN2") with { DefaultName = "DF_Code", DefaultDefinition = "('x')" },
            ]);

        var script = new CatalogScripter(catalog).Plan(ObjectFilter.IncludeAll, []).Units.Single(u => u.Source == "o:[dbo].[Customer]").Script;

        Assert.Contains("[Id] [int] NOT NULL IDENTITY(1, 1)", script, StringComparison.Ordinal);
        Assert.Contains("[Name] [nvarchar] (50) NULL", script, StringComparison.Ordinal);
        Assert.Contains("[Code] [varchar] (max) COLLATE Latin1_General_BIN2 NOT NULL CONSTRAINT [DF_Code] DEFAULT ('x')", script, StringComparison.Ordinal);
        Assert.DoesNotContain(Collation, script, StringComparison.Ordinal);
    }

    [Fact]
    public void SecretsAreNeverScripted()
    {
        var catalog = Snapshot(
            objects: [],
            principals:
            [
                new PrincipalRow(5, "app_login_user", "S", null, null, 1, "app_login", false),
                new PrincipalRow(6, "contained_user", "S", null, null, 2, null, false),
            ],
            credentials: [new CredentialRow(1, "blob", "SHARED ACCESS SIGNATURE")]);

        var units = new CatalogScripter(catalog).Plan(ObjectFilter.IncludeAll, []).Units;

        Assert.Equal(
            "CREATE DATABASE SCOPED CREDENTIAL [blob] WITH IDENTITY = N'SHARED ACCESS SIGNATURE'",
            units.Single(u => u.Source == "credential:blob").Script);
        var passwords = units.Where(u => u.Script.Contains("PASSWORD", StringComparison.Ordinal)).Select(u => u.Script).ToList();
        Assert.Equal(2, passwords.Count);
        Assert.Single(passwords.Select(p => p[(p.IndexOf("PASSWORD", StringComparison.Ordinal) + 12)..].Trim('\'')).Distinct());
        Assert.DoesNotContain(units, u => u.Script.Contains("placeholder", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void DisabledTriggerIsCreatedAndDisabledInOneUnit()
    {
        var catalog = Snapshot(
            objects: [Object(1, "dbo", "Customer"), Object(2, "dbo", "trCustomer", "TR", parentId: 1)],
            modules: [new ModuleRow(2, "CREATE TRIGGER [dbo].[trCustomer] ON [dbo].[Customer] AFTER INSERT AS RETURN", true, true, true, false)]);

        var unit = new CatalogScripter(catalog).Plan(ObjectFilter.IncludeAll, []).Units.Single(u => u.Source == "o:[dbo].[trCustomer]");

        Assert.Equal("DmlTrigger", unit.ModuleType);
        Assert.EndsWith("\nGO\nDISABLE TRIGGER [dbo].[trCustomer] ON [dbo].[Customer]", unit.Script, StringComparison.Ordinal);
    }

    [Fact]
    public void PermissionsKeepANonDefaultGrantor()
    {
        var catalog = Snapshot(
            objects: [Object(1, "dbo", "Customer")],
            principals: [new PrincipalRow(5, "reader", "S", null, null, 0, null, false), new PrincipalRow(6, "owner", "S", null, null, 0, null, false)],
            permissions: [new PermissionRow(1, 1, 0, 5, "SELECT", "G", null, 1), new PermissionRow(1, 1, 0, 5, "UPDATE", "W", null, 6)]);

        var lines = new CatalogScripter(catalog).Plan(ObjectFilter.IncludeAll, []).Units.Single(u => u.Source == "permissions").Script.Split('\n');

        Assert.Equal(["GRANT SELECT ON [dbo].[Customer] TO [reader]", "GO", "GRANT UPDATE ON [dbo].[Customer] TO [reader] WITH GRANT OPTION AS [owner]"], lines);
    }

    [Theory]
    [InlineData("nvarchar", 20, 0, 0, "[nvarchar] (10)")]
    [InlineData("nvarchar", -1, 0, 0, "[nvarchar] (max)")]
    [InlineData("varbinary", 16, 0, 0, "[varbinary] (16)")]
    [InlineData("decimal", 9, 18, 4, "[decimal] (18, 4)")]
    [InlineData("datetime2", 8, 27, 7, "[datetime2] (7)")]
    [InlineData("float", 8, 53, 0, "[float]")]
    [InlineData("float", 4, 24, 0, "[float] (24)")]
    [InlineData("sysname", 256, 0, 0, "[sys].[sysname]")]
    [InlineData("int", 4, 10, 0, "[int]")]
    public void RendersSystemTypes(string name, short maxLength, byte precision, byte scale, string expected)
    {
        Assert.Equal(expected, CatalogScripter.SystemType(name, maxLength, precision, scale));
    }

    [Fact]
    public void QuotesIdentifiers()
    {
        Assert.Equal("[odd]]name]", CatalogScripter.Q("odd]name"));
        Assert.Equal("[dbo].[a b]", CatalogScripter.Q("dbo", "a b"));
    }

    private static ObjectRow Object(int id, string schema, string name, string type = "U", int parentId = 0) => new(id, schema, name, type, parentId);

    private static TableRow Table(int id) => new(id, 0, false, false, 0, false, 0, false, false);

    private static ColumnRow Column(int objectId, int columnId, string name, string type, short maxLength = 4, string? collation = null) =>
        new(objectId, columnId, name, "sys", type, false, maxLength, 0, 0, collation, false, false, null, null, false, null, false, false, false, false, null, null, false);

    private static CatalogSnapshot Snapshot(
        IReadOnlyList<ObjectRow> objects,
        IReadOnlyList<TableRow>? tables = null,
        IReadOnlyList<ColumnRow>? columns = null,
        IReadOnlyList<ModuleRow>? modules = null,
        IReadOnlyList<DependencyRow>? dependencies = null,
        IReadOnlyList<PrincipalRow>? principals = null,
        IReadOnlyList<PermissionRow>? permissions = null,
        IReadOnlyList<CredentialRow>? credentials = null,
        IReadOnlyList<(string, int)>? unsupportedFeatures = null) => new()
        {
            Collation = Collation,
            Schemas = objects.Select(o => o.Schema).Distinct().Select((s, i) => new SchemaRow(5 + i, s, "dbo")).ToList(),
            Objects = objects,
            Tables = tables ?? objects.Where(o => o.Type == "U").Select(o => Table(o.Id)).ToList(),
            Columns = columns ?? objects.Where(o => o.Type == "U").Select(o => Column(o.Id, 1, "Id", "int")).ToList(),
            Indexes = [],
            IndexColumns = [],
            Statistics = [],
            StatisticsColumns = [],
            ForeignKeys = [],
            ForeignKeyColumns = [],
            Checks = [],
            Modules = modules ?? [],
            Types = [],
            Synonyms = [],
            Sequences = [],
            Principals = principals ?? [],
            RoleMembers = [],
            Permissions = permissions ?? [],
            ExtendedProperties = [],
            DataSources = [],
            Credentials = credentials ?? [],
            ExternalTables = [],
            Dependencies = dependencies ?? [],
            ParameterTypes = [],
            UnsupportedFeatures = unsupportedFeatures ?? [],
        };
}
