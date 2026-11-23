using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace TaskHubGreeter.Client.Tests;

public sealed record RecordedRequest(string Method, string Path, string Query, IReadOnlyDictionary<string, string> Headers, string Body)
{
    public string? Header(string name) => Headers.TryGetValue(name, out var v) ? v : null;
}

/// <summary>
/// A tiny stand-in for TaskHub: Kestrel on 127.0.0.1 with a random port, the same token/Origin
/// checks, and a per-test handler. Writes api.json + api.token into <see cref="Home"/>.
/// </summary>
public sealed class FakeTaskHub : IAsyncDisposable
{
    private WebApplication? _app;

    private FakeTaskHub(string home, string token)
    {
        Home = home;
        Token = token;
    }

    public string Home { get; }

    /// <summary>The token the server accepts.</summary>
    public string Token { get; set; }

    public int Port { get; private set; }

    public ConcurrentQueue<RecordedRequest> Requests { get; } = new();

    /// <summary>Handles authorised requests. Default: 404.</summary>
    public Func<HttpContext, RecordedRequest, Task>? Handler { get; set; }

    public static async Task<FakeTaskHub> StartAsync(string home, string token = "tok-1", bool writeDiscovery = true)
    {
        var hub = new FakeTaskHub(home, token);
        var builder = WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        var app = builder.Build();
        app.UseWebSockets();
        app.Run(hub.HandleAsync);
        await app.StartAsync();
        hub._app = app;
        hub.Port = new Uri(app.Urls.First()).Port;
        if (writeDiscovery) hub.WriteDiscovery();
        return hub;
    }

    /// <summary>Writes api.json (pointing at <paramref name="port"/>, default this server) and the token file.</summary>
    public void WriteDiscovery(int? port = null, string? token = null)
    {
        Directory.CreateDirectory(Home);
        var tokenFile = Path.Combine(Home, "api.token");
        File.WriteAllText(tokenFile, "  " + (token ?? Token) + "\r\n");
        var json = JsonSerializer.Serialize(new { port = port ?? Port, tokenFile, processId = Environment.ProcessId, version = "0.1.0" });
        File.WriteAllText(Path.Combine(Home, "api.json"), json);
    }

    public async Task StopAsync()
    {
        if (_app is null) return;
        await _app.StopAsync();
        await _app.DisposeAsync();
        _app = null;
    }

    public async ValueTask DisposeAsync() => await StopAsync();

    private async Task HandleAsync(HttpContext ctx)
    {
        var body = "";
        if (!ctx.WebSockets.IsWebSocketRequest)
        {
            using var reader = new StreamReader(ctx.Request.Body, Encoding.UTF8, leaveOpen: true);
            body = await reader.ReadToEndAsync();
        }
        var headers = ctx.Request.Headers.ToDictionary(h => h.Key, h => h.Value.ToString(), StringComparer.OrdinalIgnoreCase);
        var rec = new RecordedRequest(ctx.Request.Method, ctx.Request.Path.Value ?? "", ctx.Request.QueryString.Value ?? "", headers, body);
        Requests.Enqueue(rec);

        if (ctx.Request.Headers.ContainsKey("Origin"))
        {
            await Json(ctx, 403, """{"error":"Cross-origin requests are not allowed."}""");
            return;
        }
        var auth = ctx.Request.Headers.Authorization.ToString();
        if (auth != "Bearer " + Token)
        {
            await Json(ctx, 401, """{"error":"Missing or invalid API token."}""");
            return;
        }
        if (Handler is null)
        {
            await Json(ctx, 404, """{"error":"Not found."}""");
            return;
        }
        await Handler(ctx, rec);
    }

    public static async Task Json(HttpContext ctx, int status, string json)
    {
        ctx.Response.StatusCode = status;
        ctx.Response.ContentType = "application/json";
        await ctx.Response.WriteAsync(json);
    }

    /// <summary>A loopback port with nothing listening on it.</summary>
    public static int UnusedPort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    public static string NewHome() => Path.Combine(Path.GetTempPath(), "greeter-tests", Guid.NewGuid().ToString("N"));
}
