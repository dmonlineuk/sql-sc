using System.Security.Cryptography;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using SqlSc.Web.Services;

namespace SqlSc.Web;

/// <summary>The local web UI: a Blazor Server app on a loopback port, open only to whoever has the launch token.</summary>
public sealed class WebUi : IAsyncDisposable
{
    private readonly WebApplication app;

    private WebUi(WebApplication app, string token)
    {
        this.app = app;
        Token = token;
    }

    public string Token { get; }

    /// <summary>The address to open in a browser, including the token. Only valid once <see cref="StartAsync"/> has run.</summary>
    public Uri LaunchUri => new($"http://127.0.0.1:{Port}/?token={Token}");

    public int Port { get; private set; }

    /// <summary>Builds the UI on <paramref name="port"/> (0 picks a free one).</summary>
    public static WebUi Create(int port, string? startFolder = null)
    {
        var token = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(32));
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            ContentRootPath = AppContext.BaseDirectory,
            EnvironmentName = Environments.Production,
        });
        builder.WebHost.UseStaticWebAssets();
        builder.WebHost.UseKestrel(k => k.Listen(System.Net.IPAddress.Loopback, port));
        builder.Logging.ClearProviders();
        builder.Services.AddRazorComponents().AddInteractiveServerComponents();
        builder.Services.AddSingleton(new UiState(startFolder));

        var app = builder.Build();
        var ui = new WebUi(app, token);
        var access = new LocalAccess(token, () => ui.Port);
        app.Use(access.InvokeAsync);
        app.UseAntiforgery();
        app.MapStaticAssets();
        app.MapRazorComponents<Components.App>().AddInteractiveServerRenderMode();
        return ui;
    }

    public async Task StartAsync()
    {
        await app.StartAsync().ConfigureAwait(false);
        Port = new Uri(app.Urls.First()).Port;
    }

    public Task WaitForShutdownAsync(CancellationToken cancellationToken) => app.WaitForShutdownAsync(cancellationToken);

    public async ValueTask DisposeAsync() => await app.DisposeAsync().ConfigureAwait(false);
}
