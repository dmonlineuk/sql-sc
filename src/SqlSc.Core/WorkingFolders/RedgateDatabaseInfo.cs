using System.Globalization;
using System.Text;
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
    IReadOnlyList<string> DataFiles,
    IReadOnlyDictionary<string, string>? Prefixes = null)
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
            DataFiles: dataFiles,
            Prefixes: prefixes?.Elements()
                .Select(e => (e.Name.LocalName, Value: e.Value.Trim()))
                .Where(e => e.Value.Length > 0)
                .GroupBy(e => e.LocalName, StringComparer.Ordinal)
                .ToDictionary(g => g.Key, g => NormalizeSeparators(g.First().Value), StringComparer.Ordinal));
    }

    /// <summary>
    /// Sets the static data files listed in the <c>DataFileSet</c> of the file at <paramref name="path"/>, creating the file if needed.
    /// The rest of the file, its encoding and its line endings are kept.
    /// </summary>
    public static void SetDataFiles(string path, IEnumerable<string> dataFiles)
    {
        var files = dataFiles.Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase).ToList();
        var bytes = File.Exists(path) ? File.ReadAllBytes(path) : null;
        var encoding = bytes switch
        {
            null or [0xEF, 0xBB, 0xBF, ..] => new UTF8Encoding(true),
            [0xFF, 0xFE, ..] => Encoding.Unicode,
            [0xFE, 0xFF, ..] => Encoding.BigEndianUnicode,
            _ => (Encoding)new UTF8Encoding(false),
        };
        var text = bytes is null
            ? "<?xml version=\"1.0\" encoding=\"utf-16\" standalone=\"yes\"?>\r\n<DatabaseInformation Version=\"2\">\r\n</DatabaseInformation>\r\n"
            : new StreamReader(new MemoryStream(bytes), detectEncodingFromByteOrderMarks: true).ReadToEnd();
        var newLine = text.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
        var document = XDocument.Parse(text, LoadOptions.PreserveWhitespace);
        var root = document.Root ?? throw new InvalidDataException($"'{path}' has no root element.");

        var set = new XElement("DataFileSet", new XText(newLine + "    "), new XElement("Count", files.Count.ToString(CultureInfo.InvariantCulture)));
        foreach (var file in files)
        {
            set.Add(new XText(newLine + "    "), new XElement("DataFile", file));
        }

        set.Add(new XText(newLine + "  "));
        if (root.Element("DataFileSet") is { } existing)
        {
            existing.ReplaceWith(set);
        }
        else if (root.Elements().LastOrDefault() is { } last)
        {
            last.AddAfterSelf(new XText(newLine + "  "), set);
        }
        else
        {
            root.Add(new XText("  "), set, new XText(newLine));
        }

        var declaration = document.Declaration is { } d ? d + newLine : "";
        var trailing = text.EndsWith('\n') ? newLine : "";
        File.WriteAllText(path, declaration + root.ToString(SaveOptions.DisableFormatting).ReplaceLineEndings(newLine) + trailing, encoding);
    }

    internal static string NormalizeSeparators(string relativePath) =>
        relativePath.Replace('\\', Path.DirectorySeparatorChar).Replace('/', Path.DirectorySeparatorChar);
}
