using Microsoft.SqlServer.Dac.Model;
using SqlSc.Core.Comparison;
using SqlSc.Core.Filtering;

namespace SqlSc.Core.Modeling;

/// <summary>
/// A database can hold objects whose references don't resolve, such as a view over a dropped table, but DacFx refuses
/// to save a model containing them as a .dacpac. Untracked ones don't affect status, so they can be left out.
/// </summary>
public static class BrokenObjects
{
    /// <summary>
    /// Removes untracked objects with unresolved references, and the untracked objects that use them. Objects that a tracked
    /// object uses are kept. Returns the names of the removed objects.
    /// </summary>
    public static IReadOnlyList<string> RemoveUntracked(TSqlModel model, ObjectFilter filter)
    {
        bool Tracked(TSqlObject obj)
        {
            var owner = StatusService.OwnerOf(obj);
            return filter.Includes(owner.ObjectType.Name, owner.Name.Parts);
        }

        var objects = model.GetObjects(DacQueryScopes.UserDefined).Where(o => o.Name.HasName).ToList();
        var remove = new Dictionary<string, TSqlObject>(StringComparer.OrdinalIgnoreCase);
        foreach (var root in objects.Where(o => o.Name.Parts.Count >= 2 && !Tracked(o) && IsBroken(o)))
        {
            var closure = Dependents(root);
            if (closure.Values.All(o => !Tracked(o)))
            {
                foreach (var (key, obj) in closure)
                {
                    remove[key] = obj;
                }
            }
        }

        bool Removed(TSqlObject o) => remove.ContainsKey(Key(o)) || remove.ContainsKey(Key(StatusService.OwnerOf(o)));

        var sources = model.GetObjects(DacQueryScopes.UserDefined)
            .GroupBy(o => o.GetSourceInformation()?.SourceName, StringComparer.Ordinal)
            .Where(g => g.Key is not null && g.All(Removed));
        var removed = new List<string>();
        foreach (var source in sources)
        {
            model.DeleteObjects(source.Key);
            removed.AddRange(source.Where(o => remove.ContainsKey(Key(o)) && Key(StatusService.OwnerOf(o)) == Key(o)).Select(o => StatusService.FormatName(o.Name)!));
        }

        removed.Sort(StringComparer.Ordinal);
        return removed;
    }

    private static bool IsBroken(TSqlObject obj) =>
        obj.GetReferencedRelationshipInstances(DacQueryScopes.All).Any(r => r.Object is null && r.ObjectName is { HasName: true })
        || obj.GetChildren(DacQueryScopes.UserDefined).Any(IsBroken);

    private static Dictionary<string, TSqlObject> Dependents(TSqlObject root)
    {
        var found = new Dictionary<string, TSqlObject>(StringComparer.OrdinalIgnoreCase) { [Key(root)] = root };
        var queue = new Queue<TSqlObject>([root]);
        while (queue.TryDequeue(out var current))
        {
            foreach (var user in current.GetReferencing(DacQueryScopes.UserDefined).Select(Top).Where(o => o.Name.HasName))
            {
                if (found.TryAdd(Key(user), user))
                {
                    queue.Enqueue(user);
                }
            }
        }

        return found;
    }

    private static TSqlObject Top(TSqlObject obj)
    {
        var current = obj;
        while (current.GetParent() is { } parent && parent.ObjectType.Name != "Schema")
        {
            current = parent;
        }

        return current;
    }

    private static string Key(TSqlObject obj) => $"{obj.ObjectType.Name}|{obj.Name}";
}
