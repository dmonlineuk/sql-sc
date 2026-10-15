using Microsoft.AspNetCore.Http;

namespace SqlSc.Web.Tests;

public class LocalAccessTests
{
    private const string Token = "0123456789abcdef";
    private const int Port = 5123;

    private readonly LocalAccess access = new(Token, () => Port);

    [Theory]
    [InlineData("127.0.0.1:5123", true)]
    [InlineData("localhost:5123", true)]
    [InlineData("127.0.0.1:5124", false)]
    [InlineData("127.0.0.1", false)]
    [InlineData("evil.example:5123", false)]
    [InlineData("192.168.1.10:5123", false)]
    public void OnlyThisLoopbackPortIsAllowed(string host, bool allowed) =>
        Assert.Equal(allowed, access.IsAllowedHost(HostString.FromUriComponent(host)));

    [Fact]
    public async Task RequestsWithoutTheTokenAreRefused()
    {
        var (status, reached) = await Send(c => { });

        Assert.Equal(StatusCodes.Status401Unauthorized, status);
        Assert.False(reached);
    }

    [Fact]
    public async Task OtherHostsAreRefusedEvenWithTheToken()
    {
        var (status, reached) = await Send(c =>
        {
            c.Request.Host = new HostString("attacker.example", Port);
            c.Request.Headers.Cookie = $"{LocalAccess.CookieName}={Token}";
        });

        Assert.Equal(StatusCodes.Status400BadRequest, status);
        Assert.False(reached);
    }

    [Fact]
    public async Task TheLaunchTokenSetsACookieAndRedirectsWithoutIt()
    {
        HttpContext? context = null;
        var (status, reached) = await Send(c =>
        {
            c.Request.Path = "/commit";
            c.Request.QueryString = new QueryString($"?token={Token}");
            context = c;
        });

        Assert.Equal(StatusCodes.Status302Found, status);
        Assert.False(reached);
        Assert.Equal("/commit", context!.Response.Headers.Location.ToString());
        var cookie = context.Response.Headers.SetCookie.ToString();
        Assert.StartsWith($"{LocalAccess.CookieName}={Token}", cookie, StringComparison.Ordinal);
        Assert.Contains("httponly", cookie, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("samesite=strict", cookie, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task AWrongTokenIsRefused()
    {
        var (status, reached) = await Send(c => c.Request.QueryString = new QueryString("?token=0123456789abcdeX"));

        Assert.Equal(StatusCodes.Status401Unauthorized, status);
        Assert.False(reached);
    }

    [Fact]
    public async Task TheCookieLetsRequestsThrough()
    {
        var (status, reached) = await Send(c => c.Request.Headers.Cookie = $"{LocalAccess.CookieName}={Token}");

        Assert.Equal(StatusCodes.Status200OK, status);
        Assert.True(reached);
    }

    private async Task<(int Status, bool Reached)> Send(Action<HttpContext> setup)
    {
        var context = new DefaultHttpContext();
        context.Request.Host = new HostString("127.0.0.1", Port);
        context.Request.Path = "/";
        context.Response.Body = new MemoryStream();
        setup(context);
        var reached = false;
        await access.InvokeAsync(context, _ =>
        {
            reached = true;
            return Task.CompletedTask;
        });
        return (context.Response.StatusCode, reached);
    }
}
