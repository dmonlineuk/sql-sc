using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.SqlServer.TransactSql.ScriptDom;

namespace SqlSc.Core.Comparison;

public static class ScriptDifference
{
    public const string Same = "scripts are the same";

    private const int LineShown = 150;

    /// <summary>
    /// The first line where two scripts differ, ignoring whitespace, keyword casing, blank lines, trailing commas (so a column added
    /// at the end of a table is reported, not the comma before it) and <c>COLLATE</c> clauses naming one of
    /// <paramref name="defaultCollations"/>, which DacFx doesn't count as a difference.
    /// </summary>
    public static string First(string a, string b, string aLabel, string bLabel, IReadOnlyCollection<string>? defaultCollations = null)
    {
        var collate = defaultCollations is { Count: > 0 }
            ? new Regex(@"\s+COLLATE\s+\[?(" + string.Join('|', defaultCollations.Select(Regex.Escape)) + @")\]?(?![\w\]])", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)
            : null;
        string[] Lines(string s) => Normalize(s).Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
            .Select(l => collate?.Replace(l, string.Empty) ?? l)
            .ToArray();
        static string Cut(string s) => s.Length > LineShown ? s[..LineShown] + "..." : s;
        var (x, y) = (Lines(a), Lines(b));
        var i = 0;
        while (i < x.Length && i < y.Length && x[i].TrimEnd(',') == y[i].TrimEnd(','))
        {
            i++;
        }

        return i == x.Length && i == y.Length
            ? Same
            : string.Create(CultureInfo.InvariantCulture, $"line {i + 1} is `{(i < x.Length ? Cut(x[i]) : "(end)")}` {aLabel}, `{(i < y.Length ? Cut(y[i]) : "(end)")}` {bLabel}");
    }

    /// <summary>Collapses whitespace within each line and upper-cases keywords, as DacFx's comparison ignores both.</summary>
    private static string Normalize(string script)
    {
        var tokens = new TSql170Parser(true).GetTokenStream(new StringReader(script), out var errors);
        if (errors.Count > 0)
        {
            return script;
        }

        var sb = new StringBuilder(script.Length);
        foreach (var token in tokens.Where(t => t.Text is not null))
        {
            sb.Append(token.TokenType switch
            {
                TSqlTokenType.WhiteSpace => token.Text.Contains('\n', StringComparison.Ordinal) ? "\n" : " ",
                not (TSqlTokenType.Identifier or TSqlTokenType.QuotedIdentifier) when token.Text.All(c => char.IsLetter(c) || c == '_') => token.Text.ToUpperInvariant(),
                _ => token.Text,
            });
        }

        return sb.ToString();
    }
}
