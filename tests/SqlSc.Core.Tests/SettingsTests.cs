using SqlSc.Core.Settings;
using SqlSc.Core.WorkingFolders;

namespace SqlSc.Core.Tests;

public class SettingsTests
{
    [Fact]
    public void ProjectSettingsDefaultWhenFileIsMissing()
    {
        var settings = ProjectSettings.Load(FixturePaths.CreateTempFolder());

        Assert.Null(settings.Filter);
        Assert.True(settings.Compare.IgnoreWhitespace);
        Assert.True(settings.Compare.ToDeployOptions().BlockOnPossibleDataLoss);
    }

    [Fact]
    public void ProjectSettingsReadCommentsAndCompareOptions()
    {
        var root = FixturePaths.CreateTempFolder((ProjectSettings.FileName, """
            {
              // team settings
              "filter": "Filters/Team.scpf",
              "compare": { "ignorePermissions": true, "ignoreWhitespace": false },
            }
            """));

        var settings = ProjectSettings.Load(root);

        Assert.Equal("Filters/Team.scpf", settings.Filter);
        Assert.False(settings.Compare.Includes("Permission"));
        Assert.True(settings.Compare.Includes("ExtendedProperty"));
        var options = settings.Compare.ToDeployOptions();
        Assert.True(options.IgnorePermissions);
        Assert.False(options.IgnoreWhitespace);
    }

    [Fact]
    public void ProjectSettingsRoundTrip()
    {
        var settings = new ProjectSettings { Filter = "x.scpf", Compare = new CompareSettings { IgnoreComments = true } };
        var root = FixturePaths.CreateTempFolder((ProjectSettings.FileName, settings.Serialize()));

        Assert.Equal(settings, ProjectSettings.Load(root));
    }

    [Fact]
    public void InvalidProjectSettingsThrow()
    {
        var root = FixturePaths.CreateTempFolder((ProjectSettings.FileName, "{ \"filter\": "));

        Assert.Throws<InvalidDataException>(() => ProjectSettings.Load(root));
    }

    [Fact]
    public void MissingConfiguredFilterThrows()
    {
        var root = FixturePaths.CreateTempFolder((ProjectSettings.FileName, """{ "filter": "missing.scpf" }"""));

        Assert.Throws<FileNotFoundException>(() => WorkingFolder.Open(root));
    }

    [Fact]
    public void LinksAreSavedReplacedAndFoundFromSubfolders()
    {
        var store = new LinkStore(Path.Combine(FixturePaths.CreateTempFolder(), "nested", "links.json"));
        var folder = FixturePaths.CreateTempFolder(("Tables/dbo.T.sql", "CREATE TABLE dbo.T (Id int)"));

        store.Save(new Link(folder + Path.DirectorySeparatorChar, "Server=a;Database=one", DatabaseMode.Shared));
        store.Save(new Link(folder, "Server=a;Database=two", DatabaseMode.Dedicated));

        var link = Assert.Single(store.All());
        Assert.Equal(Path.GetFullPath(folder), link.Folder);
        Assert.Equal("Server=a;Database=two", link.Connection);
        Assert.Equal(DatabaseMode.Dedicated, link.Mode);
        Assert.Equal(link, store.FindFor(Path.Combine(folder, "Tables")));
        Assert.Null(store.FindFor(folder + "-other"));
        Assert.Contains("\"Dedicated\"", File.ReadAllText(store.FilePath), StringComparison.Ordinal);

        Assert.True(store.Remove(folder));
        Assert.Empty(store.All());
        Assert.False(store.Remove(folder));
    }

    [Fact]
    public void ClosestLinkWins()
    {
        var store = new LinkStore(Path.Combine(FixturePaths.CreateTempFolder(), "links.json"));
        var outer = FixturePaths.CreateTempFolder(("db/Tables/dbo.T.sql", "CREATE TABLE dbo.T (Id int)"));
        store.Save(new Link(outer, "Server=a;Database=outer", DatabaseMode.Shared));
        store.Save(new Link(Path.Combine(outer, "db"), "Server=a;Database=inner", DatabaseMode.Shared));

        Assert.Equal("Server=a;Database=inner", store.FindFor(Path.Combine(outer, "db", "Tables"))?.Connection);
        Assert.Equal("Server=a;Database=outer", store.FindFor(outer)?.Connection);
    }
}
