using System.CommandLine;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using SqlSc.Core.Apply;
using SqlSc.Core.ChangeTracking;
using SqlSc.Core.Comparison;
using SqlSc.Core.Database;
using SqlSc.Core.Diagnostics;
using SqlSc.Core.Export;
using SqlSc.Core.Modeling;
using SqlSc.Core.Settings;
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
    Description = "SQL Server connection string. Defaults to the SQLSC_CONNECTION environment variable, then the folder's link. "
        + "Use -c on its own to take it from SQLSC_CONNECTION. "
        + "Supports SQL auth, Integrated Security=true and Authentication=Active Directory Default/Interactive. "
        + "SQL auth passwords can come from SQLSC_PASSWORD.",
    Arity = ArgumentArity.ZeroOrOne,
};
var modeOption = new Option<DatabaseMode>("--mode") { Description = "shared (one database for the team) or dedicated (your own database).", DefaultValueFactory = _ => DatabaseMode.Shared };
var removeOption = new Option<bool>("--remove") { Description = "Remove the folder's link." };
var optionalFolderArgument = new Argument<DirectoryInfo?>("folder") { Description = "Working folder. Defaults to the current folder.", Arity = ArgumentArity.ZeroOrOne };
var noChangedByOption = new Option<bool>("--no-changed-by") { Description = "Skip reading the default trace." };
var diffOption = new Option<bool>("--diff") { Description = "For each modified object, show the first line where the database and folder scripts differ." };
var fullExtractOption = new Option<bool>("--full-extract") { Description = "Read the whole database with DacFx instead of scripting only tracked objects from the catalog." };

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
    folderArgument, connectionOption, noChangedByOption, fullExtractOption, diffOption, jsonOption,
};
status.SetAction(result =>
{
    var folderPath = result.GetRequiredValue(folderArgument).FullName;
    var connection = ResolveConnection(result.GetValue(connectionOption), folderPath);
    if (connection is null)
    {
        return 2;
    }

    var folder = WorkingFolder.Open(folderPath);
    var report = StatusService.GetStatus(
        folder,
        connection,
        includeChangedBy: !result.GetValue(noChangedByOption),
        fullExtract: result.GetValue(fullExtractOption),
        includeDifferences: result.GetValue(diffOption));

    if (result.GetValue(jsonOption))
    {
        Console.WriteLine(JsonSerializer.Serialize(report with { ChangeLog = report.ChangeLog with { Events = [] } }, json));
        return 0;
    }

    Console.WriteLine(Invariant($"{report.Server}/{report.Database} ({report.Platform}) vs {folder.RootPath}"));
    if (report.FilterPath is not null)
    {
        Console.WriteLine($"Filter: {report.FilterPath}");
    }

    Console.WriteLine($"Database model: {report.DatabaseModel.Describe()}");
    WriteIssues(report.LoadIssues);
    if (!report.ChangeLog.Available && !result.GetValue(noChangedByOption))
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
        if (change.Difference is not null)
        {
            Console.WriteLine($"      First difference: {change.Difference}");
        }

        foreach (var child in change.Children)
        {
            Console.WriteLine($"      {child.Status,-9} {child.ObjectType,-20} {child.Name}");
            if (child.Difference is not null)
            {
                Console.WriteLine($"          First difference: {child.Difference}");
            }
        }
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
    var connection = ResolveConnection(result.GetValue(connectionOption), Environment.CurrentDirectory);
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

var link = new Command("link", "Link a working folder to a database for the current user. Without --connection, shows the current link.")
{
    folderArgument, connectionOption, modeOption, removeOption,
};
link.SetAction(result =>
{
    var store = LinkStore.Default;
    var folderPath = result.GetRequiredValue(folderArgument).FullName;
    if (result.GetValue(removeOption))
    {
        Console.WriteLine(store.Remove(folderPath) ? $"Removed link for {folderPath}." : $"{folderPath} is not linked.");
        return 0;
    }

    var connection = result.GetValue(connectionOption);
    if (connection is null && result.GetResult(connectionOption) is not null)
    {
        connection = Environment.GetEnvironmentVariable("SQLSC_CONNECTION");
        if (string.IsNullOrWhiteSpace(connection))
        {
            Console.Error.WriteLine("-c was given without a connection string and SQLSC_CONNECTION is not set.");
            return 2;
        }
    }

    if (connection is null)
    {
        if (store.FindFor(folderPath) is { } existing)
        {
            Console.WriteLine($"{existing.Folder} -> {ConnectionStrings.Describe(existing.Connection)} ({existing.Mode}, {ConnectionStrings.DescribeAuthentication(existing.Connection)})");
            return 0;
        }

        Console.Error.WriteLine($"{folderPath} is not linked. Pass --connection \"...\", or -c on its own to use SQLSC_CONNECTION.");
        return 1;
    }

    WorkingFolder.Open(folderPath);
    var stored = ConnectionStrings.WithoutPassword(connection, out var removed);
    var saved = new Link(folderPath, stored, result.GetValue(modeOption));
    store.Save(saved);
    Console.WriteLine($"Linked {folderPath} to {ConnectionStrings.Describe(stored)} ({saved.Mode}, {ConnectionStrings.DescribeAuthentication(stored)}).");
    Console.WriteLine($"Saved in {store.FilePath}.");
    if (removed)
    {
        Console.WriteLine($"The password was not saved. Set {ConnectionStrings.PasswordVariable} before running sql-sc.");
    }

    return 0;
});

var noExtractOption = new Option<bool>("--no-extract") { Description = "Skip the DacFx schema extract, which can take minutes on large databases." };
var doctor = new Command("doctor", "Check the connection, permissions, default trace and working folder. The output is safe to share.")
{
    optionalFolderArgument, connectionOption, jsonOption, noExtractOption,
};
doctor.SetAction(result =>
{
    var folderPath = result.GetValue(optionalFolderArgument)?.FullName;
    var connection = ResolveConnection(result.GetValue(connectionOption), folderPath ?? Environment.CurrentDirectory, required: false);
    WorkingFolder? folder = null;
    var checks = new List<DoctorCheck>();
    if (folderPath is not null)
    {
        try
        {
            folder = WorkingFolder.Open(folderPath);
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException)
        {
            checks.Add(new DoctorCheck("Working folder", CheckResult.Failed, ex.Message));
        }
    }

    checks.AddRange(Doctor.Run(connection, folder, extract: !result.GetValue(noExtractOption)));
    var failed = checks.Any(c => c.Result == CheckResult.Failed);
    if (result.GetValue(jsonOption))
    {
        Console.WriteLine(JsonSerializer.Serialize(new { Version = typeof(Doctor).Assembly.GetName().Version?.ToString(), Os = Environment.OSVersion.ToString(), Checks = checks }, json));
        return failed ? 1 : 0;
    }

    Console.WriteLine($"sql-sc {typeof(Doctor).Assembly.GetName().Version} on {Environment.OSVersion}, .NET {Environment.Version}");
    foreach (var check in checks)
    {
        Console.WriteLine($"  [{check.Result.ToString().ToUpperInvariant(),-7}] {check.Name,-15} {check.Detail}");
    }

    return failed ? 1 : 0;
});

var objectsArgument = new Argument<string[]>("objects") { Description = "Objects to export, e.g. Sales.Customer or [Sales].[Customer].", Arity = ArgumentArity.ZeroOrMore };
var allOption = new Option<bool>("--all") { Description = "Every object that differs between the database and the folder." };
var mineOption = new Option<bool>("--mine") { Description = "Objects whose last change in the database was made by your login (needs the default trace)." };
var messageOption = new Option<string>("--message", "-m") { Description = "Commit message.", Required = true };
var dryRunOption = new Option<bool>("--dry-run") { Description = "Show the diff of each file export would write or delete, without changing anything." };

var export = new Command("export", "Write objects from the database into the working folder. Files of objects dropped from the database are deleted.")
{
    folderArgument, objectsArgument, allOption, mineOption, dryRunOption, connectionOption, fullExtractOption, jsonOption,
};
export.SetAction(result =>
{
    if (Selection(result) is not { } selection || Connect(result) is not var (folder, connection))
    {
        return 2;
    }

    var dryRun = result.GetValue(dryRunOption);
    var exported = ExportService.Export(folder, connection, selection, result.GetValue(fullExtractOption), dryRun: dryRun);
    if (result.GetValue(jsonOption))
    {
        Console.WriteLine(JsonSerializer.Serialize(new { exported.Files, exported.Problems }, json));
    }
    else
    {
        WriteExport(exported, dryRun);
    }

    return exported.Problems.Count > 0 ? 1 : 0;
});

var commit = new Command("commit", "Export objects, then git commit exactly their files. Nothing else that is staged or changed is committed.")
{
    folderArgument, objectsArgument, messageOption, allOption, mineOption, connectionOption, fullExtractOption, jsonOption,
};
commit.SetAction(result =>
{
    if (Selection(result) is not { } selection || Connect(result) is not var (folder, connection))
    {
        return 2;
    }

    var committed = CommitService.Commit(folder, connection, selection, result.GetRequiredValue(messageOption), result.GetValue(fullExtractOption));
    if (result.GetValue(jsonOption))
    {
        Console.WriteLine(JsonSerializer.Serialize(new { committed.Export.Files, committed.Export.Problems, committed.CommittedFiles, committed.Commit }, json));
    }
    else
    {
        WriteExport(committed.Export);
        Console.WriteLine(committed.Commit is { } hash
            ? Invariant($"Committed {committed.CommittedFiles.Count} file(s) as {hash}.")
            : committed.Export.Problems.Count > 0 ? "Nothing written or committed." : "Nothing to commit.");
    }

    return committed.Export.Problems.Count > 0 ? 1 : 0;
});

var applyObjectsArgument = new Argument<string[]>("objects") { Description = "Objects to apply, e.g. Sales.Customer or [Sales].[Customer]. Naming an object that is only in the database drops it.", Arity = ArgumentArity.ZeroOrMore };
var applyAllOption = new Option<bool>("--all") { Description = "Every object that differs, except objects only in the database, which are dropped only when named." };
var scriptOnlyOption = new Option<bool>("--script-only") { Description = "Print the deployment script and change nothing." };
var allowDataLossOption = new Option<bool>("--allow-data-loss") { Description = "Allow changes that could lose data, such as dropping a column, or a table that has rows." };
var forceOption = new Option<bool>("--force") { Description = "Apply objects even if another login made their last change in the database (needs the default trace)." };
var apply = new Command("apply", "Deploy the working folder's version of objects to the database (Get latest). In shared mode this changes the database for everyone.")
{
    folderArgument, applyObjectsArgument, applyAllOption, scriptOnlyOption, allowDataLossOption, forceOption, connectionOption, fullExtractOption, jsonOption,
};
apply.SetAction(result =>
{
    var names = result.GetValue(applyObjectsArgument) ?? [];
    var all = result.GetValue(applyAllOption);
    if (all == names.Length > 0)
    {
        Console.Error.WriteLine(all ? "--all can't be combined with object names." : "Name the objects to apply, or pass --all.");
        return 2;
    }

    if (Connect(result) is not var (folder, connection))
    {
        return 2;
    }

    var scriptOnly = result.GetValue(scriptOnlyOption);
    var applied = ApplyService.Apply(
        folder,
        connection,
        names,
        all,
        scriptOnly,
        result.GetValue(allowDataLossOption),
        result.GetValue(forceOption),
        result.GetValue(fullExtractOption));
    if (result.GetValue(jsonOption))
    {
        Console.WriteLine(JsonSerializer.Serialize(new { applied.Objects, applied.Applied, applied.Problems, applied.Messages, applied.Script }, json));
    }
    else
    {
        WriteApply(applied, scriptOnly);
    }

    return applied.Problems.Count > 0 ? 1 : 0;
});

var root = new RootCommand("sql-sc: database-first source control for SQL Server.") { load, status, changes, link, doctor, export, commit, apply };
try
{
    return root.Parse(args).Invoke(new InvocationConfiguration { EnableDefaultExceptionHandler = false });
}
catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or ArgumentException or Microsoft.Data.SqlClient.SqlException)
{
    Console.Error.WriteLine(ex.Message);
    return 1;
}

ExportSelection? Selection(ParseResult result)
{
    var names = result.GetValue(objectsArgument) ?? [];
    var all = result.GetValue(allOption);
    var mine = result.GetValue(mineOption);
    if (all && (names.Length > 0 || mine))
    {
        Console.Error.WriteLine("--all can't be combined with object names or --mine.");
        return null;
    }

    if (!all && !mine && names.Length == 0)
    {
        Console.Error.WriteLine("Name the objects to export, or pass --all or --mine.");
        return null;
    }

    return new ExportSelection(names, all, mine);
}

(WorkingFolder Folder, string Connection)? Connect(ParseResult result)
{
    var folderPath = result.GetRequiredValue(folderArgument).FullName;
    return ResolveConnection(result.GetValue(connectionOption), folderPath) is { } connection ? (WorkingFolder.Open(folderPath), connection) : null;
}

static void WriteExport(ExportResult exported, bool dryRun = false)
{
    if (exported.Selected.Count == 0 && exported.Problems.Count == 0)
    {
        Console.WriteLine("No differences.");
    }

    foreach (var file in exported.Files)
    {
        var action = (dryRun, file.Action) switch
        {
            (true, FileAction.Written) => "Would write",
            (true, FileAction.Deleted) => "Would delete",
            _ => file.Action.ToString(),
        };
        Console.WriteLine($"  {action,-12} {file.Path}");
    }

    foreach (var problem in exported.Problems)
    {
        Console.WriteLine($"  Problem: {problem}");
    }

    foreach (var file in exported.Files.Where(f => f.Diff is not null))
    {
        Console.WriteLine();
        Console.Write(file.Diff!.Length > 0 ? file.Diff : $"{file.Path}: only line endings change.\n");
    }

    if (dryRun)
    {
        Console.WriteLine();
        Console.WriteLine("Dry run: nothing was written or deleted.");
    }
}

static void WriteApply(ApplyResult applied, bool scriptOnly)
{
    Console.WriteLine($"{applied.Status.Server}/{applied.Status.Database}");
    if (applied.Objects.Count == 0 && applied.Problems.Count == 0)
    {
        Console.WriteLine("No differences.");
    }

    foreach (var obj in applied.Objects)
    {
        var who = obj.LastChange is { } e ? Invariant($"  last changed by {e.LoginName} {e.StartTime:yyyy-MM-dd HH:mm}") : "";
        Console.WriteLine($"  {obj.Action,-7} {obj.ObjectType,-24} {obj.Name}{who}");
    }

    foreach (var problem in applied.Problems)
    {
        Console.WriteLine($"  Problem: {problem}");
    }

    if (scriptOnly && applied.Script is not null)
    {
        Console.WriteLine();
        Console.WriteLine(applied.Script.TrimEnd());
        Console.WriteLine();
        Console.WriteLine("Script only: the database wasn't changed.");
        return;
    }

    foreach (var message in applied.Messages)
    {
        Console.WriteLine($"    {message}");
    }

    if (applied.Applied)
    {
        Console.WriteLine(Invariant($"Applied {applied.Objects.Count} object(s)."));
    }
    else if (applied.Objects.Count > 0)
    {
        Console.WriteLine("Nothing was applied.");
    }
}

static string? ResolveConnection(string? value, string folder, bool required = true)
{
    var connection = value ?? Environment.GetEnvironmentVariable("SQLSC_CONNECTION") ?? LinkStore.Default.FindFor(folder)?.Connection;
    if (string.IsNullOrWhiteSpace(connection))
    {
        if (required)
        {
            Console.Error.WriteLine("No connection string. Run `sql-sc link <folder> --connection ...`, pass --connection or set SQLSC_CONNECTION.");
        }

        return null;
    }

    return ConnectionStrings.WithPassword(connection, Environment.GetEnvironmentVariable(ConnectionStrings.PasswordVariable));
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
