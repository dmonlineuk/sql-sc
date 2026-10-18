using SqlSc.Core.ChangeTracking;
using SqlSc.Core.Comparison;

namespace SqlSc.Core.Tests;

public class ObjectLocksTests
{
    private static readonly DateTime Time = new(2026, 10, 1, 12, 0, 0, DateTimeKind.Unspecified);

    private static ObjectChange Change(string name, ObjectLock? objectLock = null) =>
        new(ObjectStatus.Modified, "Procedure", name, null, null, [], Lock: objectLock);

    [Fact]
    public void LocksAttachByNameIgnoringCase()
    {
        var mine = new ObjectLock("[Sales].[GetCustomer]", "dan", Time, null, true);

        var changes = ObjectLocks.Attach([Change("[sales].[getcustomer]"), Change("[Sales].[Other]")], [mine]);

        Assert.Equal([mine, null], changes.Select(c => c.Lock));
    }

    [Fact]
    public void OthersLocksAreSkippedOrRefusedUnlessForced()
    {
        var theirs = new ObjectLock("[Sales].[A]", "sam", Time, "half done", false);
        ObjectChange[] selected = [Change("[Sales].[A]", theirs), Change("[Sales].[B]", theirs with { Name = "[Sales].[B]" }), Change("[Sales].[C]", theirs with { Name = "[Sales].[C]", Mine = true }), Change("[Sales].[D]")];
        List<string> problems = [], skipped = [];

        var kept = ObjectLocks.Filter(selected, "apply", new HashSet<string>(["[Sales].[A]"], StringComparer.OrdinalIgnoreCase), force: false, problems, skipped);

        Assert.Equal(["[Sales].[C]", "[Sales].[D]"], kept.Select(c => c.Name));
        Assert.Equal(["[Sales].[A] is locked by sam since 2026-10-01 12:00 (half done); apply it with --force to go ahead anyway."], problems);
        Assert.Equal(["[Sales].[B] is locked by sam since 2026-10-01 12:00 (half done); apply it with --force to go ahead anyway."], skipped);

        Assert.Equal(4, ObjectLocks.Filter(selected, "apply", new HashSet<string>(), force: true, problems, skipped).Count);
    }
}
