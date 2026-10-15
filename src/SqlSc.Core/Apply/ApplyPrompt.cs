using System.Globalization;
using SqlSc.Core.Comparison;
using SqlSc.Core.StaticData;

namespace SqlSc.Core.Apply;

/// <summary>Asks before each change apply makes to a shared database: y (yes), n (no), a (yes to the rest) or q (no to the rest).</summary>
public sealed class ApplyPrompt(TextReader input, TextWriter output)
{
    public ApplyChoice Confirm(StatusReport status, IReadOnlyList<AppliedObject> objects, IReadOnlyList<AppliedData> data)
    {
        output.WriteLine($"{status.Server}/{status.Database} is a shared database: these changes affect everyone who uses it.");
        output.WriteLine("Apply each change? y = yes, n = no, a = yes to this and the rest, q = no to this and the rest.");
        bool? rest = null;
        var keptObjects = new List<AppliedObject>();
        foreach (var obj in objects)
        {
            if (rest ?? Ask(Describe(obj), ref rest))
            {
                keptObjects.Add(obj);
            }
        }

        var keptData = new List<AppliedData>();
        foreach (var table in data)
        {
            if (rest ?? Ask($"{"Data",-7} {"Table",-24} {table.Table}", ref rest))
            {
                keptData.Add(table);
            }
        }

        return new ApplyChoice(keptObjects, keptData);
    }

    private static string Describe(AppliedObject obj)
    {
        var who = obj.LastChange is { } e
            ? string.Create(CultureInfo.InvariantCulture, $"  last changed by {e.LoginName} {e.StartTime:yyyy-MM-dd HH:mm}")
            : "";
        return $"{obj.Action,-7} {obj.ObjectType,-24} {obj.Name}{who}";
    }

    /// <summary>The answer for one change. Sets <paramref name="rest"/> when the answer covers the remaining changes too.</summary>
    private bool Ask(string change, ref bool? rest)
    {
        while (true)
        {
            output.Write($"  {change}? [y/n/a/q] ");
            var answer = input.ReadLine();
            if (answer is null)
            {
                output.WriteLine();
                rest = false;
                return false;
            }

            switch (answer.Trim().ToUpperInvariant())
            {
                case "Y" or "YES":
                    return true;
                case "N" or "NO":
                    return false;
                case "A" or "ALL":
                    rest = true;
                    return true;
                case "Q" or "QUIT":
                    rest = false;
                    return false;
            }
        }
    }
}
