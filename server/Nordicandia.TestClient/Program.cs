using System.Net.Http;
using Grpc.Net.Client;
using MagicOnion.Client;
using MagicOnion.Serialization;
using MessagePack;
using SharedNet.Api;

AppContext.SetSwitch("System.Net.Http.SocketsHttpHandler.Http2UnencryptedSupport", true);

if (args.Length >= 1 && args[0] == "ws")
{
    var grpc = args.Length >= 2 ? args[1] : "https://localhost";
    var steam = args.Length >= 3 ? args[2] : "0ce37d053370b02147be1a1e8029d43a3c444efd";
    Environment.Exit(await Nordicandia.TestClient.WebSocketProbe.RunAsync(grpc, steam));
}

if (args.Length >= 2 && args[0] == "dump")
    Environment.Exit(Nordicandia.TestClient.SaveDump.Run(args[1]));

if (args.Length >= 2 && args[0] == "roundtrip")
    Environment.Exit(Nordicandia.TestClient.RoundTrip.Run(args[1]));

if (args.Length >= 2 && args[0] == "persist")
    Environment.Exit(await Nordicandia.TestClient.PersistenceProbe.RunAsync(args[1]));

if (args.Length >= 2 && args[0] == "season")
    Environment.Exit(await Nordicandia.TestClient.SeasonProbe.RunAsync(args[1]));

if (args.Length >= 2 && args[0] == "merchant")
    Environment.Exit(await Nordicandia.TestClient.MerchantProbe.RunAsync(args[1]));

var address = args.Length > 0 ? args[0] : "http://localhost:50051";

// The real client does not validate the server certificate; mirror that for HTTPS tests.
var handler = new HttpClientHandler();
if (address.StartsWith("https", StringComparison.OrdinalIgnoreCase))
    handler.ServerCertificateCustomValidationCallback = (m, c, ch, e) => true;

// Match the game client's wire format (MessagePack + LZ4BlockArray compression).
var serializerProvider = MessagePackMagicOnionSerializerProvider.Default
    .WithOptions(MessagePackSerializer.DefaultOptions.WithCompression(MessagePackCompression.Lz4BlockArray));

Console.WriteLine($"Connecting to {address} ...");
using var channel = GrpcChannel.ForAddress(address, new GrpcChannelOptions { HttpHandler = handler });
var client = MagicOnionClient.Create<ILoginServiceApi>(channel, serializerProvider);

var req = new LoginWithStandaloneDeviceIdRequest { DeviceId = "test-device-1", ClientVersion = "1.9.3" };
var res = await client.LoginWithStandaloneDeviceIdAsync(req);
Console.WriteLine("LOGIN OK");
Console.WriteLine($"  UserId      : {res.User?.UserId}");
Console.WriteLine($"  DisplayName : {res.User?.DisplayName}");
Console.WriteLine($"  AuthToken   : {res.Session?.AuthToken}");
Console.WriteLine($"  ServerTime  : {res.ServerTime}");

var characters = MagicOnionClient.Create<ICharacterServiceApi>(channel, serializerProvider);
var list = await characters.GetCharacterList(new GetCharacterListRequest());
Console.WriteLine($"CHARACTERS: {list.Characters?.Count ?? 0}");

var lb = MagicOnionClient.Create<ILeaderboardServiceApi>(channel, serializerProvider);
await lb.GetRelevantLeaderboards(new GetRelevantLeaderboardsAndTournamentsRequest());
Console.WriteLine("LEADERBOARDS OK");
