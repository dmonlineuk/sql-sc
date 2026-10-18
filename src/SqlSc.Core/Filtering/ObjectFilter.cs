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
    /// <summary>The Redgate element name of the rule that applies to every object type.</summary>
    public const string AllObjects = "None";

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

    private ObjectFilter(Rule? global, Dictionary<string, Rule> byType, IReadOnlyList<FilterRule> rules, bool caseSensitive)
    {
        this.global = global;
        this.byType = byType;
        Rules = rules;
        CaseSensitive = caseSensitive;
    }

    public static ObjectFilter IncludeAll { get; } = new(null, new Dictionary<string, Rule>(StringComparer.Ordinal), [], false);

    /// <summary>The Redgate object types a rule can apply to, besides <see cref="AllObjects"/>.</summary>
    public static IReadOnlyList<string> RedgateTypeNames { get; } = [.. RedgateTypes.Keys.Order(StringComparer.Ordinal)];

    /// <summary>Whether names are matched case-sensitively (<c>FilterCaseSensitive</c>).</summary>
    public bool CaseSensitive { get; }

    /// <summary>The file's rules in order, as written. <see cref="FilterRule.ObjectType"/> is <see cref="AllObjects"/> for the rule on every object.</summary>
    public IReadOnlyList<FilterRule> Rules { get; }

    public bool IsEmpty => global is null && byType.Count == 0;

    public static ObjectFilter LoadScpf(string path)
    {
        using var reader = new StreamReader(path, detectEncodingFromByteOrderMarks: true);
        var root = XDocument.Load(reader).Root ?? throw new InvalidDataException($"'{path}' has no root element.");
        var filters = root.Descendants("Filters").FirstOrDefault()
            ?? throw new InvalidDataException($"'{path}' has no <Filters> element.");
        var caseSensitive = string.Equals(root.Descendants("FilterCaseSensitive").FirstOrDefault()?.Value, "True", StringComparison.OrdinalIgnoreCase);
        var rules = filters.Elements()
            .Select(e => new FilterRule(
                e.Name.LocalName,
                !string.Equals(e.Element("Include")?.Value, "False", StringComparison.OrdinalIgnoreCase),
                e.Element("Expression")?.Value.Trim() ?? ""))
            .ToList();
        try
        {
            return FromRules(rules, caseSensitive);
        }
        catch (InvalidDataException ex)
        {
            throw new InvalidDataException($"'{path}' {ex.Message}", ex);
        }
    }

    /// <summary>A filter from rules, e.g. ones being edited. Rules for object types sql-sc doesn't know are kept but have no effect.</summary>
    public static ObjectFilter FromRules(IReadOnlyList<FilterRule> rules, bool caseSensitive = false)
    {
        Rule? global = null;
        var byType = new Dictionary<string, Rule>(StringComparer.Ordinal);
        foreach (var filterRule in rules)
        {
            Rule rule;
            try
            {
                rule = new Rule(filterRule.Include, FilterExpression.Parse(filterRule.Expression, caseSensitive));
            }
            catch (FormatException ex)
            {
                throw new InvalidDataException($"<{filterRule.ObjectType}>: {ex.Message}", ex);
            }

            if (filterRule.ObjectType == AllObjects)
            {
                global = rule;
            }
            else if (RedgateTypes.TryGetValue(filterRule.ObjectType, out var types))
            {
                foreach (var type in types)
                {
                    byType[type] = rule;
                }
            }
        }

        return new ObjectFilter(global, byType, rules, caseSensitive);
    }

    /// <summary>The Redgate object type with this name, ignoring case, or <see cref="AllObjects"/> for "all" and "None"; null if there is none.</summary>
    public static string? RedgateType(string name) =>
        name.Equals("all", StringComparison.OrdinalIgnoreCase) || name.Equals(AllObjects, StringComparison.OrdinalIgnoreCase) ? AllObjects
            : RedgateTypes.Keys.FirstOrDefault(k => k.Equals(name, StringComparison.OrdinalIgnoreCase));

    /// <summary>The Redgate object type whose rule applies to a DacFx object type, or null if no rule does.</summary>
    public static string? RedgateTypeOf(string objectType) =>
        RedgateTypes.FirstOrDefault(t => t.Value.Contains(objectType, StringComparer.Ordinal)).Key;

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

/// <summary>One rule of a Redgate filter: the Redgate object type it applies to, whether matches are included or excluded, and its expression.</summary>
public sealed record FilterRule(string ObjectType, bool Include, string Expression);
