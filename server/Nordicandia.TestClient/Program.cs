using System.Net.Http;
using Grpc.Net.Client;
using MagicOnion.Client;
using SharedNet.Api;

AppContext.SetSwitch("System.Net.Http.SocketsHttpHandler.Http2UnencryptedSupport", true);
var address = args.Length > 0 ? args[0] : "http://localhost:50051";

// The real client does not validate the server certificate; mirror that for HTTPS tests.
var handler = new HttpClientHandler();
if (address.StartsWith("https", StringComparison.OrdinalIgnoreCase))
    handler.ServerCertificateCustomValidationCallback = (m, c, ch, e) => true;

Console.WriteLine($"Connecting to {address} ...");
using var channel = GrpcChannel.ForAddress(address, new GrpcChannelOptions { HttpHandler = handler });
var client = MagicOnionClient.Create<ILoginServiceApi>(channel);

var req = new LoginWithStandaloneDeviceIdRequest { DeviceId = "test-device-1", ClientVersion = "1.9.3" };
var res = await client.LoginWithStandaloneDeviceIdAsync(req);
Console.WriteLine("LOGIN OK");
Console.WriteLine($"  UserId      : {res.User?.UserId}");
Console.WriteLine($"  DisplayName : {res.User?.DisplayName}");
Console.WriteLine($"  AuthToken   : {res.Session?.AuthToken}");
Console.WriteLine($"  ServerTime  : {res.ServerTime}");

var characters = MagicOnionClient.Create<ICharacterServiceApi>(channel);
var list = await characters.GetCharacterList(new GetCharacterListRequest());
Console.WriteLine($"CHARACTERS: {list.Characters?.Count ?? 0}");

var lb = MagicOnionClient.Create<ILeaderboardServiceApi>(channel);
await lb.GetRelevantLeaderboards(new GetRelevantLeaderboardsAndTournamentsRequest());
Console.WriteLine("LEADERBOARDS OK");
