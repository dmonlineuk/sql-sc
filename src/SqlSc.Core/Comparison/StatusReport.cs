using SqlSc.Core.ChangeTracking;
using SqlSc.Core.Modeling;

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

public sealed record ObjectChange(
    ObjectStatus Status,
    string ObjectType,
    string Name,
    string? File,
    ChangeEvent? LastChange);

public sealed record StatusReport(
    string Server,
    string Database,
    string Platform,
    int FolderObjectCount,
    IReadOnlyList<LoadIssue> LoadIssues,
    IReadOnlyList<ObjectChange> Changes,
    ChangeLog ChangeLog,
    IReadOnlyDictionary<string, TimeSpan> Timings);
