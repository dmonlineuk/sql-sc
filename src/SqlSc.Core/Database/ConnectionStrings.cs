using Microsoft.Data.SqlClient;

namespace SqlSc.Core.Database;

public static class ConnectionStrings
{
    public const string PasswordVariable = "SQLSC_PASSWORD";

    /// <summary>Removes any password so the connection string can be stored.</summary>
    public static string WithoutPassword(string connectionString, out bool removed)
    {
        var builder = new SqlConnectionStringBuilder(connectionString);
        removed = !string.IsNullOrEmpty(builder.Password);
        builder.Remove("Password");
        return builder.ConnectionString;
    }

    /// <summary>Adds <paramref name="password"/> to a SQL-auth connection string that has none.</summary>
    public static string WithPassword(string connectionString, string? password)
    {
        var builder = new SqlConnectionStringBuilder(connectionString);
        if (string.IsNullOrEmpty(password) || !string.IsNullOrEmpty(builder.Password) || string.IsNullOrEmpty(builder.UserID)
            || builder.Authentication is not (SqlAuthenticationMethod.NotSpecified or SqlAuthenticationMethod.SqlPassword))
        {
            return connectionString;
        }

        builder.Password = password;
        return builder.ConnectionString;
    }

    /// <summary>Whether this is a SQL-auth connection string without a password.</summary>
    public static bool NeedsPassword(string connectionString)
    {
        var builder = new SqlConnectionStringBuilder(connectionString);
        return !builder.IntegratedSecurity && string.IsNullOrEmpty(builder.Password) && !string.IsNullOrEmpty(builder.UserID)
            && builder.Authentication is SqlAuthenticationMethod.NotSpecified or SqlAuthenticationMethod.SqlPassword;
    }

    public static string DescribeAuthentication(string connectionString)
    {
        var builder = new SqlConnectionStringBuilder(connectionString);
        return builder switch
        {
            { IntegratedSecurity: true } => "Windows (integrated)",
            { Authentication: not (SqlAuthenticationMethod.NotSpecified or SqlAuthenticationMethod.SqlPassword) } => $"Entra ID ({builder.Authentication})",
            _ => $"SQL ({builder.UserID})",
        };
    }

    /// <summary>"server/database", safe to print.</summary>
    public static string Describe(string connectionString)
    {
        var builder = new SqlConnectionStringBuilder(connectionString);
        return $"{builder.DataSource}/{builder.InitialCatalog}";
    }
}
