using System.Xml.Linq;

namespace SqlSc.Core.Filtering;

/// <summary>
/// Which objects are under source control, read from a Redgate <c>Filter.scpf</c>.
/// The <c>None</c> rule applies to every object; per-type rules apply to their DacFx object types.
/// A rule includes objects matching its expression when <c>Include</c> is true, and excludes them when false.
/// Child objects (constraints, indexes, permissions...) follow their owning object.
/// </summary>
public sealed class ObjectFilter
{
    /// <summary>Redgate filter element name → DacFx object type names.</summary>
    private static readonly Dictionary<string, string[]> RedgateTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Assembly"] = ["Assembly"],
        ["AsymmetricKey"] = ["AsymmetricKey"],
        ["Certificate"] = ["Certificate"],
        ["Contract"] = ["Contract"],
        ["DdlTrigger"] = ["DatabaseDdlTrigger"],
        ["Default"] = ["Default"],
        ["ExtendedProperty"] = ["ExtendedProperty"],
        ["EventNotification"] = ["DatabaseEventNotification"],
        ["ExternalDataSource"] = ["ExternalDataSource"],
        ["ExternalFileFormat"] = ["ExternalFileFormat"],
        ["ExternalTable"] = ["ExternalTable"],
        ["FullTextCatalog"] = ["FullTextCatalog"],
        ["FullTextStoplist"] = ["FullTextStopList"],
        ["Function"] = ["ScalarFunction", "TableValuedFunction", "Aggregate"],
        ["MessageType"] = ["MessageType"],
        ["PartitionFunction"] = ["PartitionFunction"],
        ["PartitionScheme"] = ["PartitionScheme"],
        ["Queue"] = ["Queue"],
        ["Role"] = ["Role", "ApplicationRole"],
        ["Route"] = ["Route"],
        ["Rule"] = ["Rule"],
        ["Schema"] = ["Schema"],
        ["SearchPropertyList"] = ["SearchPropertyList"],
        ["SecurityPolicy"] = ["SecurityPolicy"],
        ["Sequence"] = ["Sequence"],
        ["Service"] = ["Service"],
        ["ServiceBinding"] = ["RemoteServiceBinding"],
        ["StoredProcedure"] = ["Procedure"],
        ["SymmetricKey"] = ["SymmetricKey"],
        ["Synonym"] = ["Synonym"],
        ["Table"] = ["Table"],
        ["User"] = ["User"],
        ["UserDefinedType"] = ["UserDefinedDataType", "TableType", "UserDefinedType"],
        ["View"] = ["View"],
        ["XmlSchemaCollection"] = ["XmlSchemaCollection"],
    };

    private readonly Rule? global;
    private readonly Dictionary<string, Rule> byType;

    private ObjectFilter(Rule? global, Dictionary<string, Rule> byType)
    {
        this.global = global;
        this.byType = byType;
    }

    public static ObjectFilter IncludeAll { get; } = new(null, new Dictionary<string, Rule>(StringComparer.Ordinal));

    public bool IsEmpty => global is null && byType.Count == 0;

    public static ObjectFilter LoadScpf(string path)
    {
        var root = XDocument.Load(path).Root ?? throw new InvalidDataException($"'{path}' has no root element.");
        var filters = root.Descendants("Filters").FirstOrDefault()
            ?? throw new InvalidDataException($"'{path}' has no <Filters> element.");
        var caseSensitive = string.Equals(root.Descendants("FilterCaseSensitive").FirstOrDefault()?.Value, "True", StringComparison.OrdinalIgnoreCase);

        Rule? global = null;
        var byType = new Dictionary<string, Rule>(StringComparer.Ordinal);
        foreach (var element in filters.Elements())
        {
            var include = !string.Equals(element.Element("Include")?.Value, "False", StringComparison.OrdinalIgnoreCase);
            Rule rule;
            try
            {
                rule = new Rule(include, FilterExpression.Parse(element.Element("Expression")?.Value, caseSensitive));
            }
            catch (FormatException ex)
            {
                throw new InvalidDataException($"'{path}' <{element.Name.LocalName}>: {ex.Message}", ex);
            }

            if (element.Name.LocalName == "None")
            {
                global = rule;
            }
            else if (RedgateTypes.TryGetValue(element.Name.LocalName, out var types))
            {
                foreach (var type in types)
                {
                    byType[type] = rule;
                }
            }
        }

        return new ObjectFilter(global, byType);
    }

    /// <param name="objectType">DacFx object type name, e.g. <c>Table</c> or <c>Procedure</c>.</param>
    /// <param name="nameParts">DacFx name parts, e.g. <c>[dbo, Customer]</c>.</param>
    public bool Includes(string objectType, IList<string> nameParts)
    {
        var schema = nameParts.Count >= 2 || objectType == "Schema" ? nameParts[0] : "";
        var name = nameParts.Count > 0 ? nameParts[^1] : "";
        return (global?.Includes(schema, name) ?? true)
            && (!byType.TryGetValue(objectType, out var rule) || rule.Includes(schema, name));
    }

    private sealed record Rule(bool Include, FilterExpression Expression)
    {
        public bool Includes(string schema, string name) => Expression.Matches(schema, name) == Include;
    }
}
