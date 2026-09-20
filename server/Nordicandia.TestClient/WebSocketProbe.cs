using System.Net.WebSockets;
using System.Text.Json;
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

        // 2. Channel join + chat send. Use the locale-specific global room the real client
        // joins ("Global_en") so the announcement routing below is exercised like production.
        var pushes = new List<Envelope>();
        var join = await SendAsync(socket, new Envelope
        {
            RequestId = 2,
            Message = new ChannelJoinMessage { ChannelJoin = new ChannelJoinPayload { Target = "Global_en", Type = SharedNet.Constants.Realtime.ChannelJoinType.Room } },
        }, pushes);
        Print(join);
        var joinedChannelId = (join.Message as ChannelJoinResponse)?.Channel?.Id;
        if (string.IsNullOrEmpty(joinedChannelId))
        {
            Console.WriteLine($"FAIL  channel join returned {join.Message?.GetType().Name ?? "<null>"}");
            return 6;
        }

        // Deliberately long enough that the client's LZ4BlockArray options actually compress
        // the frame (the small fixext1 header the server used to misdetect as uncompressed).
        var chatContent = "hello from the probe " + new string('x', 200);
        var chat = await SendAsync(socket, new Envelope
        {
            RequestId = 3,
            Message = new ChannelMessageSendMessage { ChannelId = joinedChannelId, Content = chatContent },
        }, pushes);
        Print(chat);
        if (chat.Message is not ChannelMessageAckMessage)
        {
            Console.WriteLine($"FAIL  chat ack was {chat.Message?.GetType().Name ?? "<null>"}");
            return 6;
        }

        // The relay must fan the sender's own message back out (RequestId 0).
        var relayed = pushes.Select(p => p.Message).OfType<ChannelMessageMessage>()
            .FirstOrDefault(m => m.ChannelMessage?.Content == chatContent);
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
        // The client files chat by RoomName, so the notice must be addressed to the room the
        // client joined, not the hardcoded "Global".
        if (announcement.ChannelMessage.RoomName != "Global_en")
        {
            Console.WriteLine($"FAIL  announcement filed under room '{announcement.ChannelMessage.RoomName}', expected 'Global_en'");
            return 7;
        }
        Console.WriteLine($"ANNOUNCE room={announcement.ChannelMessage.RoomName} type={announcement.ChannelMessage.SenderRole} content={announcement.ChannelMessage.Content}");

        // 5. Loot filter persistence: push a filter with advanced per-item-type visibility,
        // then re-enter and read every field back.
        var filterId = Guid.NewGuid();
        var filter = new SerializedLootFilter
        {
            Id = filterId,
            Name = "ProbeFilter",
            IsShared = false,
            MainFilter = new SerializedLootFilterEntry
            {
                Blacklist = false,
                FilterEnabled = true,
                LootFilterItemTypeIntegerId = 3,
                RarityFilterEnabled = true,
                MinRarityInclusive = SharedNet.Constants.Game.Rarity.A,
                AffixesFilterEnabled = true,
                MinNumAffixesInclusive = 2,
                IncludedAffixIntegerIds = new List<int> { 11, 22 },
                ExcludedAffixIntegerIds = new List<int> { 33 },
                DurabilityFilterEnabled = true,
                MinDurability = 50,
            },
            FilterOverrides = new List<SerializedLootFilterEntry>
            {
                new()
                {
                    Blacklist = true, FilterEnabled = true, LootFilterItemTypeIntegerId = 7,
                    RarityFilterEnabled = true, MinRarityInclusive = SharedNet.Constants.Game.Rarity.SS,
                },
            },
        };
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
        var accountFilter = account0.AccountData?.LootFilters?.Filters?.FirstOrDefault();
        var main = storedFilter?.MainFilter;
        if (storedFilter?.Name != "ProbeFilter" || accountFilter?.Name != "ProbeFilter"
            || main?.LootFilterItemTypeIntegerId != 3 || main.MinRarityInclusive != SharedNet.Constants.Game.Rarity.A
            || main.MinDurability != 50 || storedFilter.FilterOverrides?.Count != 1)
        {
            Console.WriteLine($"FAIL  loot filter not persisted (char='{storedFilter?.Name}' type={main?.LootFilterItemTypeIntegerId} rarity={main?.MinRarityInclusive} dur={main?.MinDurability} overrides={storedFilter?.FilterOverrides?.Count})");
            return 8;
        }
        Console.WriteLine($"FILTER persisted char+account type={main.LootFilterItemTypeIntegerId} rarity={main.MinRarityInclusive} dur={main.MinDurability} overrides={storedFilter.FilterOverrides.Count}");

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

        // 8. Skills must merge by slot: assigning one slot must not erase the others.
        var powerIds = LoadPowerIds(3);
        var powerApi = MagicOnionClient.Create<ICharacterPowerServiceApi>(channel, serializerProvider);
        for (var i = 0; i < powerIds.Count; i++)
            await powerApi.AssignActiveSkill(new AssignCharacterActiveSkillRequest
            {
                CharacterId = character.CharacterId,
                Skills = new List<CharacterSkillEntry> { new() { PowerId = powerIds[i], SkillSlot = i } },
            });
        var afterSkills = await characters.EnterGameWithCharacter(new EnterGameWithCharacterRequest { CharacterId = character.CharacterId });
        var active = afterSkills.Character?.Skills?.Skills?.Where(s => s != null && s.Slot == SharedNet.Constants.Game.PowerSlotTypes.ActiveSkill).ToList() ?? new();
        await powerApi.AssignActiveSkill(new AssignCharacterActiveSkillRequest
        {
            CharacterId = character.CharacterId,
            Skills = new List<CharacterSkillEntry> { new() { PowerId = Guid.Empty, SkillSlot = 1 } },
        });
        var afterClear = await characters.EnterGameWithCharacter(new EnterGameWithCharacterRequest { CharacterId = character.CharacterId });
        var activeAfterClear = afterClear.Character?.Skills?.Skills?.Count(s => s != null && s.Slot == SharedNet.Constants.Game.PowerSlotTypes.ActiveSkill) ?? 0;
        if (powerIds.Count < 3 || active.Count != powerIds.Count || activeAfterClear != powerIds.Count - 1)
        {
            Console.WriteLine($"FAIL  skills powers={powerIds.Count} persisted={active.Count} afterClear={activeAfterClear}");
            return 13;
        }
        Console.WriteLine($"SKILLS merged persisted={active.Count} afterClear={activeAfterClear}");

        // 9. Pets: unlock with opals, select, die, and persist across a re-enter.
        var opalsBefore = afterSync.NewOpals;
        var petUnlock = await characters.UnlockPet(new UnlockPetRequest { CharacterId = character.CharacterId, PetDefinitionIntegerId = 101, OpalCost = 20 });
        var combatUnlock = await characters.UnlockCombatPet(new UnlockCombatPetRequest { CharacterId = character.CharacterId, PayWithOpals = true, CombatPetDefinitionIntegerId = 201, Cost = 30 });
        await characters.UpdateCombatPet(new UpdateCombatPetRequest { CharacterId = character.CharacterId, CombatPetDefinitionIntegerId = 201 });
        await characters.OnPetDied(new OnPetDiedRequest { CharacterId = character.CharacterId, PetDefinitionId = 201 });
        var petState = await characters.CheckCombatPetState(new CheckCombatPetStateRequest { CharacterId = character.CharacterId, PetDefinitionId = 201 });
        var enteredPet = await characters.EnterGameWithCharacter(new EnterGameWithCharacterRequest { CharacterId = character.CharacterId });
        if (petUnlock.NewOpals != opalsBefore - 20 || combatUnlock.NewCurrencyValue != opalsBefore - 50
            || petState.IsAlive || (enteredPet.Character?.Pets?.Pets?.Count ?? 0) == 0 || (enteredPet.Character?.CombatPets?.CombatPets?.Count ?? 0) == 0)
        {
            Console.WriteLine($"FAIL  pets opals {opalsBefore}->{petUnlock.NewOpals}->{combatUnlock.NewCurrencyValue} alive={petState.IsAlive} pets={enteredPet.Character?.Pets?.Pets?.Count} combat={enteredPet.Character?.CombatPets?.CombatPets?.Count}");
            return 11;
        }
        Console.WriteLine($"PET    opals {opalsBefore}->{combatUnlock.NewCurrencyValue} persisted pets={enteredPet.Character.Pets.Pets.Count} combat={enteredPet.Character.CombatPets.CombatPets.Count} alive={petState.IsAlive}");

        // 9. Season reward track: catalogs + metadata + claim.
        var gameModes = MagicOnionClient.Create<IGameModeServiceApi>(channel, serializerProvider);
        var catalogs = MagicOnionClient.Create<ICatalogServiceApi>(channel, serializerProvider);
        var regular = await catalogs.GetCatalog(new GetCatalogRequest { Category = SharedNet.Constants.CatalogCategory.SeasonProgress, Name = "season_rewards_regular" });
        var passCatalog = await catalogs.GetCatalog(new GetCatalogRequest { Category = SharedNet.Constants.CatalogCategory.SeasonProgress, Name = "season_rewards_season_pass" });
        // The client matches the item Name against (\d+)_(\d+)_(\d+); a two-part name throws
        // while building the reward tab. Also derive the combined milestone level the client
        // computes (season*1000 + level) and sends back when claiming.
        var rewardName = regular.Catalog?.Items?.FirstOrDefault()?.Name;
        var rewardParts = rewardName?.Split('_');
        if (rewardParts is not { Length: 3 }
            || !int.TryParse(rewardParts[0], out var seasonNumber)
            || !int.TryParse(rewardParts[1], out var firstRewardLevel)
            || !int.TryParse(rewardParts[2], out _))
        {
            Console.WriteLine($"FAIL  season reward name '{rewardName}' is not season_level_slot");
            return 12;
        }
        var combinedMilestoneLevel = seasonNumber * 1000 + firstRewardLevel;
        var userApi = MagicOnionClient.Create<IUserServiceApi>(channel, serializerProvider);
        var seasonChar = (await characters.GetCharacterList(new GetCharacterListRequest())).Characters?.FirstOrDefault(c => c.GameMode is SharedNet.Constants.Game.GameMode.Season);
        if (seasonChar == null)
        {
            seasonChar = (await characters.CreateCharacter(new CreateCharacterRequest
            {
                DisplayName = "Season" + Random.Shared.Next(1000, 9999),
                CharacterClass = SharedNet.Constants.Game.CharacterClass.Mage,
                CharacterRace = SharedNet.Constants.Game.CharacterRace.HighElf,
                CharacterGameMode = SharedNet.Constants.Game.GameMode.Season,
                Data = new Game.SerializedCharacterData
                {
                    Header = new Game.SerializedCharacterData.SerializedHeader { Name = "Season", Level = 1 },
                    Data = new Game.SerializedCharacterData.SerializedData
                    {
                        Attributes = new SerializedAttributes { Values = new(), MultiplicativeValues = new() },
                        Items = new SerializedItems { Items = new() },
                        CombatStats = new Game.SerializedCharacterData.SerializedCombatStats { HelheimAttempts = new() },
                    },
                },
            })).Character;
        }
        var seasonMeta = await userApi.GetSeasonMetadata(new GetSeasonMetadataRequest());
        var claim = await gameModes.ClaimSeasonReward(new ClaimSeasonRewardRequest { SeasonLevelReward = combinedMilestoneLevel, IsSeasonPassReward = false, CharacterId = seasonChar.CharacterId });
        if ((regular.Catalog?.Items?.Count ?? 0) == 0 || (passCatalog.Catalog?.Items?.Count ?? 0) == 0
            || (seasonMeta.SeasonMetadata?.SeasonLevel ?? 0) < combinedMilestoneLevel || (claim.ReceivedRewards?.Items?.Count ?? 0) == 0)
        {
            Console.WriteLine($"FAIL  season name='{rewardName}' regular={regular.Catalog?.Items?.Count} pass={passCatalog.Catalog?.Items?.Count} level={seasonMeta.SeasonMetadata?.SeasonLevel} expected>={combinedMilestoneLevel} claim={claim.ReceivedRewards?.Items?.Count}");
            return 12;
        }
        // The client marks a milestone claimed with ClaimedSeasonRewardsByLevel.Exists(x =>
        // x == combinedMilestoneLevel), so the stored key must be the combined value, not the
        // plain track level.
        var seasonMetaAfter = await userApi.GetSeasonMetadata(new GetSeasonMetadataRequest());
        if (seasonMetaAfter.SeasonMetadata?.ClaimedSeasonRewardsByLevel?.Contains(combinedMilestoneLevel) != true)
        {
            Console.WriteLine($"FAIL  claimed key {combinedMilestoneLevel} not stored: {string.Join(",", seasonMetaAfter.SeasonMetadata?.ClaimedSeasonRewardsByLevel ?? new List<int>())}");
            return 12;
        }
        Console.WriteLine($"SEASON name='{rewardName}' combined={combinedMilestoneLevel} level={seasonMeta.SeasonMetadata.SeasonLevel} claim={claim.ReceivedRewards.Items[0].Name}");

        // 10. The remote store the client's IAP module reads its products from.
        var store = MagicOnionClient.Create<IStoreServiceApi>(channel, serializerProvider);
        var storeItems = await store.GetStoreItems(new GetStoreItemsRequest
        {
            StoreName = "RM_2.0", StoreProvider = SharedNet.Constants.StoreProvider.Steam,
        });
        // The season tab finds the pass with StoreItem.Sku == "556" and buys that id; the opal
        // bundles must use the ids from the client's bundled IAPProductCatalog.
        if ((storeItems.StoreItems?.Count ?? 0) == 0
            || !storeItems.StoreItems.Any(i => i.Sku == "556")
            || !storeItems.StoreItems.Any(i => i.Sku == "small_opal_bundle"))
        {
            Console.WriteLine($"FAIL  store items missing expected SKUs: {string.Join(",", storeItems.StoreItems?.Select(i => i.Sku) ?? new List<string>())}");
            return 15;
        }
        Console.WriteLine($"STORE  items={storeItems.StoreItems.Count} skus={string.Join(",", storeItems.StoreItems.Select(i => i.Sku))}");

        await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "done", CancellationToken.None);
        Console.WriteLine("WS OK");
        return 0;
    }

    // The Unity client serializes with LZ4BlockArray (which only compresses frames that are
    // worth it) and decodes with the same options, so use that here to match the real wire
    // format instead of the uncompressed path that hid the server's decode bug.
    private static readonly MessagePackSerializerOptions WireOptions =
        MessagePackSerializer.DefaultOptions.WithCompression(MessagePackCompression.Lz4BlockArray);

    private static async Task<Envelope> SendAsync(ClientWebSocket socket, Envelope envelope, List<Envelope> pushes = null)
    {
        var bytes = MessagePackSerializer.Serialize(envelope, WireOptions);
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

    private static List<Guid> LoadPowerIds(int count)
    {
        foreach (var dir in new[]
        {
            Environment.GetEnvironmentVariable("NORD_GAMEDATA_DIR"),
            "gamedata_decrypted",
            Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "gamedata_decrypted"),
        })
        {
            if (string.IsNullOrEmpty(dir)) continue;
            var path = Path.Combine(dir, "Powers.json");
            if (!File.Exists(path)) continue;
            using var doc = JsonDocument.Parse(File.ReadAllBytes(path));
            return doc.RootElement.EnumerateArray()
                .Select(e => e.TryGetProperty("Id", out var id) && Guid.TryParse(id.GetString(), out var g) ? g : Guid.Empty)
                .Where(g => g != Guid.Empty)
                .Take(count)
                .ToList();
        }
        return new List<Guid>();
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

        return MessagePackSerializer.Deserialize<Envelope>(ms.ToArray(), WireOptions);
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
