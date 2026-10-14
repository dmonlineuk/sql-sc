namespace SqlSc.Core.ChangeTracking;

/// <summary>Who changed which object: from sql-sc's change log when it is installed in the database, otherwise from the default trace.</summary>
public static class ChangeHistory
{
    public const string ChangeLogSource = "change log";

    public const string DefaultTraceSource = "default trace";

    public static ChangeLog Read(string connectionString)
    {
        if (ChangeLogTable.Read(connectionString) is { } changeLog)
        {
            return changeLog;
        }

        var trace = DefaultTraceReader.Read(connectionString);
        return trace.Available
            ? trace
            : trace with { UnavailableReason = $"{trace.UnavailableReason} Run `sql-sc changelog install` (needs db_owner) to record who changes objects." };
    }
}
