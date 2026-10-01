using Microsoft.SqlServer.Dac.Model;
using SqlSc.Core.WorkingFolders;

namespace SqlSc.Core.Modeling;

public static class TargetPlatform
{
    public const SqlServerVersion Default = SqlServerVersion.Sql160;

    /// <summary>Maps SERVERPROPERTY('EngineEdition') and SERVERPROPERTY('ProductMajorVersion') to a DacFx platform.</summary>
    public static SqlServerVersion FromServer(int engineEdition, int majorVersion) => engineEdition switch
    {
        5 => SqlServerVersion.SqlAzure,
        8 => SqlServerVersion.Sql160,
        _ => FromMajorVersion(majorVersion) ?? Default,
    };

    public static SqlServerVersion FromRedgateInfo(RedgateDatabaseInfo? info)
    {
        if (info is null)
        {
            return Default;
        }

        if (string.Equals(info.EngineEdition, "AzureSqlDatabase", StringComparison.OrdinalIgnoreCase))
        {
            return SqlServerVersion.SqlAzure;
        }

        return info.DatabaseVersion is { } v ? FromMajorVersion(v) ?? Default : Default;
    }

    public static SqlServerVersion Parse(string value) => value.ToUpperInvariant() switch
    {
        "AZURE" or "AZURESQL" or "SQLAZURE" => SqlServerVersion.SqlAzure,
        "MI" or "MANAGEDINSTANCE" => SqlServerVersion.Sql160,
        "2025" or "SQL170" => SqlServerVersion.Sql170,
        "2022" or "SQL160" => SqlServerVersion.Sql160,
        "2019" or "SQL150" => SqlServerVersion.Sql150,
        "2017" or "SQL140" => SqlServerVersion.Sql140,
        "2016" or "SQL130" => SqlServerVersion.Sql130,
        _ => Enum.TryParse<SqlServerVersion>(value, ignoreCase: true, out var parsed)
            ? parsed
            : throw new ArgumentException($"Unknown target platform '{value}'.", nameof(value)),
    };

    private static SqlServerVersion? FromMajorVersion(int major) => major switch
    {
        >= 17 => SqlServerVersion.Sql170,
        16 => SqlServerVersion.Sql160,
        15 => SqlServerVersion.Sql150,
        14 => SqlServerVersion.Sql140,
        13 => SqlServerVersion.Sql130,
        12 => SqlServerVersion.Sql120,
        11 => SqlServerVersion.Sql110,
        10 => SqlServerVersion.Sql100,
        _ => null,
    };
}
