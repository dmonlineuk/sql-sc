using System.Buffers;
using System.Globalization;
using System.Text;

namespace SqlSc.Core.WorkingFolders;

/// <summary>Where Redgate SQL Source Control keeps each kind of object in a working folder.</summary>
public static class RedgateLayout
{
    /// <summary>Redgate's default folder for each of its object kinds, as listed under Prefixes in RedGateDatabaseInfo.xml.</summary>
    private static readonly Dictionary<string, string> DefaultPrefixes = new(StringComparer.Ordinal)
    {
        ["Table"] = "Tables",
        ["StoredProcedure"] = "Stored Procedures",
        ["View"] = "Views",
        ["Default"] = "Defaults",
        ["FullTextCatalog"] = @"Storage\Full Text Catalogs",
        ["Function"] = "Functions",
        ["Role"] = @"Security\Roles",
        ["Rule"] = "Rules",
        ["User"] = @"Security\Users",
        ["UserDefinedType"] = @"Types\User-defined Data Types",
        ["DdlTrigger"] = "Database Triggers",
        ["Assembly"] = "Assemblies",
        ["Synonym"] = "Synonyms",
        ["XmlSchemaCollection"] = @"Types\XML Schema Collections",
        ["PartitionScheme"] = @"Storage\Partition Schemes",
        ["PartitionFunction"] = @"Storage\Partition Functions",
        ["Schema"] = @"Security\Schemas",
        ["Certificate"] = @"Security\Certificates",
        ["SymmetricKey"] = @"Security\Symmetric Keys",
        ["AsymmetricKey"] = @"Security\Asymmetric Keys",
        ["FullTextStoplist"] = @"Storage\Full Text Stoplists",
        ["Sequence"] = "Sequences",
        ["SearchPropertyList"] = "Search Property Lists",
        ["SecurityPolicy"] = "Security Policies",
        ["ExternalFileFormat"] = @"External Resources\External File Formats",
        ["ExternalDataSource"] = @"External Resources\External Data Sources",
        ["ExternalTable"] = @"Tables\External Tables",
        ["DatabaseScopedCredential"] = @"Security\Database Scoped Credentials",
    };

    /// <summary>The Redgate kind for each DacFx object type.</summary>
    private static readonly Dictionary<string, string> Kinds = new(StringComparer.Ordinal)
    {
        ["Table"] = "Table",
        ["ExternalTable"] = "ExternalTable",
        ["View"] = "View",
        ["Procedure"] = "StoredProcedure",
        ["ScalarFunction"] = "Function",
        ["TableValuedFunction"] = "Function",
        ["Aggregate"] = "Function",
        ["Role"] = "Role",
        ["User"] = "User",
        ["Schema"] = "Schema",
        ["DataType"] = "UserDefinedType",
        ["TableType"] = "UserDefinedType",
        ["UserDefinedType"] = "UserDefinedType",
        ["Sequence"] = "Sequence",
        ["Synonym"] = "Synonym",
        ["ExternalDataSource"] = "ExternalDataSource",
        ["ExternalFileFormat"] = "ExternalFileFormat",
        ["DatabaseDdlTrigger"] = "DdlTrigger",
        ["Assembly"] = "Assembly",
        ["XmlSchemaCollection"] = "XmlSchemaCollection",
        ["PartitionFunction"] = "PartitionFunction",
        ["PartitionScheme"] = "PartitionScheme",
        ["FullTextCatalog"] = "FullTextCatalog",
        ["FullTextStopList"] = "FullTextStoplist",
        ["Certificate"] = "Certificate",
        ["SymmetricKey"] = "SymmetricKey",
        ["AsymmetricKey"] = "AsymmetricKey",
        ["SearchPropertyList"] = "SearchPropertyList",
        ["SecurityPolicy"] = "SecurityPolicy",
        ["Default"] = "Default",
        ["Rule"] = "Rule",
        ["DatabaseCredential"] = "DatabaseScopedCredential",
    };

    private static readonly SearchValues<char> InvalidFileNameChars = SearchValues.Create("\\/:*?\"<>|%");

    /// <summary>
    /// The path, relative to the working folder, of the file for an object of DacFx type <paramref name="objectType"/> named
    /// <paramref name="nameParts"/>: e.g. <c>Tables\Sales.Customer.sql</c>. Null if Redgate has no folder for the type.
    /// </summary>
    public static string? PathFor(string objectType, IReadOnlyList<string> nameParts, IReadOnlyDictionary<string, string>? prefixes = null)
    {
        if (!Kinds.TryGetValue(objectType, out var kind) || nameParts.Count is 0 or > 2)
        {
            return null;
        }

        var folder = prefixes?.GetValueOrDefault(kind) ?? RedgateDatabaseInfo.NormalizeSeparators(DefaultPrefixes[kind]);
        return Path.Combine(folder, string.Join('.', nameParts.Select(Escape)) + ".sql");
    }

    private static string Escape(string part)
    {
        if (part.AsSpan().IndexOfAny(InvalidFileNameChars) < 0 && !part.Any(char.IsControl))
        {
            return part;
        }

        var sb = new StringBuilder();
        foreach (var c in part)
        {
            sb.Append(InvalidFileNameChars.Contains(c) || char.IsControl(c) ? string.Create(CultureInfo.InvariantCulture, $"%{(int)c:X2}") : c);
        }

        return sb.ToString();
    }
}
