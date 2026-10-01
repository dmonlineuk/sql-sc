namespace SqlSc.Core.WorkingFolders;

/// <summary>
/// A folder of object scripts linked to a database. Understands the Redgate SQL Source Control layout.
/// </summary>
public sealed class WorkingFolder
{
    private static readonly string[] NonSchemaFolders = ["Custom Scripts", "Migrations"];

    private WorkingFolder(string rootPath, RedgateDatabaseInfo? redgateInfo)
    {
        RootPath = rootPath;
        RedgateInfo = redgateInfo;
    }

    public string RootPath { get; }

    public RedgateDatabaseInfo? RedgateInfo { get; }

    public string DataFolder => RedgateInfo?.DataFolder ?? "Data";

    public static WorkingFolder Open(string path)
    {
        var root = Path.GetFullPath(path);
        if (!Directory.Exists(root))
        {
            throw new DirectoryNotFoundException($"Working folder '{root}' does not exist.");
        }

        var infoPath = Path.Combine(root, RedgateDatabaseInfo.FileName);
        var info = File.Exists(infoPath) ? RedgateDatabaseInfo.Load(infoPath) : null;
        return new WorkingFolder(root, info);
    }

    /// <summary>Schema object scripts (excludes static data, migrations and custom scripts), as paths relative to the root.</summary>
    public IReadOnlyList<string> GetSchemaScripts()
    {
        var excluded = NonSchemaFolders.Append(DataFolder)
            .Select(f => f.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar)
            .ToArray();

        return Directory.EnumerateFiles(RootPath, "*.sql", SearchOption.AllDirectories)
            .Select(f => Path.GetRelativePath(RootPath, f))
            .Where(rel => !excluded.Any(ex => rel.StartsWith(ex, StringComparison.OrdinalIgnoreCase)))
            .Order(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public string ReadScript(string relativePath) => File.ReadAllText(Path.Combine(RootPath, relativePath));
}
