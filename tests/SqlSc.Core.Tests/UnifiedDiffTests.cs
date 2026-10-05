using System.Globalization;
using SqlSc.Core.Export;

namespace SqlSc.Core.Tests;

public class UnifiedDiffTests
{
    [Fact]
    public void ShowsChangedLinesWithThreeLinesOfContext()
    {
        var before = "1\r\n2\r\n3\r\n4\r\n5\r\n6\r\n7\r\n8\r\n";
        var after = "1\r\n2\r\n3\r\n4\r\nfive\r\n6\r\n7\r\n8\r\n";

        Assert.Equal(
            "--- a/x.sql\n+++ b/x.sql\n@@ -2,7 +2,7 @@\n 2\n 3\n 4\n-5\n+five\n 6\n 7\n 8\n",
            UnifiedDiff.Create(before, after, "a/x.sql", "b/x.sql"));
    }

    [Fact]
    public void SplitsDistantChangesIntoHunks()
    {
        var before = string.Join("\n", Enumerable.Range(1, 20));
        var after = string.Join("\n", Enumerable.Range(1, 20).Select(i => i switch { 2 => "two", 19 => "nineteen", _ => i.ToString(CultureInfo.InvariantCulture) }));

        var diff = UnifiedDiff.Create(before, after, "a", "b");

        Assert.Equal(["@@ -1,5 +1,5 @@", "@@ -16,5 +16,5 @@"], diff.Split('\n').Where(l => l.StartsWith("@@", StringComparison.Ordinal)));
        Assert.Contains("-2\n+two\n", diff, StringComparison.Ordinal);
        Assert.Contains("-19\n+nineteen\n", diff, StringComparison.Ordinal);
    }

    [Fact]
    public void ShowsNewAndDeletedFiles()
    {
        Assert.Equal("--- /dev/null\n+++ b/x.sql\n@@ -0,0 +1,2 @@\n+GO\n+SELECT 1\n", UnifiedDiff.Create(null, "GO\nSELECT 1\n", UnifiedDiff.NoFile, "b/x.sql"));
        Assert.Equal("--- a/x.sql\n+++ /dev/null\n@@ -1 +0,0 @@\n-GO\n", UnifiedDiff.Create("GO", null, "a/x.sql", UnifiedDiff.NoFile));
    }

    [Fact]
    public void IsEmptyWhenOnlyLineEndingsDiffer() =>
        Assert.Empty(UnifiedDiff.Create("a\nb\n", "a\r\nb\r\n", "a", "b"));

    [Fact]
    public void KeepsCommonLinesBetweenInsertionsAndDeletions()
    {
        var diff = UnifiedDiff.Create("a\nb\nc\nd\n", "a\nx\nc\ny\nd\n", "a", "b");

        Assert.Equal("--- a\n+++ b\n@@ -1,4 +1,5 @@\n a\n-b\n+x\n c\n+y\n d\n", diff);
    }
}
