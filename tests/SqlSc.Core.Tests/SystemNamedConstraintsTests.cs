using SqlSc.Core.Modeling;

namespace SqlSc.Core.Tests;

public class SystemNamedConstraintsTests
{
    [Theory]
    [InlineData(
        "CREATE TABLE [dbo].[T]\n(\n[Id] [int] NOT NULL CONSTRAINT [DF__T__Id__42501F7D] DEFAULT ((0)),\nCONSTRAINT [PK__T__3213E83F9AEF8E7A] PRIMARY KEY CLUSTERED ([Id])\n)",
        "CREATE TABLE [dbo].[T]\n(\n[Id] [int] NOT NULL DEFAULT ((0)),\nPRIMARY KEY CLUSTERED ([Id])\n)")]
    [InlineData(
        "ALTER TABLE [dbo].[T] ADD CONSTRAINT [DF__T__x__12345678] DEFAULT ((1)) FOR [x]",
        "ALTER TABLE [dbo].[T] ADD DEFAULT ((1)) FOR [x]")]
    [InlineData(
        "ALTER TABLE [dbo].[T] ADD CONSTRAINT [UQ__T__A9D105347F60ED59] UNIQUE ([Code])",
        "ALTER TABLE [dbo].[T] ADD UNIQUE ([Code])")]
    public void SystemGeneratedNamesAreRemoved(string script, string expected) =>
        Assert.Equal(expected, SystemNamedConstraints.Rewrite(script));

    [Theory]
    [InlineData("CREATE TABLE [dbo].[T] ([Id] int NOT NULL CONSTRAINT [PK_T] PRIMARY KEY)")]
    [InlineData("CREATE TABLE [dbo].[T] ([Id] int NOT NULL CONSTRAINT [PK__T__Id] PRIMARY KEY)")]
    [InlineData("CREATE TABLE [dbo].[T] ([Id] int NOT NULL CONSTRAINT [PK__T__3213E83F9AEF8E7A PRIMARY KEY")]
    public void OtherNamesAndUnparseableScriptsAreLeftAlone(string script) =>
        Assert.Equal(script, SystemNamedConstraints.Rewrite(script));

    [Fact]
    public void NamesTheDatabaseGaveExplicitlyAreKept()
    {
        const string script = "ALTER TABLE [dbo].[T] ADD CONSTRAINT [PK__T__3213E83F9AEF8E7A] PRIMARY KEY ([Id])";

        Assert.Equal(script, SystemNamedConstraints.Rewrite(script, keep: new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "pk__T__3213E83F9AEF8E7A" }));
    }

    [Fact]
    public void NamesUsedElsewhereInTheFileAreKept()
    {
        const string script = "ALTER TABLE [dbo].[T] ADD CONSTRAINT [CK__T__Id__42501F7D] CHECK ([Id] > 0)";
        const string file = script + "\nGO\nEXEC sp_addextendedproperty N'MS_Description', N'x', 'SCHEMA', N'dbo', 'TABLE', N'T', 'CONSTRAINT', N'CK__T__Id__42501F7D'";

        Assert.Equal(script, SystemNamedConstraints.Rewrite(script, file: file));
    }
}
