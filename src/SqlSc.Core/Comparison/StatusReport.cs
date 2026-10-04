using SqlSc.Core.ChangeTracking;
using SqlSc.Core.Modeling;
using SqlSc.Core.Scripting;

namespace SqlSc.Core.Comparison;

public enum ObjectStatus
{
    /// <summary>Exists in the database but not in the working folder.</summary>
    New,

    /// <summary>Exists in both but differs.</summary>
    Modified,

    /// <summary>Exists in the working folder but has been dropped from the database.</summary>
    Deleted,
}

/// <summary>
/// A changed child of an object, such as a constraint, index, extended property or permission.
/// <see cref="Difference"/> is where a modified child's database and folder scripts first differ, when requested.
/// </summary>
public sealed record ChildChange(ObjectStatus Status, string ObjectType, string Name, string? Difference = null);

/// <summary>
/// A changed object, with its changed children grouped under it as in the object's script file.
/// An object whose only changes are to its children is reported as <see cref="ObjectStatus.Modified"/>.
/// <see cref="Difference"/> is where a modified object's database and folder scripts first differ, when requested.
/// </summary>
public sealed record ObjectChange(
    ObjectStatus Status,
    string ObjectType,
    string Name,
    string? File,
    ChangeEvent? LastChange,
    IReadOnlyList<ChildChange> Children,
    string? Difference = null);

public sealed record StatusReport(
    string Server,
    string Database,
    string Platform,
    int FolderObjectCount,
    string? FilterPath,
    IReadOnlyList<LoadIssue> LoadIssues,
    IReadOnlyList<ObjectChange> Changes,
    ChangeLog ChangeLog,
    IReadOnlyDictionary<string, TimeSpan> Timings,
    DatabaseModelInfo DatabaseModel);
