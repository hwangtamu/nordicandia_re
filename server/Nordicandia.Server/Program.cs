using MagicOnion.Server;
using Microsoft.AspNetCore.Server.Kestrel.Core;

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddGrpc();
builder.Services.AddMagicOnion();

var certPfx = Environment.GetEnvironmentVariable("NORD_CERT_PFX");
var certPwd = Environment.GetEnvironmentVariable("NORD_CERT_PWD");
var httpsPort = int.TryParse(Environment.GetEnvironmentVariable("NORD_HTTPS_PORT"), out var hp) ? hp : 443;
var h2cPort = int.TryParse(Environment.GetEnvironmentVariable("NORD_H2C_PORT"), out var hp2) ? hp2 : 50051;

builder.WebHost.ConfigureKestrel(o =>
{
    // plaintext HTTP/2 (dev / test client)
    o.ListenLocalhost(h2cPort, l => l.Protocols = HttpProtocols.Http2);

    // TLS + HTTP/2 (real APK). Cert must be valid for the patched hostname.
    if (!string.IsNullOrEmpty(certPfx) && File.Exists(certPfx))
        o.ListenAnyIP(httpsPort, l =>
        {
            l.Protocols = HttpProtocols.Http2;
            l.UseHttps(certPfx, certPwd);
        });
});

var app = builder.Build();
app.MapMagicOnionService();
app.MapGet("/", () => "Nordicandia private server (MagicOnion 5.1.8)");
app.Run();
