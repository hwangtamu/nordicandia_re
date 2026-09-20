using System.Net.WebSockets;
using Game;
using Grpc.Net.Client;
using MagicOnion.Client;
using MagicOnion.Serialization;
using MessagePack;
using SharedNet.Api;
using SharedNet.Dto;
using SharedNet.Dto.Realtime;
using SharedNet.Dto.Realtime.Messages.Payloads;

namespace Nordicandia.TestClient;

/// <summary>
/// Connects the realtime WebSocket exactly like the Unity clients do (Bearer auth header +
/// <c>/ws?status=True&amp;characterId=...</c>) and exercises the request/response messages the
/// in-game client sends. Run with: <c>Nordicandia.TestClient ws &lt;grpc-address&gt; [steamId]</c>.
/// </summary>
public static class WebSocketProbe
{
    public static async Task<int> RunAsync(string grpcAddress, string steamId = "0ce37d053370b02147be1a1e8029d43a3c444efd")
    {
        var http = new HttpClientHandler();
        if (grpcAddress.StartsWith("https", StringComparison.OrdinalIgnoreCase))
            http.ServerCertificateCustomValidationCallback = (m, c, ch, e) => true;
        var auth = new AuthHeaderHandler(http);

        var serializerProvider = MessagePackMagicOnionSerializerProvider.Default
            .WithOptions(MessagePackSerializer.DefaultOptions.WithCompression(MessagePackCompression.Lz4BlockArray));

        using var channel = GrpcChannel.ForAddress(grpcAddress, new GrpcChannelOptions { HttpHandler = auth });
        var login = MagicOnionClient.Create<ILoginServiceApi>(channel, serializerProvider);
        var session = await TestAuth.EnsureAccountAsync(login, $"ws-{steamId}@nord.local");
        auth.Token = session.Session?.AuthToken;
        Console.WriteLine($"LOGIN  userId={session.User?.UserId} token={(session.Session?.AuthToken is { Length: > 8 } t ? t[..8] + "..." : "<none>")}");

        var characters = MagicOnionClient.Create<ICharacterServiceApi>(channel, serializerProvider);
        var list = await characters.GetCharacterList(new GetCharacterListRequest());
        var character = list.Characters?.FirstOrDefault();
        if (character == null)
        {
            var created = await characters.CreateCharacter(new CreateCharacterRequest
            {
                DisplayName = "Probe" + Random.Shared.Next(1000, 9999),
                CharacterClass = SharedNet.Constants.Game.CharacterClass.Warrior,
                CharacterRace = SharedNet.Constants.Game.CharacterRace.Human,
                CharacterGameMode = SharedNet.Constants.Game.GameMode.Normal,
                Data = new Game.SerializedCharacterData
                {
                    Header = new Game.SerializedCharacterData.SerializedHeader { Name = "Probe", Level = 1 },
                    Data = new Game.SerializedCharacterData.SerializedData
                    {
                        Attributes = new SerializedAttributes { Values = new(), MultiplicativeValues = new() },
                        Items = new SerializedItems { Items = new() },
                        CombatStats = new Game.SerializedCharacterData.SerializedCombatStats { HelheimAttempts = new() },
                    },
                },
            });
            character = created.Character;
            Console.WriteLine($"CREATED character {character?.CharacterId} name={character?.DisplayName}");
            character ??= (await characters.GetCharacterList(new GetCharacterListRequest())).Characters?.FirstOrDefault();
            if (character == null)
            {
                Console.WriteLine("No character on this account; create one in the game first.");
                return 2;
            }
        }
        var account = await login.GetUserAccountData(new GetUserAccountDataRequest());
        Console.WriteLine($"ACCOUNT linked={(account.LinkedAccounts?.LinkedAccounts?.Count ?? 0)} ["
            + string.Join(", ", (account.LinkedAccounts?.LinkedAccounts ?? new List<SharedNet.Dto.UserLinkedAccountDto>()).Select(a => $"{a.Platform}:{a.PlatformUserId}")) + "]");
        Console.WriteLine($"CHAR   id={character.CharacterId} name={character.DisplayName} level={character.Level}");

        // Entering must hand back the authoritative level/experience/world progression that
        // the server persists, because the client replaces its local copy with this response.
        var enter = await characters.EnterGameWithCharacter(new EnterGameWithCharacterRequest { CharacterId = character.CharacterId });
        var charAttrs = enter.Character?.Attributes?.Values;
        if (charAttrs != null && charAttrs.TryGetValue(SharedNet.Constants.Game.AttributeOrigin.Character, out var map))
        {
            double? Get(int id) => map.TryGetValue(id, out var v) ? v.ValueD : null;
            Console.WriteLine($"ENTER  attrs={map.Count} level={Get(2)} exp={Get(359)} worldTierUnlocked={Get(9)} str={Get(105)}");
        }

        // Public character card (leaderboard inspect panel / gear rendering).
        var social = MagicOnionClient.Create<ISocialServiceApi>(channel, serializerProvider);
        var inspection = await social.InspectCharacter(new InspectCharacterRequest
        {
            CharacterId = character.CharacterId, InspectTargetCharacterId = character.CharacterId,
        });
        var dto = inspection.InspectionData;
        Console.WriteLine($"INSPECT name={dto?.Name} level={dto?.Level} offense={dto?.Offense} equipped={dto?.EquippedItems?.Count ?? 0}");
        foreach (var item in dto?.EquippedItems ?? new List<Game.SerializedItem>())
            Console.WriteLine($"       {item.Slot,-10} {item.Name}");

        // Leaderboards + level persistence.
        var leaderboards = MagicOnionClient.Create<ILeaderboardServiceApi>(channel, serializerProvider);
        var relevant = await leaderboards.GetRelevantLeaderboards(new GetRelevantLeaderboardsAndTournamentsRequest());
        var all = relevant.LeaderboardList?.Leaderboards ?? new List<SharedNet.Dto.LeaderboardDto>();
        Console.WriteLine($"LB     definitions={all.Count}");
        var overall = all.FirstOrDefault(l => l.Name == "character_level_overall_normal");
        if (overall == null)
        {
            Console.WriteLine("FAIL  missing character_level_overall_normal");
            return 4;
        }
        var records = await leaderboards.ListLeaderboardRecords(new ListLeaderboardRecordsRequest { LeaderboardName = overall.Name, Limit = 10 });
        Console.WriteLine($"LB     {overall.Name} records={records.Records?.Records?.Count ?? 0}");
        foreach (var r in records.Records?.Records ?? new List<SharedNet.Dto.LeaderboardRecordDto>())
            Console.WriteLine($"       #{r.Rank} {r.OwnerDisplayName} score={r.Score} -> level={Math.Round(Math.Exp(r.Score / 1_000_000.0), 1)}");

        // The in-game "Your Rank" tab uses this call.
        var around = await leaderboards.ListLeaderboardRecordsAroundOwner(new ListLeaderboardRecordsAroundOwnerRequest
        {
            LeaderboardName = overall.Name, OwnerId = character.CharacterId, Limit = 10,
        });
        Console.WriteLine($"LB     around-owner records={around.Records?.Records?.Count ?? 0}");
        foreach (var r in around.Records?.Records ?? new List<SharedNet.Dto.LeaderboardRecordDto>())
            Console.WriteLine($"       #{r.Rank} {r.OwnerDisplayName} score={r.Score} -> level={Math.Round(Math.Exp(r.Score / 1_000_000.0), 1)}");

        var wsAddress = grpcAddress.StartsWith("https", StringComparison.OrdinalIgnoreCase)
            ? grpcAddress.Replace("https", "wss", StringComparison.OrdinalIgnoreCase)
            : grpcAddress.Replace("http", "ws", StringComparison.OrdinalIgnoreCase);
        var uri = new Uri($"{wsAddress.TrimEnd('/')}/ws?status=True&characterId={character.CharacterId}");

        using var socket = new ClientWebSocket();
        socket.Options.SetRequestHeader("Authorization", "Bearer " + session.Session.AuthToken);
        socket.Options.RemoteCertificateValidationCallback = (m, c, ch, e) => true;
        Console.WriteLine($"WS     connecting {uri}");
        await socket.ConnectAsync(uri, CancellationToken.None);
        Console.WriteLine($"WS     connected ({socket.State})");

        // 1. Experience/currency sync (the periodic idle-progress message).
        var metadata = new Envelope
        {
            RequestId = 1,
            Message = new UpdateCharacterMetadataMessage
            {
                Metadata = new CharacterMetadataPayload
                {
                    BaseExperienceGained = 123.5,
                    NumMonsterKills = 2,
                    NumItemsLooted = 1,
                    LastSeenSilver = 777,
                    LastSeenOpals = 42,
                    Offense = 10,
                    Defense = 5,
                    Recovery = 3,
                    TimeSentUtc = DateTime.UtcNow,
                },
            },
        };
        var metadataReply = await SendAsync(socket, metadata);
        Print(metadataReply);
        if (metadataReply.Message is not UpdateCharacterMetadataResponse um || um.NewExperience < 123.5)
        {
            Console.WriteLine("FAIL  UpdateCharacterMetadataResponse");
            return 3;
        }

        // 2. Channel join + chat send.
        var pushes = new List<Envelope>();
        var join = await SendAsync(socket, new Envelope
        {
            RequestId = 2,
            Message = new ChannelJoinMessage { ChannelJoin = new ChannelJoinPayload { Target = "Global", Type = SharedNet.Constants.Realtime.ChannelJoinType.Room } },
        }, pushes);
        Print(join);

        var chat = await SendAsync(socket, new Envelope
        {
            RequestId = 3,
            Message = new ChannelMessageSendMessage { ChannelId = "Global", Content = "hello from the probe" },
        }, pushes);
        Print(chat);

        // The relay must fan the sender's own message back out (RequestId 0).
        var relayed = pushes.Select(p => p.Message).OfType<ChannelMessageMessage>()
            .FirstOrDefault(m => m.ChannelMessage?.Content == "hello from the probe");
        if (relayed == null)
        {
            Console.WriteLine("FAIL  chat was not relayed back to the sender");
            return 6;
        }
        Console.WriteLine($"CHAT   relayed from {relayed.ChannelMessage.SenderUserDisplayName}: {relayed.ChannelMessage.Content}");

        // 3. Quest event round-trip.
        var quest = await SendAsync(socket, new Envelope { RequestId = 4, Message = new ClientQuestEventMessage() }, pushes);
        Print(quest);

        // 4. A brand-new world level must produce a server-pushed first-to-reach announcement.
        var gameEvents = MagicOnionClient.Create<ICharacterGameEventServiceApi>(channel, serializerProvider);
        var tier = 200 + Random.Shared.Next(0, 900);
        await gameEvents.OnDungeonRunStarted(new CharacterDungeonRunStartedRequest
        {
            CharacterId = character.CharacterId, WorldTier = tier, WorldWaypoint = 900 + Random.Shared.Next(0, 90),
        });
        var announcement = await WaitForPushAsync(socket, m => m.ChannelMessage?.Content?.Contains("is the first to reach") == true, TimeSpan.FromSeconds(5));
        if (announcement == null)
        {
            Console.WriteLine("FAIL  first-to-reach announcement was not pushed");
            return 7;
        }
        Console.WriteLine($"ANNOUNCE type={announcement.ChannelMessage.SenderRole} content={announcement.ChannelMessage.Content}");

        // 5. Loot filter persistence: push a named filter, then re-enter and read it back.
        var filterId = Guid.NewGuid();
        var filter = new SerializedLootFilter { Id = filterId, Name = "ProbeFilter", IsShared = false };
        await characters.UpdateItemFilter(new UpdateItemFilterRequest
        {
            CharacterId = character.CharacterId,
            CharacterFilters = new SerializedCharacterData.SerializedCharacterLootFilters
            {
                ActiveFilterId = filterId,
                Filters = new SerializedLootFilters { Filters = new List<SerializedLootFilter> { filter } },
            },
            UserFilters = new SerializedLootFilters { Filters = new List<SerializedLootFilter> { filter } },
        });
        var reentered = await characters.EnterGameWithCharacter(new EnterGameWithCharacterRequest { CharacterId = character.CharacterId });
        var storedFilter = reentered.Character?.LootFilters?.Filters?.Filters?.FirstOrDefault();
        var account0 = await login.GetUserAccountData(new GetUserAccountDataRequest());
        if (storedFilter?.Name != "ProbeFilter" || account0.AccountData?.LootFilters?.Filters?.FirstOrDefault()?.Name != "ProbeFilter")
        {
            Console.WriteLine($"FAIL  loot filter not persisted (char='{storedFilter?.Name}' account='{account0.AccountData?.LootFilters?.Filters?.FirstOrDefault()?.Name}')");
            return 8;
        }
        Console.WriteLine($"FILTER persisted char+account id={storedFilter.Id} name={storedFilter.Name}");

        // 6. Aesir offering: grant opals, make an offering, expect the opal sink + typeCode=5 push.
        var currency = MagicOnionClient.Create<IVirtualCurrencyServiceApi>(channel, serializerProvider);
        await currency.GainVirtualCurrency(new GainVirtualCurrencyRequest { CharacterId = character.CharacterId, Amount = 100 });
        var offeringApi = MagicOnionClient.Create<IOfferingServiceApi>(channel, serializerProvider);
        var offering = await offeringApi.MakeOffering(new MakeOfferingRequest
        {
            CharacterId = character.CharacterId, IsAnonymous = false,
            OfferingType = AesirOfferingTypes.Odin, OfferingSize = AesirOfferingSizes.Small, OfferedOpals = 10,
        });
        var blessings = await offeringApi.GetCurrentBlessings(new GetCurrentBlessingsRequest { CharacterId = character.CharacterId });
        var offered = await WaitForPushAsync(socket, m => m.ChannelMessage?.Content?.Contains("has made an offering") == true, TimeSpan.FromSeconds(5));
        if (offered == null || blessings.BlessingOdin == null)
        {
            Console.WriteLine($"FAIL  offering push={offered?.ChannelMessage?.Content} blessing={blessings.BlessingOdin?.DefinitionIntegerId}");
            return 9;
        }
        Console.WriteLine($"OFFERING opals={offering.NewOpals} buffId={blessings.BlessingOdin.DefinitionIntegerId} push={offered.ChannelMessage.Content}");

        // 7. A server-side spend must survive the next periodic sync (no refund).
        await SendAsync(socket, new Envelope
        {
            RequestId = 5,
            Message = new UpdateCharacterMetadataMessage
            {
                Metadata = new CharacterMetadataPayload { LastSeenOpals = offering.NewOpals, LastSeenSilver = 0 },
            },
        }, pushes);
        var afterSync = await currency.GainVirtualCurrency(new GainVirtualCurrencyRequest { CharacterId = character.CharacterId, Amount = 1 });
        if (afterSync.NewOpals != offering.NewOpals + 1)
        {
            Console.WriteLine($"FAIL  opals refunded by sync: offered={offering.NewOpals} afterSync={afterSync.NewOpals}");
            return 10;
        }
        Console.WriteLine($"SPEND  opals persisted after sync={afterSync.NewOpals} (offered {offering.NewOpals} + 1)");

        await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "done", CancellationToken.None);
        Console.WriteLine("WS OK");
        return 0;
    }

    private static async Task<Envelope> SendAsync(ClientWebSocket socket, Envelope envelope, List<Envelope> pushes = null)
    {
        var bytes = MessagePackSerializer.Serialize(envelope);
        await socket.SendAsync(bytes, WebSocketMessageType.Binary, true, CancellationToken.None);

        // Interleaved server pushes (RequestId 0) may arrive before the reply; stash them
        // and keep waiting for the frame whose RequestId matches the request.
        while (true)
        {
            var reply = await ReceiveAsync(socket);
            if (reply.RequestId == envelope.RequestId) return reply;
            if (reply.RequestId == 0) { pushes?.Add(reply); continue; }
        }
    }

    private static async Task<ChannelMessageMessage> WaitForPushAsync(ClientWebSocket socket,
        Func<ChannelMessageMessage, bool> predicate, TimeSpan timeout)
    {
        using var cts = new CancellationTokenSource(timeout);
        try
        {
            while (true)
            {
                var envelope = await ReceiveAsync(socket, cts.Token);
                if (envelope.Message is ChannelMessageMessage push && predicate(push)) return push;
            }
        }
        catch (OperationCanceledException) { return null; }
    }

    private static async Task<Envelope> ReceiveAsync(ClientWebSocket socket, CancellationToken cancellationToken = default)
    {
        var buffer = new byte[64 * 1024];
        using var ms = new MemoryStream();
        WebSocketReceiveResult result;
        do
        {
            result = await socket.ReceiveAsync(buffer, cancellationToken);
            ms.Write(buffer, 0, result.Count);
        } while (!result.EndOfMessage);

        return MessagePackSerializer.Deserialize<Envelope>(ms.ToArray());
    }

    private static void Print(Envelope envelope)
        => Console.WriteLine($"WS     request={envelope.RequestId} reply={envelope.Message?.GetType().Name}");

    private sealed class AuthHeaderHandler : DelegatingHandler
    {
        public string Token { get; set; }
        public AuthHeaderHandler(HttpMessageHandler inner) : base(inner) { }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (!string.IsNullOrEmpty(Token))
                request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", Token);
            return base.SendAsync(request, cancellationToken);
        }
    }
}
