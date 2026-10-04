using System.Text;
using Microsoft.SqlServer.TransactSql.ScriptDom;

namespace SqlSc.Core.Modeling;

/// <summary>
/// DacFx counts a table type column with <c>COLLATE</c> naming the database's default collation as different from the same
/// column without one, although SQL Server stores both the same way and catalog scripting omits default collations. Removing
/// such clauses from table type columns makes folder table types match the database's.
/// </summary>
public static class TableTypeCollations
{
    public static string Rewrite(string script, string? defaultCollation, bool quotedIdentifier = true)
    {
        if (defaultCollation is null
            || !script.Contains("COLLATE", StringComparison.OrdinalIgnoreCase)
            || !script.Contains("TYPE", StringComparison.OrdinalIgnoreCase))
        {
            return script;
        }

        var fragment = new TSql170Parser(quotedIdentifier).Parse(new StringReader(script), out var errors);
        if (errors.Count > 0)
        {
            return script;
        }

        var finder = new Finder();
        fragment.Accept(finder);
        var removals = finder.Collations
            .Where(c => string.Equals(c.Value, defaultCollation, StringComparison.OrdinalIgnoreCase))
            .Select(Range)
            .OrderByDescending(r => r.Start)
            .ToList();
        if (removals.Count == 0)
        {
            return script;
        }

        var sb = new StringBuilder(script);
        foreach (var (start, end) in removals)
        {
            sb.Remove(start, end - start);
        }

        return sb.ToString();
    }

    /// <summary>From the whitespace before <c>COLLATE</c> to the end of the collation's name.</summary>
    private static (int Start, int End) Range(Identifier collation)
    {
        var tokens = collation.ScriptTokenStream;
        var index = collation.FirstTokenIndex - 1;
        while (index >= 0 && tokens[index].TokenType == TSqlTokenType.WhiteSpace)
        {
            index--;
        }

        while (index > 0 && tokens[index - 1].TokenType == TSqlTokenType.WhiteSpace)
        {
            index--;
        }

        return (tokens[index].Offset, collation.StartOffset + collation.FragmentLength);
    }

    private sealed class Finder : TSqlFragmentVisitor
    {
        public List<Identifier> Collations { get; } = [];

        public override void Visit(CreateTypeTableStatement node)
        {
            Collations.AddRange(node.Definition.ColumnDefinitions.Select(c => c.Collation).OfType<Identifier>());
        }
    }
}
