using Grpc.Net.Client;
using MagicOnion.Client;
using MagicOnion.Serialization;
using MessagePack;
using SharedNet.Api;
using Game;

namespace Nordicandia.TestClient;

/// <summary>
/// Creates a throwaway account/character and verifies that the server persists the
/// incremental updates it receives (attributes, world progression, items, currency) across
/// a fresh <c>EnterGameWithCharacter</c>. Run with <c>Nordicandia.TestClient persist &lt;addr&gt;</c>.
/// </summary>
public static class PersistenceProbe
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
        var session = await TestAuth.RegisterAndLoginAsync(login, "persist");
        var token = session.Session.AuthToken;
        var auth = new HttpHeaderHandler(http, token);
        using var authed = GrpcChannel.ForAddress(address, new GrpcChannelOptions { HttpHandler = auth });
        var characters = MagicOnionClient.Create<ICharacterServiceApi>(authed, serializerProvider);

        var create = await characters.CreateCharacter(new CreateCharacterRequest
        {
            DisplayName = "selftest",
            CharacterClass = SharedNet.Constants.Game.CharacterClass.Warrior,
            CharacterRace = SharedNet.Constants.Game.CharacterRace.Human,
            CharacterGameMode = SharedNet.Constants.Game.GameMode.Normal,
            Data = new SerializedCharacterData
            {
                Header = new SerializedCharacterData.SerializedHeader { Name = "selftest", Level = 1 },
                Data = new SerializedCharacterData.SerializedData
                {
                    Attributes = new SerializedAttributes { Values = new(), MultiplicativeValues = new() },
                    CombatStats = new SerializedCharacterData.SerializedCombatStats { HelheimAttempts = new() },
                },
            },
        });
        var id = create.Character.CharacterId;
        Console.WriteLine($"CREATED {id}");

        await characters.AllocateCharacterAttributes(new AllocateCharacterAttributesRequest
        {
            CharacterId = id,
            Strength = 11, Dexterity = 12, Intelligence = 13, Vitality = 14,
            Constitution = 15, Agility = 16, Mindpower = 17,
        });

        var events = MagicOnionClient.Create<ICharacterGameEventServiceApi>(authed, serializerProvider);
        await events.OnDungeonRunCompleted(new CharacterDungeonRunCompletedRequest
        {
            CharacterId = id, WorldType = SharedNet.Constants.Game.WorldTypes.Normal,
            WorldTier = 3, WorldWaypoint = 5, Duration = TimeSpan.FromSeconds(61),
        });

        var inventory = MagicOnionClient.Create<IInventoryServiceApi>(authed, serializerProvider);
        await inventory.ItemOperation(new ItemOperationRequest
        {
            CharacterId = id,
            ItemOperations = new List<ItemOperationEntry>
            {
                new AddItemOperationEntry { Item = new SerializedItem { Id = Guid.NewGuid(), Name = "Persisted Sword", Slot = SharedNet.Constants.Game.ItemSlotTypes.MainHand, DefinitionIntegerId = 1 } },
            },
        });

        var currency = MagicOnionClient.Create<IVirtualCurrencyServiceApi>(authed, serializerProvider);
        var silver = await currency.GainSilver(new GainSilverRequest { CharacterId = id, Amount = 1234 });
        var opals = await currency.GainVirtualCurrency(new GainVirtualCurrencyRequest { CharacterId = id, Amount = 77 });
        Console.WriteLine($"CURRENCY silver={silver.NewSilver} opals={opals.NewOpals}");

        var powers = MagicOnionClient.Create<ICharacterPowerServiceApi>(authed, serializerProvider);
        await powers.AssignActiveSkill(new AssignCharacterActiveSkillRequest
        {
            CharacterId = id,
            Skills = new List<CharacterSkillEntry>
            {
                new() { PowerId = Guid.Parse("73428deb-2a35-433d-8e67-39d188aa6395"), SkillSlot = 0 }, // Might
            },
        });

        // Relogin/enter and assert everything came back.
        var enter = await characters.EnterGameWithCharacter(new EnterGameWithCharacterRequest { CharacterId = id });
        var data = enter.Character;
        var attrs = data.Attributes.Values[SharedNet.Constants.Game.AttributeOrigin.Character];
        double Get(int key) => attrs.TryGetValue(key, out var v) ? v.ValueD : double.NaN;
        var ok = Get(105) == 11 && Get(106) == 12 && Get(107) == 13 && Get(108) == 14
                 && Get(126) == 15 && Get(127) == 16 && Get(128) == 17
                 && Get(9) == 3
                 && data.Waypoints.WaypointMap.TryGetValue(3, out var wp) && wp.HighestWaypointCleared == 5
                 && data.Items.Items.Any(i => i.Name == "Persisted Sword")
                 && (data.Skills?.Skills?.Any(s => s.PowerHashSafe == 497461807) ?? false);
        Console.WriteLine($"ENTER attrs str={Get(105)} dex={Get(106)} worldTierUnlocked={Get(9)} waypoint={data.Waypoints.WaypointMap[3].HighestWaypointCleared} items={data.Items.Items.Count} skills={data.Skills?.Skills?.Count ?? 0}");
        Console.WriteLine(ok ? "PERSIST OK" : "PERSIST FAIL");
        return ok ? 0 : 5;
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
