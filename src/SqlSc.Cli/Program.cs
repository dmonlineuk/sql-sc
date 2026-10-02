using System.CommandLine;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using SqlSc.Core.ChangeTracking;
using SqlSc.Core.Comparison;
using SqlSc.Core.Modeling;
using SqlSc.Core.WorkingFolders;

var json = new JsonSerializerOptions
{
    WriteIndented = true,
    Converters = { new JsonStringEnumConverter() },
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
};

var folderArgument = new Argument<DirectoryInfo>("folder") { Description = "Working folder containing the object scripts." };
var jsonOption = new Option<bool>("--json") { Description = "Write machine-readable JSON." };
var platformOption = new Option<string?>("--platform") { Description = "Target platform: 2016-2025, azure or mi. Defaults to RedGateDatabaseInfo.xml or SQL Server 2022." };
var connectionOption = new Option<string?>("--connection", "-c")
{
    Description = "SQL Server connection string. Defaults to the SQLSC_CONNECTION environment variable. "
        + "Supports SQL auth, Integrated Security=true and Authentication=Active Directory Default/Interactive.",
};
var noChangedByOption = new Option<bool>("--no-changed-by") { Description = "Skip reading the default trace." };

var load = new Command("load", "Load a working folder into a schema model and report any problems.")
{
    folderArgument, platformOption, jsonOption,
};
load.SetAction(result =>
{
    var folder = WorkingFolder.Open(result.GetRequiredValue(folderArgument).FullName);
    var platform = result.GetValue(platformOption) is { } p ? TargetPlatform.Parse(p) : (Microsoft.SqlServer.Dac.Model.SqlServerVersion?)null;
    using var model = FolderModelLoader.Load(folder, platform);

    if (result.GetValue(jsonOption))
    {
        Console.WriteLine(JsonSerializer.Serialize(new { model.Platform, model.FileCount, model.ObjectCount, model.Issues }, json));
    }
    else
    {
        Console.WriteLine(Invariant($"Loaded {model.FileCount} files into {model.ObjectCount} objects ({model.Platform})."));
        WriteIssues(model.Issues);
    }

    return model.HasErrors ? 1 : 0;
});

var status = new Command("status", "Show objects that differ between the database and the working folder.")
{
    folderArgument, connectionOption, noChangedByOption, jsonOption,
};
status.SetAction(result =>
{
    var connection = ResolveConnection(result.GetValue(connectionOption));
    if (connection is null)
    {
        return 2;
    }

    var folder = WorkingFolder.Open(result.GetRequiredValue(folderArgument).FullName);
    var report = StatusService.GetStatus(folder, connection, includeChangedBy: !result.GetValue(noChangedByOption));

    if (result.GetValue(jsonOption))
    {
        Console.WriteLine(JsonSerializer.Serialize(report with { ChangeLog = report.ChangeLog with { Events = [] } }, json));
        return 0;
    }

    Console.WriteLine(Invariant($"{report.Server}/{report.Database} ({report.Platform}) vs {folder.RootPath}"));
    WriteIssues(report.LoadIssues);
    if (!report.ChangeLog.Available)
    {
        Console.WriteLine($"Changed by: unknown ({report.ChangeLog.UnavailableReason})");
    }

    if (report.Changes.Count == 0)
    {
        Console.WriteLine("No differences.");
    }

    foreach (var change in report.Changes)
    {
        var who = change.LastChange is { } e ? Invariant($"{e.LoginName} {e.StartTime:yyyy-MM-dd HH:mm}") : "Unknown";
        Console.WriteLine($"  {change.Status,-9} {change.ObjectType,-24} {change.Name,-50} {who}");
    }

    Console.WriteLine(string.Join(", ", report.Timings.Select(t => Invariant($"{t.Key} {t.Value.TotalSeconds:0.00}s"))));
    return 0;
});

var changes = new Command("changes", "List recent object changes recorded in the default trace.")
{
    connectionOption, jsonOption,
};
changes.SetAction(result =>
{
    var connection = ResolveConnection(result.GetValue(connectionOption));
    if (connection is null)
    {
        return 2;
    }

    var log = DefaultTraceReader.Read(connection);
    if (result.GetValue(jsonOption))
    {
        Console.WriteLine(JsonSerializer.Serialize(log, json));
    }
    else if (!log.Available)
    {
        Console.WriteLine($"Default trace unavailable: {log.UnavailableReason}");
    }
    else
    {
        foreach (var e in log.Events)
        {
            Console.WriteLine(Invariant($"  {e.StartTime:yyyy-MM-dd HH:mm:ss} {e.Kind,-8} {e.Key,-50} {e.LoginName} ({e.HostName}, {e.ApplicationName})"));
        }
    }

    return log.Available ? 0 : 1;
});

var root = new RootCommand("sql-sc: database-first source control for SQL Server.") { load, status, changes };
return root.Parse(args).Invoke();

static string? ResolveConnection(string? value)
{
    var connection = value ?? Environment.GetEnvironmentVariable("SQLSC_CONNECTION");
    if (string.IsNullOrWhiteSpace(connection))
    {
        Console.Error.WriteLine("No connection string. Pass --connection or set SQLSC_CONNECTION.");
        return null;
    }

    return connection;
}

static void WriteIssues(IReadOnlyList<LoadIssue> issues)
{
    foreach (var issue in issues)
    {
        var location = issue.File is null ? "" : issue.Line is { } line ? Invariant($"{issue.File}({line}): ") : $"{issue.File}: ";
        Console.WriteLine($"  {issue.Severity}: {location}{issue.Message}");
    }
}

static string Invariant(FormattableString value) => value.ToString(CultureInfo.InvariantCulture);
