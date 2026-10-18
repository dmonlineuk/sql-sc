using SqlSc.Core.Settings;
using SqlSc.Web.Components.Pages;
using SqlSc.Web.Services;

namespace SqlSc.Web.Tests;

public class SetupTests
{
    [Theory]
    [InlineData("Server=s;Database=d;User ID=u", "Server=s;Database=d;User ID=u;Password=secret", true)]
    [InlineData("Data Source=s;Initial Catalog=d;User ID=u", "server = s; database = d; user id = u", true)]
    [InlineData("Server=s;Database=d;User ID=u", "Server=s;Database=d;User ID=other", false)]
    [InlineData("Server=s;Database=d;User ID=u", "Server=s;Database=other;User ID=u", false)]
    public void ConnectionsAreTheSameApartFromThePassword(string saved, string edited, bool same) =>
        Assert.Equal(same, Setup.SameConnection(saved, edited));

    [Fact]
    public void RememberedPasswordsAreUsedForTheirLinkOnly()
    {
        using var state = new UiState(null);
        var folder = Path.Combine(Path.GetTempPath(), "sqlsc-setup-test");
        var link = new Link(folder, "Server=s;Database=d;User ID=u", DatabaseMode.Shared);

        state.RememberPassword(folder, "secret");

        Assert.True(state.HasPassword(folder));
        Assert.False(state.HasPassword(folder + "-other"));
        Assert.Contains("Password=secret", state.ConnectionFor(link), StringComparison.Ordinal);

        state.RememberPassword(folder, null);

        Assert.False(state.HasPassword(folder));
    }
}
