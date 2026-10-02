using SqlSc.Core.Database;

namespace SqlSc.Core.Tests;

public class ConnectionStringsTests
{
    [Theory]
    [InlineData("Server=s;Database=d;User ID=u;Password=secret")]
    [InlineData("Server=s;Database=d;uid=u;pwd=secret")]
    public void PasswordIsRemoved(string connection)
    {
        var stored = ConnectionStrings.WithoutPassword(connection, out var removed);

        Assert.True(removed);
        Assert.DoesNotContain("secret", stored, StringComparison.Ordinal);
        Assert.Contains("u", stored, StringComparison.Ordinal);
    }

    [Fact]
    public void ConnectionWithoutPasswordIsUnchanged()
    {
        ConnectionStrings.WithoutPassword("Server=s;Database=d;Integrated Security=true", out var removed);

        Assert.False(removed);
    }

    [Fact]
    public void PasswordIsAddedToSqlAuthOnly()
    {
        Assert.Contains("Password=pw", ConnectionStrings.WithPassword("Server=s;User ID=u", "pw"), StringComparison.Ordinal);
        Assert.Contains("Password=original", ConnectionStrings.WithPassword("Server=s;User ID=u;Password=original", "pw"), StringComparison.Ordinal);
        Assert.Equal("Server=s;Integrated Security=true", ConnectionStrings.WithPassword("Server=s;Integrated Security=true", "pw"));
        Assert.Equal(
            "Server=s;Authentication=Active Directory Default",
            ConnectionStrings.WithPassword("Server=s;Authentication=Active Directory Default", "pw"));
        Assert.Equal("Server=s;User ID=u", ConnectionStrings.WithPassword("Server=s;User ID=u", null));
    }

    [Theory]
    [InlineData("Server=s;User ID=u", true)]
    [InlineData("Server=s;User ID=u;Password=p", false)]
    [InlineData("Server=s;Integrated Security=true", false)]
    [InlineData("Server=s;User ID=u@x.com;Authentication=Active Directory Interactive", false)]
    public void DetectsMissingSqlPassword(string connection, bool expected)
    {
        Assert.Equal(expected, ConnectionStrings.NeedsPassword(connection));
    }

    [Theory]
    [InlineData("Server=s;Database=d;User ID=dan;Password=secret", "SQL (dan)")]
    [InlineData("Server=s;Database=d;Integrated Security=true", "Windows (integrated)")]
    [InlineData("Server=s;Database=d;Authentication=Active Directory Default", "Entra ID (ActiveDirectoryDefault)")]
    public void AuthenticationIsDescribedWithoutSecrets(string connection, string expected)
    {
        Assert.Equal(expected, ConnectionStrings.DescribeAuthentication(connection));
        Assert.Equal("s/d", ConnectionStrings.Describe(connection));
    }
}
