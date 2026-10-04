using Microsoft.SqlServer.Dac.Model;
using SqlSc.Core.Comparison;
using SqlSc.Core.Modeling;

namespace SqlSc.Core.Export;

/// <summary>An object with its own script file, and every object whose script goes in that file.</summary>
internal sealed class FileOwner(string objectType, string name, TSqlObject owner)
{
    public string ObjectType { get; } = objectType;

    public string Name { get; } = name;

    public TSqlObject Owner { get; } = owner;

    public List<TSqlObject> Members { get; } = [];

    /// <summary>The files, relative to the working folder, the members' scripts were loaded from, with the first line of each.</summary>
    public Dictionary<string, int> Files { get; } = new(StringComparer.OrdinalIgnoreCase);

    public (string ObjectType, string Name) Key => (ObjectType, Name);
}

/// <summary>Groups a model's objects by the script file they belong in, as Redgate lays them out.</summary>
internal static class ObjectFiles
{
    public static Dictionary<(string ObjectType, string Name), FileOwner> Group(TSqlModel model, Func<TSqlObject, bool>? skip = null)
    {
        var owners = new Dictionary<(string, string), FileOwner>(KeyComparer.Instance);
        foreach (var obj in AllObjects(model))
        {
            if (skip?.Invoke(obj) == true)
            {
                continue;
            }

            var owner = FileOwnerOf(obj);
            if (DatabaseReferences.IsInfrastructure(owner) || StatusService.FormatName(owner.Name) is not { } name)
            {
                continue;
            }

            var key = (owner.ObjectType.Name, name);
            if (!owners.TryGetValue(key, out var entry))
            {
                owners[key] = entry = new FileOwner(owner.ObjectType.Name, name, owner);
            }

            entry.Members.Add(obj);
            if (ScriptSource.Decode(obj.GetSourceInformation()?.SourceName) is var (file, line))
            {
                entry.Files[file] = Math.Min(entry.Files.GetValueOrDefault(file, int.MaxValue), line);
            }
        }

        return owners;
    }

    /// <summary>
    /// The object whose file holds <paramref name="obj"/>. As in Redgate, database-level permissions go in the grantee's file;
    /// everything else follows <see cref="StatusService.OwnerOf"/>.
    /// </summary>
    public static TSqlObject FileOwnerOf(TSqlObject obj)
    {
        var owner = StatusService.OwnerOf(obj);
        if (owner.ObjectType.Name == "DatabaseOptions" && obj.ObjectType.Name == "Permission"
            && obj.GetReferencedRelationshipInstances(DacQueryScopes.All).FirstOrDefault(r => r.Relationship.Name == "Grantee")?.Object is { } grantee)
        {
            return StatusService.OwnerOf(grantee);
        }

        return owner;
    }

    private static IEnumerable<TSqlObject> AllObjects(TSqlModel model)
    {
        var seen = new HashSet<TSqlObject>();
        var stack = new Stack<TSqlObject>(model.GetObjects(DacQueryScopes.UserDefined));
        while (stack.TryPop(out var obj))
        {
            if (!seen.Add(obj))
            {
                continue;
            }

            yield return obj;
            foreach (var child in obj.GetChildren(DacQueryScopes.UserDefined))
            {
                stack.Push(child);
            }
        }
    }

    internal sealed class KeyComparer : IEqualityComparer<(string ObjectType, string Name)>
    {
        public static readonly KeyComparer Instance = new();

        public bool Equals((string ObjectType, string Name) x, (string ObjectType, string Name) y) =>
            string.Equals(x.ObjectType, y.ObjectType, StringComparison.Ordinal) && string.Equals(x.Name, y.Name, StringComparison.OrdinalIgnoreCase);

        public int GetHashCode((string ObjectType, string Name) obj) =>
            HashCode.Combine(obj.ObjectType, StringComparer.OrdinalIgnoreCase.GetHashCode(obj.Name));
    }
}
