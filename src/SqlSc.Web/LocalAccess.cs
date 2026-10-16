using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Http;

namespace SqlSc.Web;

/// <summary>
/// Lets in only requests from the browser that opened the launch URL: the Host header must name this loopback port, and the
/// request must carry the launch token, either as <c>?token=</c> (which sets a cookie and redirects) or in that cookie.
/// </summary>
internal sealed class LocalAccess(string token, Func<int> port)
{
    public const string CookieName = "sqlsc-token";

    private readonly byte[] expected = Encoding.UTF8.GetBytes(token);

    public async Task InvokeAsync(HttpContext context, RequestDelegate next)
    {
        if (!IsAllowedHost(context.Request.Host))
        {
            context.Response.StatusCode = StatusCodes.Status400BadRequest;
            return;
        }

        if (context.Request.Query.TryGetValue("token", out var fromQuery))
        {
            if (!Matches(fromQuery.ToString()))
            {
                context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                return;
            }

            context.Response.Cookies.Append(CookieName, token, new CookieOptions
            {
                HttpOnly = true,
                SameSite = SameSiteMode.Strict,
                Path = "/",
                IsEssential = true,
            });
            context.Response.Redirect(context.Request.PathBase + context.Request.Path);
            return;
        }

        if (!Matches(context.Request.Cookies[CookieName]))
        {
            context.Response.StatusCode = StatusCodes.Status401Unauthorized;
            context.Response.ContentType = "text/plain; charset=utf-8";
            await context.Response.WriteAsync("Open the address that `sql-sc ui` printed, including its token.").ConfigureAwait(false);
            return;
        }

        context.Response.Headers.XFrameOptions = "DENY";
        context.Response.Headers.XContentTypeOptions = "nosniff";
        await next(context).ConfigureAwait(false);
    }

    internal bool IsAllowedHost(HostString host) =>
        host.Port == port() && host.Host is "127.0.0.1" or "localhost";

    private bool Matches(string? candidate) =>
        candidate is not null && CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(candidate), expected);
}
