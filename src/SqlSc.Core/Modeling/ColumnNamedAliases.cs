using System.Text;
using Microsoft.SqlServer.Dac.Model;
using Microsoft.SqlServer.TransactSql.ScriptDom;

namespace SqlSc.Core.Modeling;

/// <summary>
/// DacFx can't resolve <c>a.x</c> when <c>a</c> aliases a VALUES or OPENJSON ... WITH table that has a column also named
/// <c>a</c> (for example <c>SELECT n.n FROM (VALUES (1)) n(n)</c>), and then refuses to save the model, although SQL Server
/// accepts it. Renaming every use of such an alias to <c>[a~]</c> gives DacFx a script it can resolve without changing
/// what the script does. Folder and database scripts are renamed the same way, so comparisons are unaffected.
/// </summary>
public static class ColumnNamedAliases
{
    private const string Suffix = "~";

    public static string Rewrite(string script, bool quotedIdentifier = true)
    {
        if (!script.Contains("VALUES", StringComparison.OrdinalIgnoreCase) && !script.Contains("OPENJSON", StringComparison.OrdinalIgnoreCase))
        {
            return script;
        }

        var fragment = new TSql170Parser(quotedIdentifier).Parse(new StringReader(script), out var errors);
        if (errors.Count > 0)
        {
            return script;
        }

        var renames = new List<(Identifier Identifier, string Alias)>();
        foreach (var batch in fragment is TSqlScript { Batches: var batches } ? batches : [])
        {
            var finder = new Finder();
            batch.Accept(finder);
            if (finder.Aliases.Count == 0)
            {
                continue;
            }

            var renamer = new Renamer(finder.Aliases, finder.CommonTableExpressions);
            batch.Accept(renamer);
            if (!renamer.Unsafe)
            {
                renames.AddRange(renamer.Identifiers.Select(i => (i, finder.Aliases[i.Value])));
            }
        }

        if (renames.Count == 0)
        {
            return script;
        }

        var sb = new StringBuilder(script);
        foreach (var (identifier, alias) in renames.DistinctBy(r => r.Identifier.StartOffset).OrderByDescending(r => r.Identifier.StartOffset))
        {
            sb.Remove(identifier.StartOffset, identifier.FragmentLength)
                .Insert(identifier.StartOffset, "[" + (alias + Suffix).Replace("]", "]]", StringComparison.Ordinal) + "]");
        }

        return sb.ToString();
    }

    /// <summary>Rewrites modules in a model loaded from a .dacpac, where each module has a source of its own.</summary>
    public static void Apply(TSqlModel model) => ModuleScripts.Rewrite(model, (_, script, quotedIdentifier) => Rewrite(script, quotedIdentifier));

    private sealed class Finder : TSqlFragmentVisitor
    {
        public Dictionary<string, string> Aliases { get; } = new(StringComparer.OrdinalIgnoreCase);

        public HashSet<string> CommonTableExpressions { get; } = new(StringComparer.OrdinalIgnoreCase);

        public override void Visit(InlineDerivedTable node) => Add(node.Alias, node.Columns.Select(c => c.Value));

        public override void Visit(OpenJsonTableReference node) =>
            Add(node.Alias, node.SchemaDeclarationItems.Select(i => i.ColumnDefinition.ColumnIdentifier.Value));

        public override void Visit(CommonTableExpression node) => CommonTableExpressions.Add(node.ExpressionName.Value);

        private void Add(Identifier? alias, IEnumerable<string> columns)
        {
            if (alias is not null && columns.Contains(alias.Value, StringComparer.OrdinalIgnoreCase))
            {
                Aliases.TryAdd(alias.Value, alias.Value);
            }
        }
    }

    private sealed class Renamer(Dictionary<string, string> aliases, HashSet<string> commonTableExpressions) : TSqlFragmentVisitor
    {
        public List<Identifier> Identifiers { get; } = [];

        public bool Unsafe { get; private set; }

        public override void Visit(TableReferenceWithAlias node)
        {
            if (node.Alias is { } alias && aliases.ContainsKey(alias.Value))
            {
                Identifiers.Add(alias);
            }
            else if (node is NamedTableReference { Alias: null, SchemaObject: { BaseIdentifier: { } name } table } && aliases.ContainsKey(name.Value))
            {
                if (table.Count == 1 && commonTableExpressions.Contains(name.Value))
                {
                    Identifiers.Add(name);
                }
                else
                {
                    Unsafe = true;
                }
            }
        }

        public override void Visit(CommonTableExpression node)
        {
            if (aliases.ContainsKey(node.ExpressionName.Value))
            {
                Identifiers.Add(node.ExpressionName);
            }
        }

        public override void Visit(ColumnReferenceExpression node) => Qualifier(node.MultiPartIdentifier, 2);

        public override void Visit(SelectStarExpression node) => Qualifier(node.Qualifier, 1);

        private void Qualifier(MultiPartIdentifier? identifier, int aliasedCount)
        {
            if (identifier is null || identifier.Count < aliasedCount)
            {
                return;
            }

            var qualifier = identifier.Identifiers[identifier.Count - aliasedCount];
            if (!aliases.ContainsKey(qualifier.Value))
            {
                return;
            }

            if (identifier.Count == aliasedCount)
            {
                Identifiers.Add(qualifier);
            }
            else
            {
                Unsafe = true;
            }
        }
    }
}
