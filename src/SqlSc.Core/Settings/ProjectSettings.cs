using System.Text.Json;
using Microsoft.SqlServer.Dac;

namespace SqlSc.Core.Settings;

/// <summary>Comparison options shared by everyone using a working folder.</summary>
public sealed record CompareSettings
{
    public bool IgnoreWhitespace { get; init; } = true;

    public bool IgnoreComments { get; init; }

    public bool IgnoreColumnOrder { get; init; }

    public bool IgnorePermissions { get; init; }

    public bool IgnoreRoleMembership { get; init; }

    public bool IgnoreExtendedProperties { get; init; }

    public DacDeployOptions ToDeployOptions() => new()
    {
        DropObjectsNotInSource = true,
        BlockOnPossibleDataLoss = true,
        IgnoreKeywordCasing = true,
        IgnoreSemicolonBetweenStatements = true,
        AllowIncompatiblePlatform = true,
        ScriptDatabaseOptions = false,
        IgnoreWhitespace = IgnoreWhitespace,
        IgnoreComments = IgnoreComments,
        IgnoreColumnOrder = IgnoreColumnOrder,
        IgnorePermissions = IgnorePermissions,
        IgnoreRoleMembership = IgnoreRoleMembership,
        IgnoreExtendedProperties = IgnoreExtendedProperties,
    };

    /// <summary>Whether a child object of this type should be compared at all.</summary>
    public bool Includes(string objectType) => objectType switch
    {
        "Permission" => !IgnorePermissions,
        "RoleMembership" => !IgnoreRoleMembership,
        "ExtendedProperty" => !IgnoreExtendedProperties,
        _ => true,
    };
}

/// <summary>
/// Team settings committed with the working folder as <c>sql-sc.json</c>. Never contains connection details.
/// </summary>
public sealed record ProjectSettings
{
    public const string FileName = "sql-sc.json";

    public const string DefaultFilter = "Filter.scpf";

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        WriteIndented = true,
    };

    /// <summary>Object filter file, relative to the working folder. Defaults to <c>Filter.scpf</c> when present.</summary>
    public string? Filter { get; init; }

    public CompareSettings Compare { get; init; } = new();

    public static ProjectSettings Load(string folder)
    {
        var path = Path.Combine(folder, FileName);
        if (!File.Exists(path))
        {
            return new ProjectSettings();
        }

        try
        {
            return JsonSerializer.Deserialize<ProjectSettings>(File.ReadAllText(path), Json) ?? new ProjectSettings();
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException($"'{path}' is not valid: {ex.Message}", ex);
        }
    }

    public string Serialize() => JsonSerializer.Serialize(this, Json);
}
