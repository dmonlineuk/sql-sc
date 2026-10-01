using System.Globalization;
using System.Xml.Linq;

namespace SqlSc.Core.WorkingFolders;

/// <summary>
/// Subset of the Redgate <c>RedGateDatabaseInfo.xml</c> file found at the root of a working folder.
/// </summary>
public sealed record RedgateDatabaseInfo(
    string? DefaultCollation,
    string? DefaultSchema,
    int? DatabaseVersion,
    string? EngineEdition,
    bool IsAzure,
    string DataFolder,
    IReadOnlyList<string> DataFiles)
{
    public const string FileName = "RedGateDatabaseInfo.xml";

    public static RedgateDatabaseInfo Load(string path)
    {
        // Redgate declares encoding="utf-16" but files are often re-saved as UTF-8; trust the BOM, not the declaration.
        using var reader = new StreamReader(path, detectEncodingFromByteOrderMarks: true);
        var root = XDocument.Load(reader).Root
            ?? throw new InvalidDataException($"'{path}' has no root element.");

        string? Value(string name) => root.Element(name)?.Value.Trim() is { Length: > 0 } v ? v : null;

        var prefixes = root.Element("WriteToFileOptions")?.Element("Prefixes");
        var dataFolder = prefixes?.Element("Data")?.Value.Trim() is { Length: > 0 } d ? d : "Data";

        var dataFiles = root.Element("DataFileSet")?.Elements("DataFile")
            .Select(e => e.Value.Trim())
            .Where(v => v.Length > 0)
            .ToList() ?? [];

        return new RedgateDatabaseInfo(
            DefaultCollation: Value("DefaultCollation"),
            DefaultSchema: Value("DefaultSchema"),
            DatabaseVersion: int.TryParse(Value("DatabaseVersion"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var version) ? version : null,
            EngineEdition: Value("EngineEdition"),
            IsAzure: string.Equals(Value("IsAzure"), "True", StringComparison.OrdinalIgnoreCase),
            DataFolder: NormalizeSeparators(dataFolder),
            DataFiles: dataFiles);
    }

    private static string NormalizeSeparators(string relativePath) =>
        relativePath.Replace('\\', Path.DirectorySeparatorChar).Replace('/', Path.DirectorySeparatorChar);
}
