namespace SqlSc.Core.Modeling;

public enum IssueSeverity
{
    Warning,
    Error,
}

/// <summary>A problem found while loading scripts into a schema model. <see cref="File"/> is relative to the working folder.</summary>
public sealed record LoadIssue(IssueSeverity Severity, string? File, int? Line, string Message);
