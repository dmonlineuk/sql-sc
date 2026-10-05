using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Data.SqlClient;

namespace SqlSc.Core.Apply;

/// <summary>Runs the SQLCMD-mode scripts DacFx generates: batches split on <c>GO</c>, with <c>:setvar</c> variables substituted.</summary>
internal static partial class SqlCmdScript
{
    public static IReadOnlyList<string> Batches(string script)
    {
        var variables = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var batches = new List<string>();
        var batch = new StringBuilder();
        void Flush()
        {
            if (!string.IsNullOrWhiteSpace(batch.ToString()))
            {
                batches.Add(batch.ToString().TrimEnd());
            }

            batch.Clear();
        }

        foreach (var text in Line().Matches(script).Select(m => m.Value))
        {
            var line = text.TrimEnd('\r', '\n');
            if (GoLine().IsMatch(line))
            {
                Flush();
            }
            else if (SetVarLine().Match(line) is { Success: true } setVar)
            {
                variables[setVar.Groups["name"].Value] = setVar.Groups["value"].Value;
            }
            else if (line.TrimStart().StartsWith(':'))
            {
                if (!OnErrorLine().IsMatch(line))
                {
                    throw new InvalidDataException($"Unsupported SQLCMD command in the deployment script: {line.Trim()}");
                }
            }
            else
            {
                batch.Append(Variable().Replace(line, m => variables.TryGetValue(m.Groups[1].Value, out var value)
                    ? value
                    : throw new InvalidDataException($"The deployment script uses SQLCMD variable $({m.Groups[1].Value}), which it doesn't set."))).Append(text.AsSpan(line.Length));
            }
        }

        Flush();
        return batches;
    }

    /// <summary>Runs every batch on one connection, stopping at the first error and rolling back any open transaction.</summary>
    /// <param name="messages">Receives the script's PRINT output.</param>
    public static void Run(string connectionString, string script, List<string> messages)
    {
        var batches = Batches(script);
        using var connection = new SqlConnection(connectionString);
        connection.InfoMessage += (_, e) => messages.AddRange(e.Errors.Cast<SqlError>().Select(m => m.Message));
        connection.Open();
        foreach (var batch in batches)
        {
            try
            {
                using var command = new SqlCommand(batch, connection) { CommandTimeout = 0 };
                command.ExecuteNonQuery();
            }
            catch (SqlException)
            {
                using var rollback = new SqlCommand("IF @@TRANCOUNT > 0 ROLLBACK TRANSACTION", connection);
                rollback.ExecuteNonQuery();
                throw;
            }
        }
    }

    [GeneratedRegex(@"[^\n]*\n|[^\n]+$", RegexOptions.CultureInvariant)]
    private static partial Regex Line();

    [GeneratedRegex(@"^\s*GO\s*$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex GoLine();

    [GeneratedRegex(@"^\s*:setvar\s+(?<name>\w+)\s+""?(?<value>.*?)""?\s*$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex SetVarLine();

    [GeneratedRegex(@"^\s*:on\s+error\s+(exit|ignore)\s*$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex OnErrorLine();

    [GeneratedRegex(@"\$\((\w+)\)", RegexOptions.CultureInvariant)]
    private static partial Regex Variable();
}
