using System.Globalization;
using System.Text;

namespace SqlSc.Core.Export;

/// <summary>Line diffs of script files in the unified format <c>git diff</c> uses, with three lines of context.</summary>
public static class UnifiedDiff
{
    public const string NoFile = "/dev/null";

    private const int Context = 3;

    private const long MaxCells = 4_000_000;

    /// <summary>The diff from <paramref name="before"/> to <paramref name="after"/> (null for no file), or empty if their lines are the same.</summary>
    public static string Create(string? before, string? after, string beforeLabel, string afterLabel)
    {
        var edits = Edits(Lines(before), Lines(after));
        if (edits.TrueForAll(e => e.Op == ' '))
        {
            return string.Empty;
        }

        var aPos = new int[edits.Count + 1];
        var bPos = new int[edits.Count + 1];
        for (var i = 0; i < edits.Count; i++)
        {
            aPos[i + 1] = aPos[i] + (edits[i].Op == '+' ? 0 : 1);
            bPos[i + 1] = bPos[i] + (edits[i].Op == '-' ? 0 : 1);
        }

        var text = new StringBuilder().Append("--- ").Append(beforeLabel).Append('\n').Append("+++ ").Append(afterLabel).Append('\n');
        var next = 0;
        while (next < edits.Count)
        {
            if (edits[next].Op == ' ')
            {
                next++;
                continue;
            }

            var start = Math.Max(0, next - Context);
            var end = next;
            var j = next;
            while (j < edits.Count)
            {
                if (edits[j].Op != ' ')
                {
                    end = ++j;
                    continue;
                }

                var k = j;
                while (k < edits.Count && edits[k].Op == ' ')
                {
                    k++;
                }

                if (k == edits.Count || k - j > 2 * Context)
                {
                    break;
                }

                j = k;
            }

            var stop = Math.Min(edits.Count, end + Context);
            text.Append(CultureInfo.InvariantCulture, $"@@ -{Range(aPos[start], aPos[stop] - aPos[start])} +{Range(bPos[start], bPos[stop] - bPos[start])} @@\n");
            for (var i = start; i < stop; i++)
            {
                text.Append(edits[i].Op).Append(edits[i].Line).Append('\n');
            }

            next = stop;
        }

        return text.ToString();
    }

    private static string Range(int start, int count) =>
        count == 1 ? (start + 1).ToString(CultureInfo.InvariantCulture) : string.Create(CultureInfo.InvariantCulture, $"{(count == 0 ? start : start + 1)},{count}");

    private static string[] Lines(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return [];
        }

        var lines = text.Split('\n').Select(l => l.TrimEnd('\r')).ToList();
        if (lines[^1].Length == 0)
        {
            lines.RemoveAt(lines.Count - 1);
        }

        return [.. lines];
    }

    private static List<(char Op, string Line)> Edits(string[] a, string[] b)
    {
        var prefix = 0;
        while (prefix < a.Length && prefix < b.Length && a[prefix] == b[prefix])
        {
            prefix++;
        }

        var suffix = 0;
        while (suffix < a.Length - prefix && suffix < b.Length - prefix && a[^(suffix + 1)] == b[^(suffix + 1)])
        {
            suffix++;
        }

        var (n, m) = (a.Length - prefix - suffix, b.Length - prefix - suffix);
        var edits = new List<(char Op, string Line)>(a.Length + b.Length);
        edits.AddRange(a.Take(prefix).Select(l => (' ', l)));
        if ((long)(n + 1) * (m + 1) > MaxCells)
        {
            edits.AddRange(a.Skip(prefix).Take(n).Select(l => ('-', l)));
            edits.AddRange(b.Skip(prefix).Take(m).Select(l => ('+', l)));
        }
        else
        {
            var width = m + 1;
            var common = new int[(n + 1) * width];
            for (var x = n - 1; x >= 0; x--)
            {
                for (var y = m - 1; y >= 0; y--)
                {
                    common[(x * width) + y] = a[prefix + x] == b[prefix + y]
                        ? common[((x + 1) * width) + y + 1] + 1
                        : Math.Max(common[((x + 1) * width) + y], common[(x * width) + y + 1]);
                }
            }

            var (i, j) = (0, 0);
            while (i < n || j < m)
            {
                if (i < n && j < m && a[prefix + i] == b[prefix + j])
                {
                    edits.Add((' ', a[prefix + i++]));
                    j++;
                }
                else if (j == m || (i < n && common[((i + 1) * width) + j] >= common[(i * width) + j + 1]))
                {
                    edits.Add(('-', a[prefix + i++]));
                }
                else
                {
                    edits.Add(('+', b[prefix + j++]));
                }
            }
        }

        edits.AddRange(a.Skip(a.Length - suffix).Select(l => (' ', l)));
        return edits;
    }
}
