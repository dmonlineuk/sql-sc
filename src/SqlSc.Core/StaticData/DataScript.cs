using System.Data.SqlTypes;
using System.Globalization;
using System.Text;
using Microsoft.SqlServer.TransactSql.ScriptDom;

namespace SqlSc.Core.StaticData;

/// <summary>Reads and writes static data scripts: one <c>INSERT</c> per row, as Redgate writes them.</summary>
internal static class DataScript
{
    /// <summary>
    /// The script's <c>INSERT</c> statements with their target changed to <paramref name="target"/>, so that the folder's rows can
    /// be loaded into a copy of the table with SQL Server parsing the values. <c>SET IDENTITY_INSERT</c> is left out, as the copy
    /// has no identity column; any other statement is an error.
    /// </summary>
    public static IReadOnlyList<string> Retarget(string script, string schema, string table, string target)
    {
        var fragment = new TSql170Parser(true).Parse(new StringReader(script), out var errors);
        if (errors.Count > 0)
        {
            throw new InvalidDataException(Invariant($"line {errors[0].Line}: {errors[0].Message}"));
        }

        var statements = new List<string>();
        foreach (var statement in ((TSqlScript)fragment).Batches.SelectMany(b => b.Statements))
        {
            switch (statement)
            {
                case SetIdentityInsertStatement:
                    break;
                case InsertStatement { InsertSpecification.Target: NamedTableReference { SchemaObject: var name } }
                    when name.ServerIdentifier is null && name.DatabaseIdentifier is null
                        && string.Equals(name.SchemaIdentifier?.Value ?? schema, schema, StringComparison.OrdinalIgnoreCase)
                        && string.Equals(name.BaseIdentifier.Value, table, StringComparison.OrdinalIgnoreCase):
                    var text = script.Substring(statement.StartOffset, statement.FragmentLength);
                    var offset = name.StartOffset - statement.StartOffset;
                    statements.Add(string.Concat(text.AsSpan(0, offset), target, text.AsSpan(offset + name.FragmentLength)));
                    break;
                default:
                    throw new InvalidDataException(Invariant($"line {statement.StartLine}: only INSERT INTO {DataTable.Quote(schema)}.{DataTable.Quote(table)} and SET IDENTITY_INSERT are allowed in a static data script."));
            }
        }

        return statements;
    }

    /// <summary>The static data script for <paramref name="rows"/>, which are in key order and hold <see cref="DataTable.Insertable"/> values.</summary>
    public static string Write(DataTable table, IEnumerable<object?[]> rows)
    {
        var columns = table.Insertable;
        var insert = $"INSERT INTO {table.Quoted} ({string.Join(", ", columns.Select(c => c.Quoted))}) VALUES (";
        var sb = new StringBuilder();
        foreach (var row in rows)
        {
            sb.Append(insert);
            for (var i = 0; i < columns.Count; i++)
            {
                sb.Append(i == 0 ? "" : ", ").Append(Literal(row[i], columns[i]));
            }

            sb.Append(")\r\n");
        }

        return sb.Length == 0 || !table.HasIdentity
            ? sb.ToString()
            : $"SET IDENTITY_INSERT {table.Quoted} ON\r\n{sb}SET IDENTITY_INSERT {table.Quoted} OFF\r\n";
    }

    /// <summary>A T-SQL literal for a value of <paramref name="column"/> that means the same whatever the session's language or date format.</summary>
    public static string Literal(object? value, DataColumn column) => value switch
    {
        null or DBNull => "NULL",
        bool b => b ? "1" : "0",
        SqlDecimal d => d.ToString(),
        byte[] bytes when column.Type is "geography" or "geometry" or "hierarchyid" => $"CAST({Hex(bytes)} AS {column.Type})",
        byte[] bytes => Hex(bytes),
        string s when column.Type is "char" or "varchar" or "text" => Quote(s, ""),
        string s => Quote(s, "N"),
        Guid g => $"'{g.ToString("D").ToUpperInvariant()}'",
        DateTime dt => column.Type switch
        {
            "date" => $"'{dt:yyyy-MM-dd}'",
            "datetime" => $"'{dt.ToString("yyyy-MM-dd'T'HH:mm:ss.fff", CultureInfo.InvariantCulture)}'",
            "smalldatetime" => $"'{dt.ToString("yyyy-MM-dd'T'HH:mm:ss", CultureInfo.InvariantCulture)}'",
            _ => $"'{dt.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture)}{Fraction(dt.Ticks % TimeSpan.TicksPerSecond, column.Scale)}'",
        },
        DateTimeOffset dto => $"'{dto.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture)}{Fraction(dto.Ticks % TimeSpan.TicksPerSecond, column.Scale)} {dto.ToString("zzz", CultureInfo.InvariantCulture)}'",
        TimeSpan t => $"'{t.ToString(@"hh\:mm\:ss", CultureInfo.InvariantCulture)}{Fraction(t.Ticks % TimeSpan.TicksPerSecond, column.Scale)}'",
        double d => Float(d),
        float f => Float(f),
        IFormattable number => number.ToString(null, CultureInfo.InvariantCulture),
        _ => throw new InvalidDataException($"{column.Name}: sql-sc can't write {column.Type} values into a static data script."),
    };

    /// <summary>A float literal (with an exponent, so SQL Server doesn't read it as a decimal) that round-trips exactly.</summary>
    private static string Float(double value) =>
        value.ToString("R", CultureInfo.InvariantCulture) is var r && r.Contains('E', StringComparison.Ordinal) ? r : r + "E0";

    private static string Fraction(long ticks, int scale) =>
        scale <= 0 ? "" : "." + ticks.ToString("0000000", CultureInfo.InvariantCulture)[..Math.Min(scale, 7)];

    private static string Hex(byte[] bytes) => "0x" + Convert.ToHexString(bytes);

    private static string Quote(string value, string prefix) => $"{prefix}'{value.Replace("'", "''", StringComparison.Ordinal)}'";

    private static string Invariant(FormattableString value) => value.ToString(CultureInfo.InvariantCulture);
}
