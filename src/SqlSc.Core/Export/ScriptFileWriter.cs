using System.Text;
using System.Text.RegularExpressions;
using Microsoft.SqlServer.Dac.Model;
using SqlSc.Core.Modeling;
using SqlSc.Core.Scripting;

namespace SqlSc.Core.Export;

/// <summary>
/// Writes the text of a script file: each object's statement followed by <c>GO</c>, with <c>SET QUOTED_IDENTIFIER</c> and
/// <c>SET ANSI_NULLS</c> before modules, CRLF line endings.
/// </summary>
internal static partial class ScriptFileWriter
{
    private const string Go = "\r\nGO\r\n";

    /// <summary>Order of statements after the owner's own, roughly as Redgate writes them.</summary>
    private static readonly string[] StatementOrder =
    [
        "PrimaryKeyConstraint", "UniqueConstraint", "CheckConstraint", "ForeignKeyConstraint", "DefaultConstraint", "Index",
        "Statistics", "FullTextIndex", "DmlTrigger", "RoleMembership", "Permission", "ExtendedProperty",
    ];

    /// <summary>
    /// The scripts of <paramref name="owner"/>'s members, the owner's first. Objects with no script of their own, such as
    /// columns and inline constraints, are part of another member's script.
    /// </summary>
    public static IReadOnlyList<(TSqlObject Object, string Script)> Statements(FileOwner owner, IReadOnlyDictionary<string, string> originalScripts)
    {
        var statements = new List<(TSqlObject Object, string Script, int Rank)>();
        foreach (var obj in owner.Members.Distinct())
        {
            if (!obj.TryGetScript(out var script) || string.IsNullOrWhiteSpace(script))
            {
                continue;
            }

            var rank = IsSame(obj, owner.Owner) ? -1 : Array.IndexOf(StatementOrder, obj.ObjectType.Name) is var i and >= 0 ? i : StatementOrder.Length;
            statements.Add((obj, Original(script.Trim(), originalScripts), rank));
        }

        return statements
            .OrderBy(s => s.Rank)
            .ThenBy(s => s.Object.ObjectType.Name, StringComparer.Ordinal)
            .ThenBy(s => s.Script, StringComparer.Ordinal)
            .DistinctBy(s => s.Script, StringComparer.Ordinal)
            .Select(s => (s.Object, s.Script))
            .ToList();
    }

    public static string Write(IEnumerable<(TSqlObject Object, string Script)> statements)
    {
        var sb = new StringBuilder();
        (bool QuotedIdentifier, bool AnsiNulls)? settings = null;
        foreach (var (obj, script) in statements)
        {
            if (ModuleScripts.OptionsOf(obj) is { } options)
            {
                var wanted = (options.QuotedIdentifier ?? true, options.AnsiNulls ?? true);
                if (settings != wanted)
                {
                    sb.Append("SET QUOTED_IDENTIFIER ").Append(wanted.Item1 ? "ON" : "OFF").Append(Go);
                    sb.Append("SET ANSI_NULLS ").Append(wanted.Item2 ? "ON" : "OFF").Append(Go);
                    settings = wanted;
                }
            }

            sb.Append(LineBreak().Replace(script, "\r\n")).Append(Go);
        }

        return sb.ToString();
    }

    /// <summary>The script as the database has it, undoing sql-sc's in-memory workarounds (see <see cref="DatabaseModel.OriginalScripts"/>).</summary>
    private static string Original(string script, IReadOnlyDictionary<string, string> originalScripts)
    {
        if (originalScripts.TryGetValue(script, out var original))
        {
            return original;
        }

        foreach (var (rewritten, unit) in originalScripts)
        {
            var separator = rewritten.IndexOf(CatalogScripter.BatchSeparator, StringComparison.Ordinal);
            if (separator > 0 && rewritten.AsSpan(0, separator).Trim().SequenceEqual(script))
            {
                var end = unit.IndexOf(CatalogScripter.BatchSeparator, StringComparison.Ordinal);
                return (end > 0 ? unit[..end] : unit).Trim();
            }
        }

        return script;
    }

    private static bool IsSame(TSqlObject a, TSqlObject b) =>
        a.ObjectType.Name == b.ObjectType.Name && string.Equals(a.Name.ToString(), b.Name.ToString(), StringComparison.Ordinal);

    [GeneratedRegex(@"\r\n|\r|\n")]
    private static partial Regex LineBreak();
}
