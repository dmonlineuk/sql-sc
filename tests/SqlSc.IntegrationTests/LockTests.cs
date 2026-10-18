using Microsoft.Data.SqlClient;
using SqlSc.Core.Apply;
using SqlSc.Core.ChangeTracking;
using SqlSc.Core.Comparison;
using SqlSc.Core.Export;
using SqlSc.Core.StaticData;
using SqlSc.Core.WorkingFolders;

namespace SqlSc.IntegrationTests;

[Collection(SqlServerGroup.Name)]
public class LockTests(SqlServerFixture sql)
{
    private const string Procedure = "[Sales].[GetCustomer]";

    private static WorkingFolder Demo => WorkingFolder.Open(SqlServerFixture.DemoFolder);

    [Fact]
    public void LocksAreSharedAndKeepOthersFromExportingOrApplying()
    {
        var database = sql.CreateDatabaseFromFolder(SqlServerFixture.DemoFolder);
        var owner = sql.ConnectionString(database);
        var developer = Developer(database);
        var developerLogin = new SqlConnectionStringBuilder(developer).UserID;

        var cantCreate = Assert.Throws<InvalidOperationException>(() => ObjectLocks.Lock(developer, ["Sales.Customer"], null));
        Assert.Contains("sql-sc locks --script-only", cantCreate.Message, StringComparison.Ordinal);

        var ownerLock = ObjectLocks.Lock(owner, ["Sales.Customer"], null);
        Assert.Empty(ownerLock.Problems);
        Assert.Equal(("[Sales].[Customer]", true), (ownerLock.Locks.Single().Name, ownerLock.Locks.Single().Mine));

        var developerLock = ObjectLocks.Lock(developer, ["Sales.GetCustomer", "[Sales].[Customer]", "a.b.c"], "half done");
        Assert.Equal([Procedure], developerLock.Locks.Select(l => l.Name));
        Assert.Equal(2, developerLock.Problems.Count);
        Assert.StartsWith("[Sales].[Customer] is already locked by sa since ", developerLock.Problems[0], StringComparison.Ordinal);
        Assert.StartsWith("a.b.c isn't an object name", developerLock.Problems[1], StringComparison.Ordinal);

        sql.Execute(database, "ALTER PROCEDURE [Sales].[GetCustomer] @CustomerId int AS SELECT @CustomerId AS CustomerId");
        var report = StatusService.GetStatus(Demo, owner);
        var locked = report.Changes.Single(c => c.Name == Procedure).Lock;
        Assert.Equal((developerLogin, "half done", false), (locked?.LockedBy, locked?.Reason, locked?.Mine));
        Assert.Equal(2, report.Locks?.Count);
        Assert.DoesNotContain(report.Changes, c => c.Name.Contains("sqlsc", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(StaticDataService.Tables(owner), t => t.Schema == "sqlsc");

        var all = ExportService.Export(Demo, owner, new ExportSelection([], All: true), dryRun: true);
        Assert.DoesNotContain(all.Selected, c => c.Name == Procedure);
        Assert.Contains(all.Skipped, s => s.StartsWith($"{Procedure} is locked by {developerLogin}", StringComparison.Ordinal));
        var named = ExportService.Export(Demo, owner, new ExportSelection(["Sales.GetCustomer"]), dryRun: true);
        Assert.Contains(named.Problems, p => p.StartsWith($"{Procedure} is locked by {developerLogin}", StringComparison.Ordinal));
        Assert.Contains(ExportService.Export(Demo, owner, new ExportSelection(["Sales.GetCustomer"], Force: true), dryRun: true).Selected, c => c.Name == Procedure);

        var applied = ApplyService.Apply(Demo, owner, [], all: true, scriptOnly: true);
        Assert.DoesNotContain(applied.Objects, o => o.Name == Procedure);
        Assert.Contains(applied.Skipped ?? [], s => s.StartsWith($"{Procedure} is locked by {developerLogin}", StringComparison.Ordinal));

        Assert.StartsWith($"{Procedure} is locked by {developerLogin}", ObjectLocks.Unlock(owner, [Procedure], force: false).Problems.Single(), StringComparison.Ordinal);
        Assert.Equal([Procedure], ObjectLocks.Unlock(owner, [Procedure], force: true).Locks.Select(l => l.Name));
        Assert.Equal(["Sales.Nothing isn't locked."], ObjectLocks.Unlock(owner, ["Sales.Nothing"], force: false).Problems);
        Assert.Single(ObjectLocks.Unlock(developer, ["Sales.Customer"], force: false).Problems);
        Assert.Single(ObjectLocks.Unlock(owner, ["Sales.Customer"], force: false).Locks);
        Assert.Empty(ObjectLocks.Read(developer));
    }

    private string Developer(string database)
    {
        var login = "dev_" + Guid.NewGuid().ToString("N")[..8];
        const string password = "Dev-Passw0rd!";
        sql.Execute("master", $"CREATE LOGIN [{login}] WITH PASSWORD = '{password}', CHECK_POLICY = OFF");
        sql.Execute(database, $"CREATE USER [{login}] FOR LOGIN [{login}];");
        return new SqlConnectionStringBuilder(sql.ConnectionString(database)) { UserID = login, Password = password }.ConnectionString;
    }
}
