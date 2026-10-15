using System.Globalization;

namespace SqlSc.Web.Services;

/// <summary>One row of a side-by-side diff. A null line number means that side has no line in this row.</summary>
public sealed record DiffRow(int? LeftNumber, string? Left, int? RightNumber, string? Right, bool IsHunk = false)
{
    public bool Changed => !IsHunk && Left != Right;
}

/// <summary>Turns a unified diff (as <c>UnifiedDiff.Create</c> writes it) into rows with the old text on the left and the new on the right.</summary>
public static class SideBySideDiff
{
    public static IReadOnlyList<DiffRow> Parse(string? unified)
    {
        var rows = new List<DiffRow>();
        if (string.IsNullOrEmpty(unified))
        {
            return rows;
        }

        var removed = new List<(int Number, string Text)>();
        var added = new List<(int Number, string Text)>();
        var (left, right) = (0, 0);

        void Flush()
        {
            for (var i = 0; i < Math.Max(removed.Count, added.Count); i++)
            {
                var (l, r) = (i < removed.Count ? removed[i] : default, i < added.Count ? added[i] : default);
                rows.Add(new DiffRow(i < removed.Count ? l.Number : null, i < removed.Count ? l.Text : null, i < added.Count ? r.Number : null, i < added.Count ? r.Text : null));
            }

            removed.Clear();
            added.Clear();
        }

        foreach (var line in unified.Split('\n'))
        {
            if (line.StartsWith("--- ", StringComparison.Ordinal) || line.StartsWith("+++ ", StringComparison.Ordinal))
            {
                continue;
            }

            if (line.StartsWith("@@", StringComparison.Ordinal))
            {
                Flush();
                (left, right) = HunkStart(line);
                rows.Add(new DiffRow(null, line, null, line, IsHunk: true));
            }
            else if (line.StartsWith('-'))
            {
                removed.Add((left++, line[1..]));
            }
            else if (line.StartsWith('+'))
            {
                added.Add((right++, line[1..]));
            }
            else if (line.StartsWith(' '))
            {
                Flush();
                rows.Add(new DiffRow(left++, line[1..], right++, line[1..]));
            }
        }

        Flush();
        return rows;
    }

    private static (int Left, int Right) HunkStart(string header)
    {
        var parts = header.Split(' ');
        return (Start(parts.ElementAtOrDefault(1)), Start(parts.ElementAtOrDefault(2)));

        static int Start(string? range)
        {
            var digits = range?.TrimStart('-', '+').Split(',')[0];
            return int.TryParse(digits, NumberStyles.None, CultureInfo.InvariantCulture, out var start) ? Math.Max(start, 1) : 1;
        }
    }
}
