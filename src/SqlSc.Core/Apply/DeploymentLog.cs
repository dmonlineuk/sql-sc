using System.Globalization;
using System.Text;
using SqlSc.Core.Comparison;
using SqlSc.Core.Export;
using SqlSc.Core.Settings;
using SqlSc.Core.StaticData;

namespace SqlSc.Core.Apply;

/// <summary>
/// Keeps a copy of each deployment script that apply runs, for audit, in <c>deployments</c> next to <c>links.json</c>.
/// Each file starts with who ran it, where and what it changed, and ends with the outcome. Connection strings are never written.
/// </summary>
public sealed class DeploymentLog(string directory)
{
    public static DeploymentLog Default => new(Path.Combine(LinkStore.SettingsDirectory, "deployments"));

    public string Directory { get; } = directory;

    /// <summary>Creates the file for a deployment and writes its header; returns its path.</summary>
    internal string Start(
        StatusReport status,
        string folderRoot,
        string login,
        IReadOnlyList<AppliedObject> objects,
        IReadOnlyList<AppliedData> data,
        DateTime utcNow)
    {
        System.IO.Directory.CreateDirectory(Directory);
        var stamp = utcNow.ToString("yyyyMMdd-HHmmss-fff", CultureInfo.InvariantCulture);
        var path = Path.Combine(Directory, $"{stamp}-{Safe(status.Server)}-{Safe(status.Database)}.sql");
        var header = new StringBuilder()
            .Append("-- sql-sc apply\r\n")
            .Append(CultureInfo.InvariantCulture, $"-- Time (UTC): {utcNow:yyyy-MM-dd HH:mm:ss}\r\n")
            .Append(CultureInfo.InvariantCulture, $"-- Server: {status.Server}\r\n")
            .Append(CultureInfo.InvariantCulture, $"-- Database: {status.Database}\r\n")
            .Append(CultureInfo.InvariantCulture, $"-- Login: {login}\r\n")
            .Append(CultureInfo.InvariantCulture, $"-- Working folder: {Path.GetFullPath(folderRoot)}\r\n");
        if (Commit(folderRoot) is { } commit)
        {
            header.Append(CultureInfo.InvariantCulture, $"-- Git commit: {commit}\r\n");
        }

        header.Append("-- Changes:\r\n");
        foreach (var obj in objects)
        {
            header.Append(CultureInfo.InvariantCulture, $"--   {obj.Action} {obj.ObjectType} {obj.Name}\r\n");
        }

        foreach (var table in data)
        {
            header.Append(CultureInfo.InvariantCulture, $"--   Data Table {table.Table}\r\n");
        }

        File.WriteAllText(path, header.Append("\r\n").ToString());
        return path;
    }

    internal static void Append(string path, string text) =>
        File.AppendAllText(path, text.EndsWith('\n') ? text : text + "\r\n");

    /// <summary>Writes the outcome as the file's last lines.</summary>
    internal static void Finish(string path, string outcome) =>
        Append(path, "\r\n-- Result: " + outcome.ReplaceLineEndings(" ") + "\r\n");

    private static string Safe(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        return new string(name.Select(c => invalid.Contains(c) || c is ',' or ':' or '\\' or '/' ? '_' : c).ToArray());
    }

    private static string? Commit(string folderRoot)
    {
        try
        {
            var (exitCode, output, _) = Git.Run(folderRoot, "rev-parse", "HEAD");
            return exitCode == 0 && output.Length > 0 ? output : null;
        }
        catch (InvalidDataException)
        {
            return null;
        }
    }
}
