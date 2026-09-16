using Grpc.Net.Client;
using MagicOnion.Client;
using MagicOnion.Serialization;
using MessagePack;
using SharedNet.Api;
using Game;

namespace Nordicandia.TestClient;

/// <summary>
/// Verifies the season flow: <c>GetSeasonInfo</c> must publish an active current season, and
/// <c>CreateCharacter</c> must accept <c>GameMode.Season</c>. Run with
/// <c>Nordicandia.TestClient season &lt;addr&gt;</c>.
/// </summary>
public static class SeasonProbe
{
    public static async Task<int> RunAsync(string address)
    {
        var http = new HttpClientHandler();
        if (address.StartsWith("https", StringComparison.OrdinalIgnoreCase))
            http.ServerCertificateCustomValidationCallback = (m, c, ch, e) => true;

        var serializerProvider = MessagePackMagicOnionSerializerProvider.Default
            .WithOptions(MessagePackSerializer.DefaultOptions.WithCompression(MessagePackCompression.Lz4BlockArray));

        using var channel = GrpcChannel.ForAddress(address, new GrpcChannelOptions { HttpHandler = http });
        var login = MagicOnionClient.Create<ILoginServiceApi>(channel, serializerProvider);
        var account = "season-" + Guid.NewGuid().ToString("N")[..8];
        var session = await login.LoginWithStandaloneDeviceIdAsync(new LoginWithStandaloneDeviceIdRequest { DeviceId = account });
        var token = session.Session.AuthToken;
        using var authed = GrpcChannel.ForAddress(address, new GrpcChannelOptions { HttpHandler = new HttpHeaderHandler(http, token) });

        var gameMode = MagicOnionClient.Create<IGameModeServiceApi>(authed, serializerProvider);
        var info = await gameMode.GetSeasonInfo(new GetSeasonInfoRequest());
        var now = DateTime.UtcNow;
        Console.WriteLine($"SEASON current={info.CurrentSeasonName} {info.CurrentSeasonStart:u}..{info.CurrentSeasonEnd:u}");
        Console.WriteLine($"       prev={info.PrevSeasonName} next={info.NextSeasonName} {info.NextSeasonStart:u}..{info.NextSeasonEnd:u}");
        var active = info.CurrentSeasonStart is { } start && info.CurrentSeasonEnd is { } end
                     && start.AddHours(-1) <= now && now < end;
        Console.WriteLine($"SEASON active={active}");

        var characters = MagicOnionClient.Create<ICharacterServiceApi>(authed, serializerProvider);
        var create = await characters.CreateCharacter(new CreateCharacterRequest
        {
            DisplayName = "seasontest",
            CharacterClass = SharedNet.Constants.Game.CharacterClass.Warrior,
            CharacterRace = SharedNet.Constants.Game.CharacterRace.Human,
            CharacterGameMode = SharedNet.Constants.Game.GameMode.Season,
            Data = new SerializedCharacterData
            {
                Header = new SerializedCharacterData.SerializedHeader { Name = "seasontest", Level = 1 },
                Data = new SerializedCharacterData.SerializedData
                {
                    Attributes = new SerializedAttributes { Values = new(), MultiplicativeValues = new() },
                    CombatStats = new SerializedCharacterData.SerializedCombatStats { HelheimAttempts = new() },
                },
            },
        });
        Console.WriteLine($"CREATE gameMode={create.Character.GameMode} id={create.Character.CharacterId}");

        // Entering must inject the SeasonBuff icon and the +100% experience attribute.
        var enter = await characters.EnterGameWithCharacter(new EnterGameWithCharacterRequest { CharacterId = create.Character.CharacterId });
        var seasonBuff = enter.Character.Buffs?.Buffs?.FirstOrDefault(b => b.DefinitionIntegerId == 258);
        var attrs = enter.Character.Attributes.Values[SharedNet.Constants.Game.AttributeOrigin.Character];
        var expBonus = attrs.TryGetValue(361, out var v) ? v.ValueD : double.NaN;
        Console.WriteLine($"BUFF definition={seasonBuff?.DefinitionIntegerId} expBonus={expBonus}");

        var list = await characters.GetCharacterList(new GetCharacterListRequest());
        Console.WriteLine($"LIST {string.Join(", ", list.Characters.Select(c => $"{c.DisplayName}:{c.GameMode}"))}");

        // Clean up the throwaway account's character so repeated runs don't fill the slot.
        await characters.DeleteCharacter(new DeleteCharacterRequest { CharacterId = create.Character.CharacterId });

        var ok = active && create.Character.GameMode == SharedNet.Constants.Game.GameMode.Season && expBonus >= 1.0 && seasonBuff != null;
        Console.WriteLine(ok ? "SEASON OK" : "SEASON FAIL");
        return ok ? 0 : 6;
    }

    private sealed class HttpHeaderHandler : DelegatingHandler
    {
        private readonly string token;
        public HttpHeaderHandler(HttpMessageHandler inner, string token) : base(inner) => this.token = token;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);
            return base.SendAsync(request, cancellationToken);
        }
    }
}
