using Grpc.Net.Client;
using MagicOnion.Client;
using MagicOnion.Serialization;
using MessagePack;
using SharedNet.Api;

namespace Nordicandia.TestClient;

/// <summary>
/// Verifies the strict auth policy: email register/login succeeds, a wrong password is
/// rejected, and unverified device login is rejected. Run with
/// <c>Nordicandia.TestClient auth &lt;addr&gt;</c>.
/// </summary>
public static class AuthProbe
{
    public static async Task<int> RunAsync(string address)
    {
        var http = new HttpClientHandler();
        if (address.StartsWith("https", StringComparison.OrdinalIgnoreCase))
            http.ServerCertificateCustomValidationCallback = (m, c, ch, e) => true;
        var serializer = MessagePackMagicOnionSerializerProvider.Default
            .WithOptions(MessagePackSerializer.DefaultOptions.WithCompression(MessagePackCompression.Lz4BlockArray));

        using var channel = GrpcChannel.ForAddress(address, new GrpcChannelOptions { HttpHandler = http });
        var login = MagicOnionClient.Create<ILoginServiceApi>(channel, serializer);

        var email = $"auth-{Guid.NewGuid():N}@nord.local";
        var ok = await login.LoginWithEmailAsync(new LoginWithEmailRequest
        {
            Email = email, Password = TestAuth.Password, CreateAccount = true, ClientVersion = "1.9.3",
        });
        if (ok?.Session?.AuthToken == null) { Console.WriteLine("FAIL email register/login"); return 1; }
        Console.WriteLine("AUTH email register/login OK");

        var wrong = await login.LoginWithEmailAsync(new LoginWithEmailRequest
        {
            Email = email, Password = "not-the-password", ClientVersion = "1.9.3",
        });
        if (wrong?.Session?.AuthToken != null) { Console.WriteLine("FAIL wrong password was accepted"); return 1; }
        Console.WriteLine("AUTH wrong password rejected OK");

        var device = await login.LoginWithStandaloneDeviceIdAsync(new LoginWithStandaloneDeviceIdRequest
        {
            DeviceId = "auth-probe-" + Guid.NewGuid().ToString("N"),
        });
        if (device?.Session?.AuthToken != null)
        {
            Console.WriteLine("FAIL unverified device login was accepted (set NORD_AUTH_ALLOW_UNVERIFIED_PROVIDERS=0)");
            return 1;
        }
        Console.WriteLine("AUTH unverified device login rejected OK");
        return 0;
    }
}