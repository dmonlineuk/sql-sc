using SqlSc.Core.Filtering;
using SqlSc.Core.Settings;

namespace SqlSc.Core.WorkingFolders;

/// <summary>
/// A folder of object scripts linked to a database. Understands the Redgate SQL Source Control layout.
/// </summary>
public sealed class WorkingFolder
{
    private static readonly string[] NonSchemaFolders = ["Custom Scripts", "Migrations"];

    private WorkingFolder(string rootPath, RedgateDatabaseInfo? redgateInfo, ProjectSettings settings, ObjectFilter filter, string? filterPath)
    {
        RootPath = rootPath;
        RedgateInfo = redgateInfo;
        Settings = settings;
        Filter = filter;
        FilterPath = filterPath;
    }

    public string RootPath { get; }

    public RedgateDatabaseInfo? RedgateInfo { get; }

    public ProjectSettings Settings { get; }

    public ObjectFilter Filter { get; }

    /// <summary>The filter file in use, relative to the root, or null when every object is included.</summary>
    public string? FilterPath { get; }

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
        var settings = ProjectSettings.Load(root);
        var filterPath = settings.Filter ?? (File.Exists(Path.Combine(root, ProjectSettings.DefaultFilter)) ? ProjectSettings.DefaultFilter : null);
        ObjectFilter filter;
        if (filterPath is null)
        {
            filter = ObjectFilter.IncludeAll;
        }
        else if (File.Exists(Path.Combine(root, filterPath)))
        {
            filter = ObjectFilter.LoadScpf(Path.Combine(root, filterPath));
        }
        else
        {
            throw new FileNotFoundException($"Filter file '{filterPath}' from {ProjectSettings.FileName} does not exist.", Path.Combine(root, filterPath));
        }

        return new WorkingFolder(root, info, settings, filter, filterPath);
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
