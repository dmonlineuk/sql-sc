using System.Text.Json;
using System.Text.Json.Serialization;

namespace SqlSc.Core.Settings;

public enum DatabaseMode
{
    /// <summary>Everyone develops on one database; the database is usually ahead of source control.</summary>
    Shared,

    /// <summary>Each developer has their own database.</summary>
    Dedicated,
}

/// <summary>A working folder linked to a database. Stored per user, never committed.</summary>
public sealed record Link(string Folder, string Connection, DatabaseMode Mode);

/// <summary>
/// Per-user links between working folders and databases, in <c>%APPDATA%\sql-sc\links.json</c> or
/// <c>~/.config/sql-sc/links.json</c>. <c>SQLSC_HOME</c> overrides the folder.
/// </summary>
public sealed class LinkStore(string path)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    private static readonly StringComparison PathComparison =
        OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    public static string SettingsDirectory =>
        Environment.GetEnvironmentVariable("SQLSC_HOME") is { Length: > 0 } home
            ? home
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "sql-sc");

    public static LinkStore Default => new(Path.Combine(SettingsDirectory, "links.json"));

    public string FilePath { get; } = path;

    public IReadOnlyList<Link> All() =>
        File.Exists(FilePath)
            ? JsonSerializer.Deserialize<List<Link>>(File.ReadAllText(FilePath), Json) ?? []
            : [];

    /// <summary>Adds or replaces the link for <paramref name="link"/>'s folder.</summary>
    public void Save(Link link)
    {
        var normalized = link with { Folder = Normalize(link.Folder) };
        var links = All().Where(l => !SamePath(l.Folder, normalized.Folder)).Append(normalized).ToList();
        Write(links);
    }

    public bool Remove(string folder)
    {
        var links = All().ToList();
        var removed = links.RemoveAll(l => SamePath(l.Folder, Normalize(folder)));
        Write(links);
        return removed > 0;
    }

    /// <summary>The link for <paramref name="path"/> or the closest linked folder containing it.</summary>
    public Link? FindFor(string path)
    {
        var target = Normalize(path);
        return All()
            .Where(l => SamePath(l.Folder, target) || target.StartsWith(l.Folder + Path.DirectorySeparatorChar, PathComparison))
            .MaxBy(l => l.Folder.Length);
    }

    private void Write(List<Link> links)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
        File.WriteAllText(FilePath, JsonSerializer.Serialize(links, Json));
    }

    private static string Normalize(string path) => Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));

    private static bool SamePath(string a, string b) => string.Equals(a, b, PathComparison);
}
