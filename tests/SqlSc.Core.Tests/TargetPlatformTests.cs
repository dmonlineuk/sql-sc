using Microsoft.SqlServer.Dac.Model;
using SqlSc.Core.ChangeTracking;
using SqlSc.Core.Modeling;

namespace SqlSc.Core.Tests;

public class TargetPlatformTests
{
    [Theory]
    [InlineData(3, 16, SqlServerVersion.Sql160)]
    [InlineData(2, 15, SqlServerVersion.Sql150)]
    [InlineData(5, 12, SqlServerVersion.SqlAzure)]
    [InlineData(8, 12, SqlServerVersion.Sql160)]
    [InlineData(3, 17, SqlServerVersion.Sql170)]
    public void MapsServerProperties(int engineEdition, int majorVersion, SqlServerVersion expected)
    {
        Assert.Equal(expected, TargetPlatform.FromServer(engineEdition, majorVersion));
    }

    [Theory]
    [InlineData("2022", SqlServerVersion.Sql160)]
    [InlineData("mi", SqlServerVersion.Sql160)]
    [InlineData("azure", SqlServerVersion.SqlAzure)]
    [InlineData("Sql150", SqlServerVersion.Sql150)]
    public void ParsesPlatformNames(string value, SqlServerVersion expected)
    {
        Assert.Equal(expected, TargetPlatform.Parse(value));
    }

    [Theory]
    [InlineData(@"C:\Program Files\Microsoft SQL Server\MSSQL16.MSSQLSERVER\MSSQL\Log\log_12.trc", @"C:\Program Files\Microsoft SQL Server\MSSQL16.MSSQLSERVER\MSSQL\Log\log.trc")]
    [InlineData("/var/opt/mssql/log/log_3.trc", "/var/opt/mssql/log/log.trc")]
    public void DefaultTraceRolloverPath(string current, string expected)
    {
        Assert.Equal(expected, DefaultTraceReader.RolloverBasePath(current));
    }
}
