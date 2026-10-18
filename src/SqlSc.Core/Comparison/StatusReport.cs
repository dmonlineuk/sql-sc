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
/// <see cref="Side"/> is which side changed since the two last matched. <see cref="Lock"/> is the soft lock on the object, if any.
/// </summary>
public sealed record ObjectChange(
    ObjectStatus Status,
    string ObjectType,
    string Name,
    string? File,
    ChangeEvent? LastChange,
    IReadOnlyList<ChildChange> Children,
    string? Difference = null,
    ChangeSide Side = ChangeSide.Unknown,
    ObjectLock? Lock = null);

/// <summary>
/// A row of a static data table that differs, by its primary key. <see cref="ObjectStatus.New"/> rows are only in the database and
/// <see cref="ObjectStatus.Deleted"/> rows only in the folder. <see cref="Columns"/> are the columns that differ in a modified row.
/// </summary>
public sealed record RowChange(ObjectStatus Status, string Key, IReadOnlyList<string> Columns);

/// <summary>
/// How a table tracked for static data differs between the database and its data script (<see cref="File"/>, relative to the
/// working folder). <see cref="Rows"/> lists at most <see cref="MaxRows"/> rows of each status; the counts are exact.
/// <see cref="Problem"/> says why the table couldn't be compared.
/// </summary>
public sealed record DataChange(string Table, string File, int New, int Modified, int Deleted, IReadOnlyList<RowChange> Rows, string? Problem = null)
{
    public const int MaxRows = 1000;

    public bool HasChanges => New + Modified + Deleted > 0;
}

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
    DatabaseModelInfo DatabaseModel,
    IReadOnlyList<DataChange> Data,
    IReadOnlyList<ObjectLock>? Locks = null);
