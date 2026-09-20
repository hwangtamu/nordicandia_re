using MagicOnion.Server;
using MagicOnion.Serialization;
using MessagePack;
using Microsoft.AspNetCore.Server.Kestrel.Core;

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddGrpc();
builder.Services.AddMagicOnion(options =>
{
    // The client (Unity/BestHTTP build) serializes MessagePack with LZ4BlockArray
    // compression, so the server must use the same options on the wire.
    options.MessageSerializer = MessagePackMagicOnionSerializerProvider.Default
        .WithOptions(MessagePackSerializer.DefaultOptions.WithCompression(MessagePackCompression.Lz4BlockArray));
});

var certPfx = Environment.GetEnvironmentVariable("NORD_CERT_PFX");
var certPwd = Environment.GetEnvironmentVariable("NORD_CERT_PWD");
var httpsPort = int.TryParse(Environment.GetEnvironmentVariable("NORD_HTTPS_PORT"), out var hp) ? hp : 443;
var h2cPort = int.TryParse(Environment.GetEnvironmentVariable("NORD_H2C_PORT"), out var hp2) ? hp2 : 50051;
var cloudRun = int.TryParse(Environment.GetEnvironmentVariable("PORT"), out var cloudPort);
var listenAny = Environment.GetEnvironmentVariable("NORD_LISTEN_ANY") == "1";

if (!cloudRun && listenAny && (string.IsNullOrEmpty(certPfx) || !File.Exists(certPfx)))
    throw new InvalidOperationException("NORD_LISTEN_ANY requires an existing NORD_CERT_PFX certificate.");

builder.WebHost.ConfigureKestrel(o =>
{
    if (cloudRun)
    {
        // Cloud Run terminates TLS and forwards native gRPC as clear-text HTTP/2.
        // The Nordicandia client also opens /ws with HTTP/2 extended CONNECT.
        o.ListenAnyIP(cloudPort, l => l.Protocols = HttpProtocols.Http2);
        return;
    }

    // plaintext HTTP/2 (dev / test client)
    o.ListenLocalhost(h2cPort, l => l.Protocols = HttpProtocols.Http2);

    // TLS + HTTP/2 (real APK). Cert must be valid for the patched hostname.
    if (!string.IsNullOrEmpty(certPfx) && File.Exists(certPfx))
    {
        void ConfigureTls(ListenOptions l)
        {
            l.Protocols = HttpProtocols.Http1AndHttp2;
            l.UseHttps(certPfx, certPwd);
        }
        if (listenAny) o.ListenAnyIP(httpsPort, ConfigureTls);
        else o.ListenLocalhost(httpsPort, ConfigureTls);
    }
});

var app = builder.Build();

// Migrate snapshots written before the level curve was enforced.
Nordicandia.Server.State.GameStore.Instance.RecalculateLevels();

if (Environment.GetEnvironmentVariable("NORD_DUMP_BODY") == "1")
{
    app.Use(async (ctx, next) =>
    {
        if (ctx.Request.Path.StartsWithSegments("/ILoginServiceApi"))
        {
            ctx.Request.EnableBuffering();
            using var ms = new MemoryStream();
            await ctx.Request.Body.CopyToAsync(ms);
            ctx.Request.Body.Position = 0;
            Console.WriteLine($"[BODY] {ctx.Request.Path} {ms.Length}B {Convert.ToHexString(ms.ToArray())}");
        }
        await next();
    });
}

app.MapMagicOnionService();
app.MapGet("/", () => "Nordicandia private server (MagicOnion 5.1.8)");
app.MapGet("/healthz", () => Results.Ok(new { status = "ok" }));

// Realtime gateway. The Unity clients open a raw WebSocket at /ws (HTTP/1.1 upgrade,
// or extended CONNECT over the existing HTTP/2 + TLS session).
app.UseWebSockets(new WebSocketOptions { KeepAliveInterval = TimeSpan.FromSeconds(30) });
app.Map("/ws", branch => branch.Run(Nordicandia.Server.Realtime.RealtimeGateway.HandleAsync));

app.Run();
