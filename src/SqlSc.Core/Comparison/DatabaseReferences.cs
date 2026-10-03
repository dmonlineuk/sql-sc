using Microsoft.SqlServer.Dac.Model;
using SqlSc.Core.Modeling;

namespace SqlSc.Core.Comparison;

/// <summary>Objects copied from the database model into a folder model.</summary>
/// <param name="Objects">Named application objects that were copied (excludes server-level and infrastructure objects).</param>
/// <param name="Count">Every object copied, including unnamed and infrastructure ones.</param>
public sealed record BorrowedObjects(IReadOnlyList<TSqlObject> Objects, int Count);

/// <summary>
/// A working folder rarely builds on its own: logins, credentials, filtered-out objects and objects nobody has
/// committed yet are referenced but not scripted. Copying every object the folder lacks from the live database
/// makes the folder model buildable, and the copied objects are exactly the ones that are "new in the database".
/// </summary>
public static class DatabaseReferences
{
    public const string SourcePrefix = "db:";

    private static readonly HashSet<string> ServerScopedTypes = new(StringComparer.Ordinal)
    {
        "Credential", "Endpoint", "ErrorMessage", "EventSession", "LinkedServer", "LinkedServerLogin", "Login",
        "ServerAudit", "ServerAuditSpecification", "ServerDdlTrigger", "ServerEventNotification", "ServerOptions",
        "ServerRole", "ServerRoleMembership",
    };

    /// <summary>Types whose names share one namespace per schema (sys.objects), so two of them can't have the same name.</summary>
    private static readonly HashSet<string> SchemaObjectTypes = new(StringComparer.Ordinal)
    {
        "Table", "ExternalTable", "View", "Procedure", "ScalarFunction", "TableValuedFunction", "AggregateFunction",
        "Synonym", "Sequence", "PrimaryKeyConstraint", "UniqueConstraint", "ForeignKeyConstraint", "CheckConstraint",
        "DefaultConstraint", "DmlTrigger", "ExtendedProcedure", "Queue", "Rule", "Default",
    };

    /// <summary>Objects that live outside the database or are created by SQL Server itself, so never belong in source control.</summary>
    public static bool IsInfrastructure(TSqlObject obj) =>
        ServerScopedTypes.Contains(obj.ObjectType.Name)
        || obj.ObjectType.Name is "SqlFile" or "DatabaseOptions" or "MasterKey"
        || (obj.ObjectType.Name == "Route" && obj.Name.Parts is ["AutoCreatedLocal"]);

    public static BorrowedObjects AddMissing(TSqlModel folderModel, TSqlModel databaseModel)
    {
        var inFolder = folderModel.GetObjects(DacQueryScopes.UserDefined)
            .Where(o => o.Name.HasName)
            .Select(Key)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var schemaObjectsInFolder = folderModel.GetObjects(DacQueryScopes.UserDefined)
            .Where(IsSchemaObject)
            .Select(o => o.Name.ToString())
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var copied = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var objects = new List<TSqlObject>();
        var count = 0;
        foreach (var obj in databaseModel.GetObjects(DacQueryScopes.UserDefined))
        {
            var parent = obj.GetParent();
            var copy = obj.Name.HasName
                ? !inFolder.Contains(Key(obj)) && !(IsSchemaObject(obj) && schemaObjectsInFolder.Contains(obj.Name.ToString()))
                : parent is { Name.HasName: true } && copied.Contains(Key(parent));
            if (!copy || !obj.TryGetScript(out var script))
            {
                continue;
            }

            folderModel.AddOrUpdateObjects(script, SourcePrefix + count, new TSqlObjectOptions());
            count++;
            if (obj.Name.HasName)
            {
                copied.Add(Key(obj));
                if (!IsInfrastructure(obj))
                {
                    objects.Add(obj);
                }
            }
        }

        SystemDatabase.AddMissingLogins(folderModel);
        SystemDatabase.AddMissingMasterKey(folderModel);
        return new BorrowedObjects(objects, count);
    }

    public static bool IsBorrowed(TSqlObject? obj) =>
        obj?.GetSourceInformation()?.SourceName?.StartsWith(SourcePrefix, StringComparison.Ordinal) == true;

    private static bool IsSchemaObject(TSqlObject obj) => obj.Name.HasName && SchemaObjectTypes.Contains(obj.ObjectType.Name);

    private static string Key(TSqlObject obj) => $"{obj.ObjectType.Name}|{obj.Name}";
}
