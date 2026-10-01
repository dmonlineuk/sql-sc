using System.Globalization;

namespace SqlSc.Core.Modeling;

/// <summary>
/// Encodes the origin of a batch (file + first line) as the DacFx model source name so objects can be traced back to files.
/// </summary>
public static class ScriptSource
{
    private const char Separator = '|';

    public static string Encode(string relativePath, int startLine) =>
        string.Create(CultureInfo.InvariantCulture, $"{relativePath}{Separator}{startLine}");

    public static (string File, int StartLine)? Decode(string? sourceName)
    {
        if (string.IsNullOrEmpty(sourceName))
        {
            return null;
        }

        var index = sourceName.LastIndexOf(Separator);
        if (index < 0 || !int.TryParse(sourceName.AsSpan(index + 1), NumberStyles.Integer, CultureInfo.InvariantCulture, out var line))
        {
            return (sourceName, 1);
        }

        return (sourceName[..index], line);
    }
}
