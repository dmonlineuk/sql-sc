using Microsoft.SqlServer.Dac.Model;
using SqlSc.Core.Modeling;
using SqlSc.Core.WorkingFolders;

namespace SqlSc.Core.Tests;

public class FolderModelLoaderTests
{
    [Fact]
    public void LoadsDemoFolderWithoutIssues()
    {
        using var model = FolderModelLoader.Load(WorkingFolder.Open(FixturePaths.Demo));

        Assert.Empty(model.Issues);
        Assert.Equal(SqlServerVersion.Sql160, model.Platform);
        Assert.Equal(5, model.FileCount);

        var names = model.Model.GetObjects(DacQueryScopes.UserDefined)
            .Where(o => o.Name.HasName)
            .Select(o => $"{o.ObjectType.Name}:{o.Name}")
            .ToList();
        Assert.Contains("Schema:[Sales]", names);
        Assert.Contains("Table:[Sales].[Customer]", names);
        Assert.Contains("PrimaryKeyConstraint:[Sales].[PK_Customer]", names);
        Assert.Contains("View:[Sales].[ActiveCustomer]", names);
        Assert.Contains("Procedure:[Sales].[GetCustomer]", names);
        Assert.Contains("User:[app_reader]", names);
        Assert.Contains(names, n => n.StartsWith("ExtendedProperty:", StringComparison.Ordinal));
    }

    [Fact]
    public void ObjectsRememberTheirFileAndLine()
    {
        using var model = FolderModelLoader.Load(WorkingFolder.Open(FixturePaths.Demo));

        var view = model.Model.GetObjects(DacQueryScopes.UserDefined, ModelSchema.View).Single();
        var source = ScriptSource.Decode(view.GetSourceInformation().SourceName);

        Assert.Equal((Path.Combine("Views", "Sales.ActiveCustomer.sql"), 5), source);
    }

    [Fact]
    public void SetBatchesApplyToFollowingObjects()
    {
        var root = FixturePaths.CreateTempFolder(("Views/dbo.V.sql", "SET QUOTED_IDENTIFIER OFF\r\nGO\r\nCREATE VIEW dbo.V AS SELECT 1 AS One\r\nGO\r\n"));

        using var model = FolderModelLoader.Load(WorkingFolder.Open(root));

        var view = model.Model.GetObjects(DacQueryScopes.UserDefined, ModelSchema.View).Single();
        Assert.False(view.GetProperty<bool>(View.QuotedIdentifierOn));
    }

    [Fact]
    public void CommentsAroundAModuleArePartOfItsDefinition()
    {
        var root = FixturePaths.CreateTempFolder(("Stored Procedures/dbo.P.sql", "SET QUOTED_IDENTIFIER ON\r\nGO\r\n-- Author: x\r\ncreate procedure dbo.P AS SELECT 1\r\n-- trailing\r\nGO\r\n"));

        using var model = FolderModelLoader.Load(WorkingFolder.Open(root));

        var procedure = model.Model.GetObjects(DacQueryScopes.UserDefined, ModelSchema.Procedure).Single();
        Assert.True(procedure.TryGetScript(out var script));
        Assert.Equal("-- Author: x\r\ncreate procedure dbo.P AS SELECT 1\r\n-- trailing", script);
    }

    [Fact]
    public void EntraUsersStayExternal()
    {
        var root = FixturePaths.CreateTempFolder(("Security/Users/x.sql", "CREATE USER [x] FROM EXTERNAL PROVIDER\r\nGO\r\n"));

        using var model = FolderModelLoader.Load(WorkingFolder.Open(root), SqlServerVersion.SqlAzure);

        Assert.Empty(model.Issues);
        var user = model.Model.GetObjects(DacQueryScopes.UserDefined, ModelSchema.User).Single();
        Assert.Equal(AuthenticationType.ExternalAuthenticationProvider, user.GetProperty<AuthenticationType>(User.AuthenticationType));
    }

    [Fact]
    public void ParseErrorsAreReportedWithFileAndLine()
    {
        var root = FixturePaths.CreateTempFolder(("Tables/dbo.Broken.sql", "CREATE TABLE dbo.Ok (Id int)\nGO\nCREATE TABLE dbo.Broken (Id int,,)\nGO\n"));

        using var model = FolderModelLoader.Load(WorkingFolder.Open(root));

        var issue = Assert.Single(model.Issues);
        Assert.Equal(IssueSeverity.Error, issue.Severity);
        Assert.Equal(Path.Combine("Tables", "dbo.Broken.sql"), issue.File);
        Assert.Equal(3, issue.Line);
    }

    [Fact]
    public void UnresolvedReferencesAreWarnings()
    {
        var root = FixturePaths.CreateTempFolder(
            ("Tables/Missing.T.sql", "CREATE TABLE [Missing].[T] (Id int)\nGO\n"),
            ("Security/Users/u.sql", "CREATE USER [u] FOR LOGIN [u]\nGO\n"));

        using var model = FolderModelLoader.Load(WorkingFolder.Open(root));

        Assert.False(model.HasErrors);
        Assert.Contains(model.Issues, i => i.Message.StartsWith("SQL71501", StringComparison.Ordinal));
        Assert.All(model.Issues, i => Assert.Equal(IssueSeverity.Warning, i.Severity));
    }

    [Fact]
    public void ConstraintAndTriggerStateStatementsApplyToObjectsEarlierInTheFile()
    {
        var root = FixturePaths.CreateTempFolder(("Tables/dbo.T.sql", """
            CREATE TABLE [dbo].[T] ([Id] int NOT NULL PRIMARY KEY, [X] int NULL)
            GO
            ALTER TABLE [dbo].[T] ADD CONSTRAINT [CK_T_On] CHECK (([X]>(0)))
            GO
            ALTER TABLE [dbo].[T] WITH NOCHECK ADD CONSTRAINT [CK_T_Off] CHECK (([X]<(9)))
            GO
            ALTER TABLE [dbo].[T] CHECK CONSTRAINT [CK_T_On]
            GO
            ALTER TABLE [dbo].[T] NOCHECK CONSTRAINT [CK_T_Off]
            GO
            CREATE TRIGGER [dbo].[T_Insert] ON [dbo].[T] AFTER INSERT AS SET NOCOUNT ON
            GO
            DISABLE TRIGGER [dbo].[T_Insert] ON [dbo].[T]
            GO
            """));

        using var model = FolderModelLoader.Load(WorkingFolder.Open(root));

        Assert.False(model.HasErrors, string.Join("\n", model.Issues.Select(i => i.Message)));
        var checks = model.Model.GetObjects(DacQueryScopes.UserDefined, ModelSchema.CheckConstraint).ToDictionary(c => c.Name.Parts[^1]);
        Assert.False(checks["CK_T_On"].GetProperty<bool>(CheckConstraint.Disabled));
        Assert.True(checks["CK_T_Off"].GetProperty<bool>(CheckConstraint.Disabled));
        var trigger = model.Model.GetObjects(DacQueryScopes.UserDefined, ModelSchema.DmlTrigger).Single();
        Assert.True(trigger.GetProperty<bool>(DmlTrigger.Disabled));
    }

    [Theory]
    [InlineData("Tables/dbo.T.sql", 12)]
    [InlineData("a|b.sql", 1)]
    public void ScriptSourceRoundTrips(string file, int line)
    {
        Assert.Equal((file, line), ScriptSource.Decode(ScriptSource.Encode(file, line)));
    }
}
