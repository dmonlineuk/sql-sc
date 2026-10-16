using SqlSc.Web.Services;

namespace SqlSc.Web.Tests;

public class SideBySideDiffTests
{
    [Fact]
    public void PairsRemovedAndAddedLinesAndNumbersEachSide()
    {
        const string unified = """
            --- a/Tables/dbo.T.sql
            +++ b/Tables/dbo.T.sql
            @@ -2,4 +2,4 @@
             [Id] INT NOT NULL,
            -[Name] VARCHAR(50) NULL,
            -[Old] INT NULL
            +[Name] VARCHAR(255) NULL
             )
            +GO
            """;

        var rows = SideBySideDiff.Parse(unified.Replace("\r\n", "\n", StringComparison.Ordinal));

        Assert.Equal(
            [
                new DiffRow(null, "@@ -2,4 +2,4 @@", null, "@@ -2,4 +2,4 @@", IsHunk: true),
                new DiffRow(2, "[Id] INT NOT NULL,", 2, "[Id] INT NOT NULL,"),
                new DiffRow(3, "[Name] VARCHAR(50) NULL,", 3, "[Name] VARCHAR(255) NULL"),
                new DiffRow(4, "[Old] INT NULL", null, null),
                new DiffRow(5, ")", 4, ")"),
                new DiffRow(null, null, 5, "GO"),
            ],
            rows);
        Assert.Equal([false, false, true, true, false, true], rows.Select(r => r.Changed));
    }

    [Fact]
    public void ANewFileHasOnlyARightSide()
    {
        var rows = SideBySideDiff.Parse("--- /dev/null\n+++ b/x.sql\n@@ -0,0 +1,2 @@\n+CREATE VIEW v\n+AS SELECT 1\n");

        Assert.Equal([new DiffRow(null, null, 1, "CREATE VIEW v"), new DiffRow(null, null, 2, "AS SELECT 1")], rows.Skip(1));
    }

    [Fact]
    public void NoDiffHasNoRows() => Assert.Empty(SideBySideDiff.Parse(""));
}
