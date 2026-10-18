using System.Text;
using System.Xml;
using System.Xml.Linq;
using SqlSc.Core.Settings;

namespace SqlSc.Core.Filtering;

public enum FilterMode
{
    /// <summary>Every object of the type is tracked.</summary>
    Everything,

    /// <summary>No object of the type is tracked.</summary>
    Nothing,

    /// <summary>Only objects matching the conditions are tracked.</summary>
    Only,

    /// <summary>Every object except those matching the conditions is tracked.</summary>
    Except,
}

public enum ConditionField
{
    Schema,
    Name,
}

public enum ConditionOperator
{
    Equal,
    NotEqual,
    Like,
    NotLike,
}

/// <summary>One condition of a filter rule, e.g. <c>@SCHEMA LIKE 'staging%'</c>. LIKE uses T-SQL patterns: %, _ and [...].</summary>
public sealed record FilterCondition(ConditionField Field, ConditionOperator Operator, string Value)
{
    public string ToExpression()
    {
        var field = Field == ConditionField.Schema ? "@SCHEMA" : "@NAME";
        var op = Operator switch
        {
            ConditionOperator.Equal => "=",
            ConditionOperator.NotEqual => "!=",
            ConditionOperator.Like => "LIKE",
            _ => "NOT LIKE",
        };
        return $"{field} {op} '{Value.Replace("'", "''", StringComparison.Ordinal)}'";
    }
}

/// <summary>
/// A filter rule split into what the filter editors show: its mode, and its conditions when the expression is only simple
/// conditions joined by AND or by OR (otherwise <see cref="Conditions"/> is null and only <see cref="Expression"/> can be edited).
/// </summary>
public sealed record FilterRuleParts(string ObjectType, FilterMode Mode, IReadOnlyList<FilterCondition>? Conditions, bool MatchAll, string Expression)
{
    public static FilterRuleParts Of(FilterRule rule)
    {
        var everything = string.IsNullOrWhiteSpace(rule.Expression) || rule.Expression.Trim().Equals("TRUE", StringComparison.OrdinalIgnoreCase);
        var mode = (rule.Include, everything) switch
        {
            (true, true) => FilterMode.Everything,
            (false, true) => FilterMode.Nothing,
            (true, false) => FilterMode.Only,
            _ => FilterMode.Except,
        };
        return new FilterRuleParts(rule.ObjectType, mode, FilterExpression.Split(rule.Expression, out var matchAll), matchAll, everything ? "" : rule.Expression);
    }

    /// <summary>The rule for a mode, with an expression built from <paramref name="conditions"/> (joined by AND when <paramref name="matchAll"/>, else OR).</summary>
    public static FilterRule Build(string objectType, FilterMode mode, IReadOnlyList<FilterCondition> conditions, bool matchAll) =>
        Build(objectType, mode, string.Join(matchAll ? " AND " : " OR ", conditions.Select(c => conditions.Count == 1 ? c.ToExpression() : $"({c.ToExpression()})")));

    /// <summary>The rule for a mode and a raw expression. Only and Except need one.</summary>
    public static FilterRule Build(string objectType, FilterMode mode, string expression)
    {
        var include = mode is FilterMode.Everything or FilterMode.Only;
        if (mode is FilterMode.Everything or FilterMode.Nothing)
        {
            return new FilterRule(objectType, include, "TRUE");
        }

        return string.IsNullOrWhiteSpace(expression)
            ? throw new InvalidDataException($"{Describe(objectType)}: \"{(mode == FilterMode.Only ? "only" : "everything except")} objects matching\" needs at least one condition.")
            : new FilterRule(objectType, include, expression.Trim());
    }

    public static string Describe(string objectType) => objectType == ObjectFilter.AllObjects ? "All objects" : objectType;
}

/// <summary>Reads and writes a working folder's Redgate filter file (<c>Filter.scpf</c>).</summary>
public static class FilterFile
{
    private const string NewFile = """
        <?xml version="1.0" encoding="utf-8" standalone="yes"?>
        <NamedFilter version="1" type="SQLCompareFilter">
          <Filter version="1" type="DifferenceFilter">
            <FilterCaseSensitive>False</FilterCaseSensitive>
            <Filters version="1" />
          </Filter>
        </NamedFilter>
        """;

    /// <summary>The filter file's path relative to the working folder: the one in sql-sc.json, else <c>Filter.scpf</c>.</summary>
    public static string RelativePath(string folder) => ProjectSettings.Load(folder).Filter ?? ProjectSettings.DefaultFilter;

    /// <summary>The file's rules, or none if it doesn't exist yet.</summary>
    public static ObjectFilter Load(string folder)
    {
        var path = Path.Combine(folder, RelativePath(folder));
        return File.Exists(path) ? ObjectFilter.LoadScpf(path) : ObjectFilter.IncludeAll;
    }

    /// <summary>
    /// Writes the rules, creating the file if needed. Everything else in an existing file (its declaration, byte order mark,
    /// line endings and other settings) is kept. Rules must be for known object types, one each, with valid expressions.
    /// </summary>
    /// <returns>The file's path relative to the working folder.</returns>
    public static string Save(string folder, IReadOnlyList<FilterRule> rules)
    {
        var relative = RelativePath(folder);
        var path = Path.Combine(folder, relative);
        var existing = File.Exists(path) ? File.ReadAllText(path) : null;
        var bom = existing is not null && HasByteOrderMark(path);
        var newline = existing is null || existing.Contains("\r\n", StringComparison.Ordinal) || !existing.Contains('\n', StringComparison.Ordinal) ? "\r\n" : "\n";
        var document = XDocument.Parse(existing ?? NewFile, LoadOptions.PreserveWhitespace);
        var root = document.Root ?? throw new InvalidDataException($"{relative} has no root element.");
        var filters = root.Descendants("Filters").FirstOrDefault()
            ?? throw new InvalidDataException($"{relative} has no <Filters> element.");
        var caseSensitive = string.Equals(root.Descendants("FilterCaseSensitive").FirstOrDefault()?.Value, "True", StringComparison.OrdinalIgnoreCase);

        Validate(rules, caseSensitive, filters.Elements().Select(e => e.Name.LocalName).ToHashSet(StringComparer.Ordinal));
        filters.RemoveNodes();
        foreach (var rule in rules)
        {
            filters.Add(new XElement(
                rule.ObjectType,
                new XAttribute("version", "1"),
                new XElement("Include", rule.Include ? "True" : "False"),
                new XElement("Expression", rule.Expression)));
        }

        foreach (var text in document.DescendantNodes().OfType<XText>().Where(t => string.IsNullOrWhiteSpace(t.Value)).ToList())
        {
            text.Remove();
        }

        var xml = new StringBuilder();
        using (var writer = XmlWriter.Create(xml, new XmlWriterSettings { OmitXmlDeclaration = true, Indent = true, IndentChars = "  ", NewLineChars = newline }))
        {
            root.WriteTo(writer);
        }

        var declaration = document.Declaration?.ToString() ?? """<?xml version="1.0" encoding="utf-8" standalone="yes"?>""";
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, declaration + newline + xml + newline, new UTF8Encoding(bom));
        return relative;
    }

    /// <summary>The rules with <paramref name="rule"/> replacing the one for its object type, or added (the All objects rule first).</summary>
    public static IReadOnlyList<FilterRule> With(IReadOnlyList<FilterRule> rules, FilterRule rule)
    {
        var list = rules.ToList();
        var index = list.FindIndex(r => r.ObjectType.Equals(rule.ObjectType, StringComparison.OrdinalIgnoreCase));
        if (index >= 0)
        {
            list[index] = rule;
        }
        else if (rule.ObjectType == ObjectFilter.AllObjects)
        {
            list.Insert(0, rule);
        }
        else
        {
            list.Add(rule);
        }

        return list;
    }

    public static IReadOnlyList<FilterRule> Without(IReadOnlyList<FilterRule> rules, string objectType) =>
        rules.Where(r => !r.ObjectType.Equals(objectType, StringComparison.OrdinalIgnoreCase)).ToList();

    /// <param name="kept">Object types already in the file, which are kept even if sql-sc doesn't know them.</param>
    private static void Validate(IReadOnlyList<FilterRule> rules, bool caseSensitive, HashSet<string> kept)
    {
        foreach (var rule in rules)
        {
            if (ObjectFilter.RedgateType(rule.ObjectType) != rule.ObjectType && !kept.Contains(rule.ObjectType))
            {
                throw new InvalidDataException($"{rule.ObjectType} isn't an object type a filter rule can apply to. Use one of: {ObjectFilter.AllObjects}, {string.Join(", ", ObjectFilter.RedgateTypeNames)}.");
            }
        }

        if (rules.GroupBy(r => r.ObjectType, StringComparer.OrdinalIgnoreCase).FirstOrDefault(g => g.Count() > 1) is { } duplicate)
        {
            throw new InvalidDataException($"{FilterRuleParts.Describe(duplicate.Key)} has more than one rule.");
        }

        _ = ObjectFilter.FromRules(rules, caseSensitive);
    }

    private static bool HasByteOrderMark(string path)
    {
        Span<byte> start = stackalloc byte[3];
        using var stream = File.OpenRead(path);
        return stream.Read(start) == 3 && start.SequenceEqual(Encoding.UTF8.Preamble);
    }
}
