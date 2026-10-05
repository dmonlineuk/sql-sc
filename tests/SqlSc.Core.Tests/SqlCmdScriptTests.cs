using SqlSc.Core.Apply;

namespace SqlSc.Core.Tests;

public class SqlCmdScriptTests
{
    [Fact]
    public void SplitsOnGoAndSubstitutesVariables()
    {
        const string script = """
            :setvar DatabaseName "Shop"
            :setvar DefaultFilePrefix "Shop"
            :on error exit
            GO
            USE [$(DatabaseName)];

            go
            PRINT N'Altering $(DefaultFilePrefix)...';
            GO
            """;

        Assert.Equal(["USE [Shop];", "PRINT N'Altering Shop...';"], SqlCmdScript.Batches(script.Replace("\n", "\r\n", StringComparison.Ordinal)));
    }

    [Fact]
    public void KeepsLineEndingsWithinABatch() =>
        Assert.Equal(["CREATE VIEW v\r\nAS\nSELECT 1"], SqlCmdScript.Batches("CREATE VIEW v\r\nAS\nSELECT 1\r\nGO\r\n"));

    [Fact]
    public void RejectsUnknownVariablesAndCommands()
    {
        Assert.Throws<InvalidDataException>(() => SqlCmdScript.Batches("SELECT '$(Missing)'"));
        Assert.Throws<InvalidDataException>(() => SqlCmdScript.Batches(":r other.sql"));
    }
}
