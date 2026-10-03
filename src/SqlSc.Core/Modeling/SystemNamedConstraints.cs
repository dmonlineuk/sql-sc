using System.Text;
using System.Text.RegularExpressions;
using Microsoft.SqlServer.TransactSql.ScriptDom;

namespace SqlSc.Core.Modeling;

/// <summary>
/// SQL Server names unnamed constraints itself (e.g. <c>PK__Customer__3213E83F9AEF8E7A</c>). Redgate scripts those names
/// into the working folder, but DacFx models them as unnamed, as SQL Server would recreate them with different names. Removing
/// <c>CONSTRAINT [name]</c> for system-generated names makes folder constraints match the database's. Names used anywhere
/// else in <c>file</c> (e.g. <c>NOCHECK CONSTRAINT</c> or an extended property) are kept.
/// </summary>
public static partial class SystemNamedConstraints
{
    public static string Rewrite(string script, bool quotedIdentifier = true, string? file = null)
    {
        if (!script.Contains("__", StringComparison.Ordinal))
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
        var removals = finder.Constraints
            .Where(c => SystemName().IsMatch(c.ConstraintIdentifier.Value)
                && Occurrences(file ?? script, c.ConstraintIdentifier.Value) == 1
                && c.ScriptTokenStream[c.FirstTokenIndex].TokenType == TSqlTokenType.Constraint)
            .Select(c => (Start: c.StartOffset, End: End(c)))
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

    /// <summary>The offset of the first token after the constraint's name, skipping the whitespace that follows it.</summary>
    private static int End(ConstraintDefinition constraint)
    {
        var tokens = constraint.ScriptTokenStream;
        var index = constraint.ConstraintIdentifier.LastTokenIndex + 1;
        while (index < tokens.Count && tokens[index].TokenType == TSqlTokenType.WhiteSpace)
        {
            index++;
        }

        return index < tokens.Count ? tokens[index].Offset : constraint.ConstraintIdentifier.StartOffset + constraint.ConstraintIdentifier.FragmentLength;
    }

    private static int Occurrences(string script, string name)
    {
        var count = 0;
        for (var i = script.IndexOf(name, StringComparison.OrdinalIgnoreCase); i >= 0; i = script.IndexOf(name, i + name.Length, StringComparison.OrdinalIgnoreCase))
        {
            count++;
        }

        return count;
    }

    [GeneratedRegex("^(PK|UQ|DF|CK|FK)__.+__[0-9A-F]{8}([0-9A-F]{8})?$", RegexOptions.CultureInvariant)]
    private static partial Regex SystemName();

    private sealed class Finder : TSqlFragmentVisitor
    {
        public List<ConstraintDefinition> Constraints { get; } = [];

        public override void Visit(ConstraintDefinition node)
        {
            if (node.ConstraintIdentifier is not null)
            {
                Constraints.Add(node);
            }
        }
    }
}
