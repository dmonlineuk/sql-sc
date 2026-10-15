using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using SqlSc.Core.Export;
using SqlSc.Core.Modeling;
using SqlSc.Core.Scripting;
using SqlSc.Core.Settings;
using SqlSc.Core.WorkingFolders;

namespace SqlSc.Core.Comparison;

/// <summary>Which side an object changed on since the database and the folder last matched.</summary>
public enum ChangeSide
{
    /// <summary>Not known: the object hasn't matched since sql-sc started keeping a baseline.</summary>
    Unknown,

    /// <summary>Changed in the database; the folder still has the version that last matched.</summary>
    Database,

    /// <summary>Changed in the folder (e.g. by a git pull); the database still has the version that last matched.</summary>
    Folder,

    /// <summary>Changed on both sides: a conflict the user has to resolve.</summary>
    Both,
}

/// <summary>
/// Per-user baselines, one file per working folder and database, in <c>baselines</c> under <see cref="LinkStore.SettingsDirectory"/>.
/// </summary>
public sealed class BaselineStore(string directory)
{
    public static BaselineStore Default => new(Path.Combine(LinkStore.SettingsDirectory, "baselines"));

    public string Directory { get; } = directory;

    internal string PathFor(string folderRoot, string server, string database)
    {
        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(folderRoot));
        if (OperatingSystem.IsWindows())
        {
            root = root.ToUpperInvariant();
        }

        var id = SHA256.HashData(Encoding.UTF8.GetBytes($"{root}|{server.ToUpperInvariant()}|{database.ToUpperInvariant()}"));
        return Path.Combine(Directory, Convert.ToHexStringLower(id)[..32] + ".json");
    }
}

/// <summary>
/// Hashes of each object's database and folder scripts from the last time the two matched, so a difference can be put down to
/// a change in the database, in the folder, or in both. Objects that match are recorded on every comparison, and objects are
/// recorded when export or apply makes the two sides match.
/// </summary>
internal sealed class Baseline
{
    private const int Version = 1;

    /// <summary>The database hash of an object apply deployed: its database script wasn't read back.</summary>
    private const string AppliedSource = "applied";

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    private readonly string path;
    private readonly string filter;
    private readonly string source;
    private readonly bool complete;
    private readonly Dictionary<string, BaselineEntry> objects;
    private readonly HashSet<string> unknown;
    private readonly Dictionary<string, (string? Database, string? Folder)> current;

    private Baseline(string path, string filter, string source, BaselineData? saved, Dictionary<string, (string?, string?)> current)
    {
        this.path = path;
        this.filter = filter;
        this.source = source;
        this.current = current;
        complete = saved is not null && saved.Version == Version && saved.Filter == filter;
        objects = new Dictionary<string, BaselineEntry>(saved?.Version == Version ? saved.Objects : [], StringComparer.OrdinalIgnoreCase);
        unknown = new HashSet<string>(saved?.Version == Version ? saved.Unknown : [], StringComparer.OrdinalIgnoreCase);
    }

    public static Baseline Load(BaselineStore store, WorkingFolder folder, string server, string databaseName, DatabaseModel database, FolderModel folderModel)
    {
        var path = store.PathFor(folder.RootPath, server, databaseName);
        BaselineData? saved = null;
        if (File.Exists(path))
        {
            try
            {
                saved = JsonSerializer.Deserialize<BaselineData>(File.ReadAllText(path), Json);
            }
            catch (JsonException)
            {
                saved = null;
            }
        }

        var current = new Dictionary<string, (string?, string?)>(StringComparer.OrdinalIgnoreCase);
        foreach (var owner in ObjectFiles.Group(database.Model).Values.Where(o => folder.Filter.Includes(o.ObjectType, o.Owner.Name.Parts)))
        {
            current[Key(owner.ObjectType, owner.Name)] = (Hash(ScriptFileWriter.Write(ScriptFileWriter.Statements(owner, database.OriginalScripts))), null);
        }

        foreach (var owner in ObjectFiles.Group(folderModel.Model, DatabaseReferences.IsBorrowed).Values.Where(o => folder.Filter.Includes(o.ObjectType, o.Owner.Name.Parts)))
        {
            var key = Key(owner.ObjectType, owner.Name);
            var text = string.Concat(owner.Files.Keys.Order(StringComparer.OrdinalIgnoreCase).Select(folder.ReadScript));
            current[key] = (current.GetValueOrDefault(key).Item1, Hash(text));
        }

        var filter = folder.FilterPath is { } filterPath ? Hash(folder.ReadScript(filterPath)) : string.Empty;
        return new Baseline(path, filter, database.Info.FromCatalog ? "catalog" : "extract", saved, current);
    }

    public ChangeSide SideOf(ObjectChange change)
    {
        var key = Key(change.ObjectType, change.Name);
        if (Previous(key) is not { } entry)
        {
            return ChangeSide.Unknown;
        }

        var (database, folder) = current.GetValueOrDefault(key);
        var folderChanged = folder != entry.Folder;
        bool? databaseChanged = entry is { Database: null, Folder: null } ? database is not null
            : entry.Source == source ? database != entry.Database
            : null;
        return (databaseChanged, folderChanged) switch
        {
            (true, true) => ChangeSide.Both,
            (true, false) or (null, false) => ChangeSide.Database,
            (false, true) => ChangeSide.Folder,
            _ => ChangeSide.Unknown,
        };
    }

    /// <summary>Records every object that now matches, and keeps the previous baseline of those that differ.</summary>
    public void Refresh(IReadOnlyList<ObjectChange> changes)
    {
        var changed = changes.Select(c => Key(c.ObjectType, c.Name)).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var previous = changed.ToDictionary(k => k, Previous, StringComparer.OrdinalIgnoreCase);
        objects.Clear();
        unknown.Clear();
        foreach (var (key, (database, folder)) in current.Where(c => !changed.Contains(c.Key)))
        {
            objects[key] = new BaselineEntry(database, folder, source);
        }

        foreach (var (key, entry) in previous)
        {
            if (entry is null)
            {
                unknown.Add(key);
            }
            else
            {
                objects[key] = entry;
            }
        }
    }

    /// <summary>Records objects export wrote to <paramref name="text"/> (null if it deleted their file).</summary>
    public void Exported(IEnumerable<(string ObjectType, string Name)> keys, string? text)
    {
        foreach (var (objectType, name) in keys)
        {
            var key = Key(objectType, name);
            Set(key, current.GetValueOrDefault(key).Database, text is null ? null : Hash(text), source);
        }
    }

    /// <summary>Records objects apply deployed from the folder, or dropped.</summary>
    public void Applied(IEnumerable<(string ObjectType, string Name, bool Dropped)> applied)
    {
        foreach (var (objectType, name, dropped) in applied)
        {
            var key = Key(objectType, name);
            Set(key, null, dropped ? null : current.GetValueOrDefault(key).Folder, AppliedSource);
        }
    }

    public void Save()
    {
        System.IO.Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var data = new BaselineData(
            Version,
            filter,
            new SortedDictionary<string, BaselineEntry>(objects, StringComparer.OrdinalIgnoreCase),
            [.. unknown.Order(StringComparer.OrdinalIgnoreCase)]);
        var temporary = path + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(data, Json));
        File.Move(temporary, path, overwrite: true);
    }

    /// <summary>
    /// The object's baseline, or null if it isn't known. An object the last complete comparison didn't see was in neither the
    /// database nor the folder.
    /// </summary>
    private BaselineEntry? Previous(string key) =>
        objects.TryGetValue(key, out var entry) ? entry
        : complete && !unknown.Contains(key) ? new BaselineEntry(null, null, source)
        : null;

    private void Set(string key, string? database, string? folder, string entrySource)
    {
        unknown.Remove(key);
        if (database is null && folder is null)
        {
            objects.Remove(key);
        }
        else
        {
            objects[key] = new BaselineEntry(database, folder, entrySource);
        }
    }

    private static string Key(string objectType, string name) => $"{objectType}|{name}";

    /// <summary>A hash of a script that ignores layout: runs of whitespace count as one space.</summary>
    internal static string Hash(string script)
    {
        var normalized = string.Join(' ', script.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(normalized)))[..32];
    }

    /// <summary>Hashes of an object's scripts when the sides last matched; null where it didn't exist.</summary>
    /// <param name="Source">How the database hash was made: <c>catalog</c>, <c>extract</c> or <c>applied</c>.</param>
    internal sealed record BaselineEntry(string? Database, string? Folder, string Source);

    internal sealed record BaselineData(int Version, string Filter, IDictionary<string, BaselineEntry> Objects, IReadOnlyList<string> Unknown);
}

/// <summary>Keeps export and apply from undoing a change made on the side they overwrite since the two sides last matched.</summary>
internal static class SideGuard
{
    /// <param name="overwritten">The side the command overwrites: <see cref="ChangeSide.Folder"/> for export, <see cref="ChangeSide.Database"/> for apply.</param>
    /// <param name="named">Names given on the command line; other objects that would undo a change are skipped rather than refused.</param>
    /// <param name="force">Overwrite regardless.</param>
    public static List<ObjectChange> Filter(
        IEnumerable<ObjectChange> selected,
        ChangeSide overwritten,
        IReadOnlySet<string> named,
        bool force,
        List<string> problems,
        List<string> skipped)
    {
        var kept = new List<ObjectChange>();
        foreach (var change in selected)
        {
            var reason = force ? null : change.Side switch
            {
                ChangeSide.Both => $"{change.Name} changed in both the database and the folder since they last matched (conflict). Keep the database version with `export {change.Name} --force`, or the folder version with `apply {change.Name} --force`.",
                ChangeSide.Folder when overwritten == ChangeSide.Folder => $"{change.Name} changed in the folder since it last matched the database, so exporting it would undo that change. Apply it instead, or export it with --force to keep the database version.",
                ChangeSide.Database when overwritten == ChangeSide.Database => $"{change.Name} changed in the database since it last matched the folder, so applying it would undo that change. Export it instead, or apply it with --force to keep the folder version.",
                _ => null,
            };
            if (reason is null)
            {
                kept.Add(change);
            }
            else if (change.Side == ChangeSide.Both || named.Contains(change.Name))
            {
                problems.Add(reason);
            }
            else
            {
                skipped.Add(reason);
            }
        }

        return kept;
    }
}
