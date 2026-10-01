namespace SqlSc.Core.Tests;

internal static class FixturePaths
{
    public static string Demo => Path.Combine(AppContext.BaseDirectory, "Fixtures", "Demo");

    public static string CreateTempFolder(params (string RelativePath, string Content)[] files)
    {
        var root = Path.Combine(Path.GetTempPath(), "sql-sc-tests", Guid.NewGuid().ToString("N"));
        foreach (var (relativePath, content) in files)
        {
            var path = Path.Combine(root, relativePath);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, content);
        }

        Directory.CreateDirectory(root);
        return root;
    }
}
