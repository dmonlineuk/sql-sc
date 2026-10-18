using System.Text;
using System.Text.RegularExpressions;

namespace SqlSc.Core.Filtering;

/// <summary>
/// A Redgate SQL Compare filter condition, e.g. <c>(@SCHEMA = 'dbo') AND (@NAME LIKE 'tmp[_]%')</c>.
/// Supports @SCHEMA and @NAME with =, !=, &lt;&gt;, LIKE and NOT LIKE, combined with AND, OR, NOT, parentheses, TRUE and FALSE.
/// </summary>
public sealed class FilterExpression
{
    private readonly Func<string, string, bool> evaluate;

    private FilterExpression(string text, Func<string, string, bool> evaluate)
    {
        Text = text;
        this.evaluate = evaluate;
    }

    public static FilterExpression True { get; } = new("TRUE", (_, _) => true);

    public string Text { get; }

    public bool IsTrue => ReferenceEquals(this, True);

    public bool Matches(string schema, string name) => evaluate(schema, name);

    public static FilterExpression Parse(string? text, bool caseSensitive = false)
    {
        if (string.IsNullOrWhiteSpace(text) || text.Trim().Equals("TRUE", StringComparison.OrdinalIgnoreCase))
        {
            return True;
        }

        var parser = new Parser(Tokenize(text), caseSensitive);
        var result = parser.ParseOr();
        parser.ExpectEnd();
        return new FilterExpression(text, result);
    }

    /// <summary>
    /// Splits an expression made only of simple conditions joined by one kind of operator, such as
    /// <c>(@SCHEMA LIKE 'staging%') OR (@NAME = 'Customers')</c>, into its conditions; null for anything else.
    /// <paramref name="matchAll"/> is whether they are joined by AND.
    /// </summary>
    public static IReadOnlyList<FilterCondition>? Split(string? text, out bool matchAll)
    {
        matchAll = false;
        if (string.IsNullOrWhiteSpace(text) || text.Trim().Equals("TRUE", StringComparison.OrdinalIgnoreCase))
        {
            return [];
        }

        List<string> tokens;
        try
        {
            tokens = Tokenize(text);
        }
        catch (FormatException)
        {
            return null;
        }

        var conditions = new List<FilterCondition>();
        string? joiner = null;
        var i = 0;
        while (true)
        {
            var opened = 0;
            while (i < tokens.Count && tokens[i] == "(")
            {
                (opened, i) = (opened + 1, i + 1);
            }

            if (i + 2 >= tokens.Count || tokens[i] is not ("@SCHEMA" or "@NAME"))
            {
                return null;
            }

            var field = tokens[i] == "@SCHEMA" ? ConditionField.Schema : ConditionField.Name;
            var negate = tokens[i + 1] == "NOT";
            var op = (negate, tokens[i + (negate ? 2 : 1)]) switch
            {
                (false, "=") => ConditionOperator.Equal,
                (false, "!=") => ConditionOperator.NotEqual,
                (false, "LIKE") => ConditionOperator.Like,
                (true, "LIKE") => ConditionOperator.NotLike,
                _ => (ConditionOperator?)null,
            };
            i += negate ? 3 : 2;
            if (op is null || i >= tokens.Count || !tokens[i].StartsWith('\''))
            {
                return null;
            }

            conditions.Add(new FilterCondition(field, op.Value, tokens[i][1..]));
            i++;
            while (opened > 0 && i < tokens.Count && tokens[i] == ")")
            {
                (opened, i) = (opened - 1, i + 1);
            }

            if (opened > 0)
            {
                return null;
            }

            if (i == tokens.Count)
            {
                matchAll = joiner == "AND";
                return conditions;
            }

            if (tokens[i] is not ("AND" or "OR") || (joiner is not null && joiner != tokens[i]))
            {
                return null;
            }

            (joiner, i) = (tokens[i], i + 1);
        }
    }

    private static List<string> Tokenize(string text)
    {
        var tokens = new List<string>();
        var i = 0;
        while (i < text.Length)
        {
            var c = text[i];
            if (char.IsWhiteSpace(c))
            {
                i++;
            }
            else if (c is '(' or ')' or '=')
            {
                tokens.Add(c.ToString());
                i++;
            }
            else if (c is '!' or '<' && i + 1 < text.Length && text[i + 1] is '=' or '>')
            {
                tokens.Add("!=");
                i += 2;
            }
            else if (c == '\'')
            {
                var value = new StringBuilder("'");
                i++;
                while (true)
                {
                    if (i >= text.Length)
                    {
                        throw new FormatException($"Unterminated string in filter expression: {text}");
                    }

                    if (text[i] == '\'' && i + 1 < text.Length && text[i + 1] == '\'')
                    {
                        value.Append('\'');
                        i += 2;
                    }
                    else if (text[i] == '\'')
                    {
                        i++;
                        break;
                    }
                    else
                    {
                        value.Append(text[i++]);
                    }
                }

                tokens.Add(value.ToString());
            }
            else if (c == '@' || char.IsLetter(c))
            {
                var start = i++;
                while (i < text.Length && (char.IsLetterOrDigit(text[i]) || text[i] == '_'))
                {
                    i++;
                }

                tokens.Add(text[start..i].ToUpperInvariant());
            }
            else
            {
                throw new FormatException($"Unexpected '{c}' in filter expression: {text}");
            }
        }

        return tokens;
    }

    /// <summary>Converts a T-SQL LIKE pattern (%, _ and [...] classes) to an anchored regex.</summary>
    internal static Regex LikeToRegex(string pattern, bool caseSensitive)
    {
        var regex = new StringBuilder("^");
        for (var i = 0; i < pattern.Length; i++)
        {
            switch (pattern[i])
            {
                case '%':
                    regex.Append(".*");
                    break;
                case '_':
                    regex.Append('.');
                    break;
                case '[' when pattern.IndexOf(']', i + 1) is var end and > 0:
                    var body = pattern[(i + 1)..end];
                    var negate = body.StartsWith('^');
                    regex.Append('[').Append(negate ? "^" : "").Append(Regex.Escape(negate ? body[1..] : body).Replace("\\-", "-", StringComparison.Ordinal)).Append(']');
                    i = end;
                    break;
                default:
                    regex.Append(Regex.Escape(pattern[i].ToString()));
                    break;
            }
        }

        var options = RegexOptions.CultureInvariant | (caseSensitive ? RegexOptions.None : RegexOptions.IgnoreCase);
        return new Regex(regex.Append('$').ToString(), options);
    }

    private sealed class Parser(List<string> tokens, bool caseSensitive)
    {
        private int position;

        private string? Peek => position < tokens.Count ? tokens[position] : null;

        public Func<string, string, bool> ParseOr()
        {
            var left = ParseAnd();
            while (Accept("OR"))
            {
                var l = left;
                var r = ParseAnd();
                left = (s, n) => l(s, n) || r(s, n);
            }

            return left;
        }

        public void ExpectEnd()
        {
            if (Peek is { } token)
            {
                throw new FormatException($"Unexpected '{token}' in filter expression.");
            }
        }

        private Func<string, string, bool> ParseAnd()
        {
            var left = ParseNot();
            while (Accept("AND"))
            {
                var l = left;
                var r = ParseNot();
                left = (s, n) => l(s, n) && r(s, n);
            }

            return left;
        }

        private Func<string, string, bool> ParseNot()
        {
            if (Accept("NOT"))
            {
                var inner = ParseNot();
                return (s, n) => !inner(s, n);
            }

            return ParsePrimary();
        }

        private Func<string, string, bool> ParsePrimary()
        {
            if (Accept("("))
            {
                var inner = ParseOr();
                Expect(")");
                return inner;
            }

            if (Accept("TRUE"))
            {
                return (_, _) => true;
            }

            if (Accept("FALSE"))
            {
                return (_, _) => false;
            }

            var variable = Next();
            Func<string, string, string> select = variable switch
            {
                "@SCHEMA" => (s, _) => s,
                "@NAME" => (_, n) => n,
                _ => throw new FormatException($"Unknown filter variable '{variable}'. Only @SCHEMA and @NAME are supported."),
            };

            var negate = Accept("NOT");
            var op = Next();
            var value = Next();
            if (!value.StartsWith('\''))
            {
                throw new FormatException($"Expected a quoted string after {variable} {op}.");
            }

            value = value[1..];
            var comparison = caseSensitive ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;
            Func<string, bool> test = op switch
            {
                "=" when !negate => v => string.Equals(v, value, comparison),
                "!=" when !negate => v => !string.Equals(v, value, comparison),
                "LIKE" => LikeToRegex(value, caseSensitive) is var regex && negate ? v => !regex.IsMatch(v) : v => regex.IsMatch(v),
                _ => throw new FormatException($"Unsupported filter operator '{(negate ? "NOT " : "")}{op}'."),
            };

            return (s, n) => test(select(s, n));
        }

        private bool Accept(string token)
        {
            if (Peek == token)
            {
                position++;
                return true;
            }

            return false;
        }

        private void Expect(string token)
        {
            if (!Accept(token))
            {
                throw new FormatException($"Expected '{token}' in filter expression.");
            }
        }

        private string Next() =>
            position < tokens.Count ? tokens[position++] : throw new FormatException("Filter expression ended unexpectedly.");
    }
}
