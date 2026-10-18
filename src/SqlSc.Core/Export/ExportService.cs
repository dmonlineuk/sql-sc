using System.Text;
using Microsoft.Data.SqlClient;
using SqlSc.Core.ChangeTracking;
using SqlSc.Core.Comparison;
using SqlSc.Core.Scripting;
using SqlSc.Core.StaticData;
using SqlSc.Core.WorkingFolders;

namespace SqlSc.Core.Export;

/// <summary>Which changed objects to export: those named, every changed object, or those last changed by the current login.</summary>
/// <param name="Data">Include the rows of selected static data tables; without it only their schema is selected.</param>
/// <param name="Force">Export objects even if that undoes a change made in the folder since it last matched the database, or another login has locked them.</param>
/// <summary>
/// The objects to export or apply. <see cref="DataNames"/>, when given, are the tables whose static data is included instead of
/// <see cref="Names"/> or <see cref="All"/>, so a table's schema and rows can be picked separately.
/// </summary>
public sealed record ExportSelection(
    IReadOnlyList<string> Names,
    bool All = false,
    bool Mine = false,
    bool Data = false,
    bool Force = false,
    IReadOnlyList<string>? DataNames = null)
{
    internal IReadOnlySet<string> NamedObjects => Names.Select(ExportService.FormatName).ToHashSet(StringComparer.OrdinalIgnoreCase);
}

public enum FileAction
{
    Written,
    Deleted,
    Unchanged,
}

/// <summary>
/// A script file export wrote or deleted. <see cref="Path"/> is relative to the working folder. <see cref="Diff"/> is the unified diff
/// of the change on a dry run (empty if only line endings change), otherwise null.
/// </summary>
public sealed record ExportedFile(string Path, FileAction Action, IReadOnlyList<string> Objects, string? Diff = null);

/// <summary><see cref="Skipped"/> says why objects picked by <c>--all</c> or <c>--mine</c> were left out.</summary>
public sealed record ExportResult(
    StatusReport Status,
    IReadOnlyList<ObjectChange> Selected,
    IReadOnlyList<DataChange> SelectedData,
    IReadOnlyList<ExportedFile> Files,
    IReadOnlyList<string> Problems,
    IReadOnlyList<string> Skipped);

/// <summary>Writes changed objects from the database into the working folder, one file per object as Redgate lays them out.</summary>
public static class ExportService
{
    private static readonly UTF8Encoding Utf8 = new(encoderShouldEmitUTF8Identifier: false);

    /// <param name="writeIfProblems">Whether to write the files that can be exported when others can't.</param>
    /// <param name="dryRun">Whether to only work out each file's <see cref="ExportedFile.Diff"/>, writing and deleting nothing.</param>
    /// <param name="baseline">Where the baseline is kept; null to keep none.</param>
    /// <param name="includeChangedBy">Whether to look up who last changed each object even when <see cref="ExportSelection.Mine"/> is off.</param>
    public static ExportResult Export(
        WorkingFolder folder,
        string connectionString,
        ExportSelection selection,
        bool fullExtract = false,
        bool writeIfProblems = true,
        bool dryRun = false,
        BaselineStore? baseline = null,
        bool includeChangedBy = false)
    {
        using var session = StatusService.Open(folder, connectionString, includeChangedBy: selection.Mine || includeChangedBy, fullExtract, baselineStore: baseline);
        var report = session.Report;
        var problems = new List<string>();
        var skipped = new List<string>();
        var selected = ObjectLocks.Filter(
            SideGuard.Filter(Select(report, selection, connectionString, problems), ChangeSide.Folder, selection.NamedObjects, selection.Force, problems, skipped),
            "export",
            selection.NamedObjects,
            selection.Force,
            problems,
            skipped);
        var selectedData = SelectData(report, selection, problems);

        var database = ObjectFiles.Group(session.Database.Model);
        var folderOwners = ObjectFiles.Group(session.Folder.Model, DatabaseReferences.IsBorrowed);
        var changed = report.Changes.Select(c => (c.ObjectType, c.Name)).ToHashSet(ObjectFiles.KeyComparer.Instance);

        var byFile = new Dictionary<string, List<(string ObjectType, string Name)>>(StringComparer.OrdinalIgnoreCase);
        foreach (var change in selected)
        {
            var key = (change.ObjectType, change.Name);
            var files = folderOwners.GetValueOrDefault(key)?.Files.Keys.ToList() ?? [];
            string? path;
            if (files.Count > 1)
            {
                problems.Add($"{change.Name} is spread over {files.Count} files ({string.Join(", ", files)}); move it into one file first.");
                continue;
            }
            else if (files.Count == 1)
            {
                path = files[0];
            }
            else if (database.GetValueOrDefault(key) is { } owner)
            {
                path = RedgateLayout.PathFor(change.ObjectType, [.. owner.Owner.Name.Parts], folder.RedgateInfo?.Prefixes);
                if (path is null)
                {
                    problems.Add($"{change.Name}: sql-sc doesn't know which folder {change.ObjectType} objects go in.");
                    continue;
                }

                if (File.Exists(Path.Combine(folder.RootPath, path)))
                {
                    problems.Add($"{change.Name}: {path} already exists but doesn't create it.");
                    continue;
                }
            }
            else
            {
                problems.Add($"{change.Name} is in neither the database nor a folder file.");
                continue;
            }

            (byFile.TryGetValue(path, out var list) ? list : byFile[path] = []).Add(key);
        }

        var planned = new List<(ExportedFile File, string? Text, IReadOnlyList<(string ObjectType, string Name)> Keys)>();
        foreach (var (path, keys) in byFile.OrderBy(f => f.Key, StringComparer.OrdinalIgnoreCase))
        {
            var inFile = folderOwners.Values
                .Where(o => o.Files.ContainsKey(path))
                .OrderBy(o => o.Files[path])
                .Select(o => o.Key)
                .Concat(keys)
                .Distinct(ObjectFiles.KeyComparer.Instance)
                .ToList();
            var selectedKeys = keys.ToHashSet(ObjectFiles.KeyComparer.Instance);
            var blockers = inFile
                .Where(k => !selectedKeys.Contains(k))
                .Select(k => changed.Contains(k)
                    ? $"{path} also contains {k.Name}, which has changes too; export both together."
                    : !database.ContainsKey(k) ? $"{path} also contains {k.Name}, which isn't in the database or isn't tracked." : null)
                .OfType<string>()
                .ToList();
            var statements = inFile
                .Where(database.ContainsKey)
                .SelectMany(k => ScriptFileWriter.Statements(database[k], session.Database.OriginalScripts))
                .ToList();
            blockers.AddRange(statements
                .Where(s => s.Script.Contains(CatalogScripter.GeneratedPassword, StringComparison.Ordinal))
                .Select(s => $"{path}: sql-sc can't script the password of {StatusService.FormatName(s.Object.Name)}."));
            if (blockers.Count > 0)
            {
                problems.AddRange(blockers);
                continue;
            }

            var (file, text) = Plan(folder, path, statements.Count == 0 ? null : ScriptFileWriter.Write(statements), [.. keys.Select(k => k.Name)], dryRun);
            planned.Add((file, text, keys));
        }

        if (selectedData.Count > 0)
        {
            var tracked = StaticDataService.Tracked(folder);
            using var connection = new SqlConnection(connectionString);
            connection.Open();
            foreach (var change in selectedData)
            {
                var table = tracked.First(t => string.Equals(t.File, change.File, StringComparison.OrdinalIgnoreCase));
                string text;
                try
                {
                    text = StaticDataService.Script(connection, folder, table);
                }
                catch (InvalidDataException ex)
                {
                    problems.Add(ex.Message);
                    continue;
                }

                var (file, script) = Plan(folder, table.File, text, [change.Table], dryRun);
                planned.Add((file, script, []));
            }
        }

        if (!dryRun && (problems.Count == 0 || writeIfProblems))
        {
            foreach (var (file, text, keys) in planned)
            {
                var fullPath = Path.Combine(folder.RootPath, file.Path);
                switch (file.Action)
                {
                    case FileAction.Deleted:
                        File.Delete(fullPath);
                        break;
                    case FileAction.Written:
                        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
                        File.WriteAllText(fullPath, text, HasByteOrderMark(fullPath) ? new UTF8Encoding(true) : Utf8);
                        break;
                }

                if (file.Action != FileAction.Unchanged)
                {
                    session.Baseline?.Exported(keys, text);
                }
            }

            session.Baseline?.Save();
        }

        return new ExportResult(report, selected, selectedData, planned.Select(p => p.File).ToList(), problems, skipped);
    }

    /// <summary>
    /// The static data tables to export or apply: those named, or every table whose rows differ for <see cref="ExportSelection.All"/>.
    /// <c>--mine</c> picks none, as nothing records who changed rows. Tables that couldn't be compared are problems.
    /// </summary>
    internal static List<DataChange> SelectData(StatusReport report, ExportSelection selection, List<string> problems)
    {
        if (!selection.Data)
        {
            return [];
        }

        var candidates = selection.All && selection.DataNames is null
            ? report.Data.Where(d => d.HasChanges || d.Problem is not null).ToList()
            : (selection.DataNames ?? selection.Names).Select(FormatName)
                .SelectMany(name => report.Data.Where(d => string.Equals(d.Table, name, StringComparison.OrdinalIgnoreCase)))
                .Distinct()
                .ToList();
        problems.AddRange(candidates.Select(d => d.Problem).OfType<string>());
        return candidates.Where(d => d.Problem is null).ToList();
    }

    private static (ExportedFile File, string? Text) Plan(WorkingFolder folder, string path, string? text, IReadOnlyList<string> objects, bool dryRun)
    {
        var fullPath = Path.Combine(folder.RootPath, path);
        var existing = File.Exists(fullPath) ? File.ReadAllText(fullPath) : null;
        var action = existing == text ? FileAction.Unchanged : text is null ? FileAction.Deleted : FileAction.Written;
        var label = path.Replace('\\', '/');
        var diff = dryRun && action != FileAction.Unchanged
            ? UnifiedDiff.Create(existing, text, existing is null ? UnifiedDiff.NoFile : "a/" + label, text is null ? UnifiedDiff.NoFile : "b/" + label)
            : null;
        return (new ExportedFile(path, action, objects, diff), text);
    }

    internal static List<ObjectChange> Select(StatusReport report, ExportSelection selection, string connectionString, List<string> problems)
    {
        if (selection.All)
        {
            return [.. report.Changes];
        }

        var selected = new List<ObjectChange>();
        foreach (var name in selection.Names)
        {
            var formatted = FormatName(name);
            var matches = report.Changes.Where(c => string.Equals(c.Name, formatted, StringComparison.OrdinalIgnoreCase)).ToList();
            var dataDiffers = report.Data.Any(d => string.Equals(d.Table, formatted, StringComparison.OrdinalIgnoreCase) && (d.HasChanges || d.Problem is not null));
            if (matches.Count == 0 && !dataDiffers)
            {
                problems.Add($"{name}: no differences between the database and the folder.");
            }
            else if (matches.Count == 0 && !selection.Data)
            {
                problems.Add($"{name}: only its static data differs; pass --data to include it.");
            }

            selected.AddRange(matches);
        }

        if (selection.Mine)
        {
            if (!report.ChangeLog.Available)
            {
                throw new InvalidDataException($"--mine needs to know who changed each object, which isn't available: {report.ChangeLog.UnavailableReason}");
            }

            var login = CurrentLogin(connectionString);
            selected.AddRange(report.Changes.Where(c => string.Equals(c.LastChange?.LoginName, login, StringComparison.OrdinalIgnoreCase)));
        }

        return selected.Distinct().ToList();
    }

    /// <summary>An object name as status reports it: <c>Sales.Customer</c> or <c>[Sales].[Customer]</c> becomes <c>[Sales].[Customer]</c>.</summary>
    public static string FormatName(string name) =>
        NameParts(name) is { } parts ? string.Join('.', parts.Select(p => $"[{p}]")) : name;

    /// <summary>The parts of a dotted name, with brackets or quotes removed, or null if it isn't a valid name.</summary>
    internal static IReadOnlyList<string>? NameParts(string name)
    {
        var parts = new List<string>();
        var part = new StringBuilder();
        var closing = (char?)null;
        for (var i = 0; i < name.Length; i++)
        {
            var c = name[i];
            if (closing is { } end)
            {
                if (c != end)
                {
                    part.Append(c);
                }
                else if (i + 1 < name.Length && name[i + 1] == end)
                {
                    part.Append(c);
                    i++;
                }
                else
                {
                    closing = null;
                }
            }
            else if (c is '[' or '"')
            {
                closing = c == '[' ? ']' : '"';
            }
            else if (c == '.')
            {
                parts.Add(part.ToString().Trim());
                part.Clear();
            }
            else
            {
                part.Append(c);
            }
        }

        parts.Add(part.ToString().Trim());
        return closing is not null || parts.Any(p => p.Length == 0) ? null : parts;
    }

    internal static string CurrentLogin(string connectionString)
    {
        using var connection = new SqlConnection(connectionString);
        connection.Open();
        using var command = new SqlCommand("SELECT ORIGINAL_LOGIN()", connection);
        return (string)command.ExecuteScalar();
    }

    private static bool HasByteOrderMark(string path)
    {
        if (!File.Exists(path))
        {
            return false;
        }

        Span<byte> start = stackalloc byte[3];
        using var stream = File.OpenRead(path);
        return stream.Read(start) == 3 && start.SequenceEqual(Encoding.UTF8.Preamble);
    }
}
