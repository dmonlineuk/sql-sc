using System.Text;
using Microsoft.Data.SqlClient;
using SqlSc.Core.Comparison;
using SqlSc.Core.Scripting;
using SqlSc.Core.WorkingFolders;

namespace SqlSc.Core.Export;

/// <summary>Which changed objects to export: those named, every changed object, or those last changed by the current login.</summary>
public sealed record ExportSelection(IReadOnlyList<string> Names, bool All = false, bool Mine = false);

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

public sealed record ExportResult(
    StatusReport Status,
    IReadOnlyList<ObjectChange> Selected,
    IReadOnlyList<ExportedFile> Files,
    IReadOnlyList<string> Problems);

/// <summary>Writes changed objects from the database into the working folder, one file per object as Redgate lays them out.</summary>
public static class ExportService
{
    private static readonly UTF8Encoding Utf8 = new(encoderShouldEmitUTF8Identifier: false);

    /// <param name="writeIfProblems">Whether to write the files that can be exported when others can't.</param>
    /// <param name="dryRun">Whether to only work out each file's <see cref="ExportedFile.Diff"/>, writing and deleting nothing.</param>
    public static ExportResult Export(
        WorkingFolder folder,
        string connectionString,
        ExportSelection selection,
        bool fullExtract = false,
        bool writeIfProblems = true,
        bool dryRun = false)
    {
        using var session = StatusService.Open(folder, connectionString, includeChangedBy: selection.Mine, fullExtract);
        var report = session.Report;
        var problems = new List<string>();
        var selected = Select(report, selection, connectionString, problems);

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

        var planned = new List<(ExportedFile File, string? Text)>();
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

            var fullPath = Path.Combine(folder.RootPath, path);
            var objects = keys.Select(k => k.Name).ToList();
            var existing = File.Exists(fullPath) ? File.ReadAllText(fullPath) : null;
            var text = statements.Count == 0 ? null : ScriptFileWriter.Write(statements);
            var action = existing == text ? FileAction.Unchanged : text is null ? FileAction.Deleted : FileAction.Written;
            var label = path.Replace('\\', '/');
            var diff = dryRun && action != FileAction.Unchanged
                ? UnifiedDiff.Create(existing, text, existing is null ? UnifiedDiff.NoFile : "a/" + label, text is null ? UnifiedDiff.NoFile : "b/" + label)
                : null;
            planned.Add((new ExportedFile(path, action, objects, diff), text));
        }

        if (!dryRun && (problems.Count == 0 || writeIfProblems))
        {
            foreach (var (file, text) in planned)
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
            }
        }

        return new ExportResult(report, selected, planned.Select(p => p.File).ToList(), problems);
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
            if (matches.Count == 0)
            {
                problems.Add($"{name}: no differences between the database and the folder.");
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
    public static string FormatName(string name)
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
        return closing is not null || parts.Any(p => p.Length == 0) ? name : string.Join('.', parts.Select(p => $"[{p}]"));
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
