using System.Security.Cryptography;
using System.Text.Json;
using Game;
using Grpc.Core;
using MessagePack;
using SharedNet.Api;
using SharedNet.Constants;
using SharedNet.Dto;

namespace Nordicandia.Server.State;

/// <summary>Single-process private-server storage. Every mutation is flushed before acknowledgement.</summary>
public sealed class GameStore : IDisposable
{
    private static readonly Lazy<GameStore> Default = new(() => new GameStore(
        Environment.GetEnvironmentVariable("NORD_DATA_DIR") ?? Path.Combine(AppContext.BaseDirectory, "data")));
    public static GameStore Instance => Default.Value;
    private readonly object gate = new();
    private readonly string path;
    private readonly FileStream lease;
    private readonly TimeProvider clock;
    private State state;
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    /// <summary>The season wheel is deterministic (365-day seasons anchored at 2024-01-01), so a
    /// season is always active without persisting a schedule. <c>Number</c> is the 1-based index
    /// used by the season reward item names and by the client's combined season level.</summary>
    public static (int Number, string Name, DateTime Start, DateTime End) SeasonAt(DateTime utc)
    {
        var epoch = new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var length = TimeSpan.FromDays(365);
        var index = (long)Math.Floor((utc - epoch).Ticks / (double)length.Ticks);
        var start = epoch.AddTicks(index * length.Ticks);
        var number = (int)(index + 1);
        return (number, $"Season {number}", start, start + length);
    }

    public static int CurrentSeasonNumber() => SeasonAt(DateTime.UtcNow).Number;
    public sealed class State
    {
        public int Version { get; set; } = 1;
        public Dictionary<string, UserDto> Users { get; set; } = new();
        public Dictionary<string, SessionDto> Sessions { get; set; } = new();
        public Dictionary<Guid, SavedCharacter> Characters { get; set; } = new();
        public Dictionary<Guid, List<UserLinkedAccountDto>> LinkedAccounts { get; set; } = new();
        public Dictionary<string, AccountCredential> Credentials { get; set; } = new();
        // "{gameMode}|{tier}|{waypoint}" -> the first character to reach that world level.
        public Dictionary<string, Guid> WorldFirsts { get; set; } = new();
        // Account-level blob (SerializedUserAccountData): loot filters, season data, cosmetics.
        public Dictionary<Guid, byte[]> UserAccountData { get; set; } = new();
    }

    /// <summary>Server-only password credential. Deliberately not part of any client DTO,
    /// so the hash is never serialized into a login/account response.</summary>
    public sealed class AccountCredential
    {
        public Guid UserId { get; set; }
        public string Identity { get; set; } = string.Empty;
        public string PasswordHash { get; set; } = string.Empty;
        public DateTime Created { get; set; }
        public DateTime? Updated { get; set; }
    }

    public sealed class SavedCharacter
    {
        public Guid Owner { get; set; }
        public byte[] Header { get; set; }
        public byte[] Data { get; set; }
        public byte[] GameModeAccount { get; set; }
        // Realtime progress, flushed by the WebSocket gateway. Older snapshots omit
        // these; the defaults keep a freshly created (level 1) character consistent.
        public bool HasRealtimeProgress { get; set; }
        public double Experience { get; set; }
        public int Silver { get; set; }
        public int Opals { get; set; }
        public DateTime LastRealtimeUpdate { get; set; }
        // Guards the one-time +100% season experience bonus so repeated logins don't stack it.
        public bool SeasonBonusApplied { get; set; }
        // Aesir blessing expiries keyed by AesirOfferingTypes (1=Odin, 2=Tyr, 3=Frigg, 4=Thor).
        public Dictionary<int, DateTime> Blessings { get; set; } = new();
    }

    /// <summary>Read-only projection used by the leaderboard services.</summary>
    public readonly record struct CharacterStanding(Guid Owner, Guid CharacterId, string DisplayName, double Level,
        double Experience, int Silver, int Opals, int WorldTier, int WorldWaypoint,
        SharedNet.Constants.Game.GameMode GameMode, SharedNet.Constants.Game.CharacterClass Class,
        SharedNet.Constants.Game.CharacterRace Race, DateTime LastLogin);

    /// <summary>Snapshot of the progress fields the realtime socket is authoritative for.</summary>
    public readonly record struct RealtimeProgress(double Experience, int Silver, int Opals, DateTime UpdatedUtc)
    {
        public static readonly RealtimeProgress Empty = new(0, 0, 0, default);
    }

    /// <summary>A world milestone announced to every player over the realtime channel.
    /// <c>Kind</c> is <c>first</c> (first to reach a world level) or <c>hardcore</c> (a
    /// hardcore character died).</summary>
    public sealed record WorldAnnouncement(string Kind, int GameMode, int WorldTier, int WorldWaypoint,
        string CharacterName, Guid CharacterId);

    // Attribute ids recovered from the client's GameAttributes static ctor (see
    // steam_analysis and the SaveDump tool). Keep in sync with Progression/InventoryService.
    private const int AttrLevel = 2;
    private const int AttrWorldTier = 8;
    private const int AttrWorldTierUnlocked = 9;
    private const int AttrAttributePoints = 100;
    private const int AttrStrengthAllocated = 105;
    private const int AttrDexterityAllocated = 106;
    private const int AttrIntelligenceAllocated = 107;
    private const int AttrVitalityAllocated = 108;
    private const int AttrConstitutionAllocated = 126;
    private const int AttrAgilityAllocated = 127;
    private const int AttrMindpowerAllocated = 128;
    private const int AttrExperience = 359;
    private const int AttrExperienceBonusPercent = 361;
    private const int AttrMaxHelheimDepth = 434;
    private const int AttrHelheimDepth = 435;
    private const int SeasonBuffDefinitionId = 258;

    /// <summary>Season characters get the persistent <c>SeasonBuff</c> (definition 258, the
    /// trophy icon). The doubled experience itself is written to the character's
    /// <c>Experience_Bonus_Percent</c> (attribute 361) at the <c>Character</c> origin, which the
    /// client always applies when it loads the character, so the +100% gain cannot be lost if
    /// the buff's own attribute map is ignored. Applied once (guarded by
    /// <c>SavedCharacter.SeasonBonusApplied</c>).</summary>
    private static void EnsureSeasonExperienceBuff(SerializedCharacterData.SerializedData data)
    {
        data.Buffs ??= new SerializedCharacterData.SerializedBuffs();
        data.Buffs.Buffs ??= new List<SerializedCharacterData.SerializedBuff>();
        var buff = data.Buffs.Buffs.FirstOrDefault(b => b != null && b.DefinitionIntegerId == SeasonBuffDefinitionId);
        if (buff == null)
        {
            buff = CreateSeasonBuff();
            data.Buffs.Buffs.Add(buff);
        }
        // The effect lives on the character attributes; keep the buff attribute-free so it can
        // never be counted twice if the client does apply it.
        buff.Attributes = new SerializedAttributes { Values = new(), MultiplicativeValues = new() };
    }

    /// <summary>The attribute-free <c>SeasonBuff</c> used for the trophy icon, both on the
    /// serialized character and in the realtime <c>BuffReceivedMessage</c> push.</summary>
    public static SerializedCharacterData.SerializedBuff CreateSeasonBuff() => new()
    {
        DefinitionIntegerId = SeasonBuffDefinitionId,
        IsCharacterContext = true,
        Attributes = new SerializedAttributes { Values = new(), MultiplicativeValues = new() },
    };

    private static void ApplySeasonExperienceBonus(SerializedCharacterData.SerializedData data)
    {
        var current = GetAttribute(data, AttrExperienceBonusPercent) ?? 0.0;
        SetAttribute(data, AttrExperienceBonusPercent, current + 1.0);
    }

    /// <summary>
    /// Stamps the character's last-active epoch. The client's offline progress
    /// (<c>WindowWelcomeBack.GetTimeAwayInSeconds</c>) is
    /// <c>CurrentEpoch - IdleProgress.LastActiveEpoch</c>, so the server must keep this fresh
    /// while the character is online — otherwise a brand-new character replays the whole time
    /// since creation as "offline", which is the bug being reported.
    /// </summary>
    private static void SetLastActiveEpoch(SerializedCharacterData.SerializedData data)
    {
        data.IdleProgress ??= new SerializedCharacterData.SerializedIdleProgress();
        data.IdleProgress.LastActiveEpoch = (int)DateTimeOffset.UtcNow.ToUnixTimeSeconds();
    }

    private static bool IsSeasonMode(SharedNet.Constants.Game.GameMode mode) =>
        mode is SharedNet.Constants.Game.GameMode.Season or SharedNet.Constants.Game.GameMode.SeasonHardcore;

    private static void SetAttribute(SerializedCharacterData.SerializedData data, int id, double value)
    {
        data.Attributes ??= new SerializedAttributes { Values = new(), MultiplicativeValues = new() };
        data.Attributes.Values ??= new();
        if (!data.Attributes.Values.TryGetValue(SharedNet.Constants.Game.AttributeOrigin.Character, out var map) || map == null)
            data.Attributes.Values[SharedNet.Constants.Game.AttributeOrigin.Character] = map = new Dictionary<int, GameAttributeValue>();
        map[id] = new GameAttributeValue { Value = (int)value, ValueD = value };
    }

    private static double? GetAttribute(SerializedCharacterData.SerializedData data, int id)
    {
        if (data?.Attributes?.Values != null && data.Attributes.Values.TryGetValue(SharedNet.Constants.Game.AttributeOrigin.Character, out var map)
            && map != null && map.TryGetValue(id, out var value)) return value.ValueD;
        return null;
    }
    public GameStore(string directory, TimeProvider clock = null)
    {
        this.clock = clock ?? TimeProvider.System;
        Directory.CreateDirectory(directory);
        path = Path.Combine(Path.GetFullPath(directory), "world.json");
        lease = new FileStream(path + ".lock", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        try
        {
            state = File.Exists(path) ? JsonSerializer.Deserialize<State>(File.ReadAllBytes(path)) : new State();
            if (state == null || state.Version != 1 || state.Users == null || state.Sessions == null || state.Characters == null)
                throw new InvalidDataException("Invalid world snapshot; restore a valid backup.");
            state.LinkedAccounts ??= new();
            state.Credentials ??= new();
            state.WorldFirsts ??= new();
            state.UserAccountData ??= new();
        }
        catch { lease.Dispose(); throw; }
    }
    private DateTime Now => clock.GetUtcNow().UtcDateTime;
    private T Change<T>(Func<State, T> change)
    {
        lock (gate)
        {
            var next = JsonSerializer.Deserialize<State>(JsonSerializer.SerializeToUtf8Bytes(state));
            var result = change(next);
            using (var file = new FileStream(path + ".tmp", FileMode.Create, FileAccess.Write, FileShare.None))
            { JsonSerializer.Serialize(file, next, Json); file.Flush(true); }
            File.Move(path + ".tmp", path, true);
            state = next;
            return result;
        }
    }
    public static byte[] Pack<T>(T data) => MessagePackSerializer.Serialize(data);
    public static T Unpack<T>(byte[] data) => MessagePackSerializer.Deserialize<T>(data);
    public UserDto GetOrCreateUser(string identity)
    {
        if (string.IsNullOrWhiteSpace(identity) || identity.Length > 512) throw new RpcException(new Status(StatusCode.InvalidArgument, "Invalid identity"));
        return Change(s => {
            if (!s.Users.TryGetValue(identity, out var user))
                s.Users[identity] = user = new UserDto { UserId = Guid.NewGuid(), DisplayName = identity, Created = Now, Role = UserRole.Player };
            user.LastLogin = Now;
            return Unpack<UserDto>(Pack(user));
        });
    }
    public bool HasCredential(string identity)
    {
        lock (gate) return state.Credentials.ContainsKey(identity);
    }

    /// <summary>Verifies an email/username credential and returns the owning user, or null.</summary>
    public UserDto VerifyCredential(string identity, string password)
    {
        lock (gate)
        {
            if (!state.Credentials.TryGetValue(identity, out var credential)) return null;
            if (!PasswordHasher.Verify(password, credential.PasswordHash)) return null;
            var user = state.Users.Values.FirstOrDefault(u => u.UserId == credential.UserId);
            return user == null ? null : Unpack<UserDto>(Pack(user));
        }
    }

    /// <summary>Creates a new account with a password credential (fails if the identity exists).</summary>
    public UserDto RegisterCredential(string identity, string password, string displayName = null) => Change(s =>
    {
        if (s.Credentials.ContainsKey(identity))
            throw new RpcException(new Status(StatusCode.AlreadyExists, "Account already exists"));
        if (!s.Users.TryGetValue(identity, out var user))
            s.Users[identity] = user = new UserDto { UserId = Guid.NewGuid(), DisplayName = displayName ?? identity, Created = Now, Role = UserRole.Player };
        s.Credentials[identity] = new AccountCredential { UserId = user.UserId, Identity = identity, PasswordHash = PasswordHasher.Hash(password), Created = Now };
        user.LastLogin = Now;
        return Unpack<UserDto>(Pack(user));
    });

    /// <summary>Attaches (or replaces) a password credential for an existing user.</summary>
    public void SetCredentialForUser(Guid userId, string identity, string password) => Change(s =>
    {
        if (!s.Users.Values.Any(u => u.UserId == userId))
            throw new RpcException(new Status(StatusCode.NotFound, "Unknown user"));
        if (s.Credentials.TryGetValue(identity, out var existing) && existing.UserId != userId)
            throw new RpcException(new Status(StatusCode.AlreadyExists, "Account already exists"));
        s.Credentials[identity] = new AccountCredential { UserId = userId, Identity = identity, PasswordHash = PasswordHasher.Hash(password), Created = Now };
        return true;
    });

    /// <summary>Replaces the password for an existing credential (used by change-password).</summary>
    public bool UpdateCredentialPassword(string identity, string password) => Change(s =>
    {
        if (!s.Credentials.TryGetValue(identity, out var credential)) return false;
        credential.PasswordHash = PasswordHasher.Hash(password);
        credential.Updated = Now;
        return true;
    });

    public SessionDto CreateSession(Guid userId) => Change(s => {
        if (!s.Users.Values.Any(u => u.UserId == userId)) throw new InvalidOperationException("Unknown user");
        foreach (var key in s.Sessions.Where(p => p.Value.RefreshExpireTimestamp <= Now).Select(p => p.Key).ToArray()) s.Sessions.Remove(key);
        var session = new SessionDto { UserId = userId, AuthToken = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)),
            RefreshToken = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)), NewlyCreated = true,
            CreateTimestamp = Now, ExpireTimestamp = Now.AddHours(12), RefreshExpireTimestamp = Now.AddDays(30) };
        s.Sessions.Add(session.AuthToken, session);
        return Unpack<SessionDto>(Pack(session));
    });
    public UserDto FindUserByRefreshToken(string token)
    {
        lock (gate) {
            var session = state.Sessions.Values.FirstOrDefault(s => s.RefreshToken == token && s.RefreshExpireTimestamp > Now);
            var user = session == null ? null : state.Users.Values.FirstOrDefault(u => u.UserId == session.UserId);
            return user == null ? null : Unpack<UserDto>(Pack(user));
        }
    }
    public UserDto GetUserByAuthToken(string token)
    {
        lock (gate) {
            if (token == null || !state.Sessions.TryGetValue(token, out var session) || !(session.ExpireTimestamp > Now)) return null;
            return Unpack<UserDto>(Pack(state.Users.Values.Single(u => u.UserId == session.UserId)));
        }
    }
    public Guid RequireUser(string authorization)
    {
        var token = authorization?.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase) == true ? authorization[7..] : authorization;
        return GetUserByAuthToken(token)?.UserId ?? throw new RpcException(new Status(StatusCode.Unauthenticated, "Sign in first"));
    }
    public List<CharacterHeaderDto> Characters(Guid owner)
    { lock (gate) return state.Characters.Values.Where(c => c.Owner == owner).Select(c => Unpack<CharacterHeaderDto>(c.Header)).ToList(); }
    public CharacterHeaderDto CreateCharacter(Guid owner, CreateCharacterRequest req)
    {
        if (req?.Data?.Data == null || string.IsNullOrWhiteSpace(req.DisplayName) || req.DisplayName.Length > 32 ||
            (int)req.CharacterClass < 0 || (int)req.CharacterClass > 7 || (int)req.CharacterRace < 0 || (int)req.CharacterRace > 7 ||
            req.CharacterGameMode is not (SharedNet.Constants.Game.GameMode.Normal or SharedNet.Constants.Game.GameMode.NormalHardcore
                or SharedNet.Constants.Game.GameMode.Season or SharedNet.Constants.Game.GameMode.SeasonHardcore))
            throw new RpcException(new Status(StatusCode.InvalidArgument, "Invalid character or unsupported game mode"));
        return Change(s => {
            if (s.Characters.Values.Count(c => c.Owner == owner) >= 3) throw new RpcException(new Status(StatusCode.ResourceExhausted, "Character slots full"));
            var id = Guid.NewGuid();
            var header = new CharacterHeaderDto { CharacterId = id, DisplayName = req.DisplayName.Trim(), Created = Now,
                LastLogin = Now, Class = req.CharacterClass, Race = req.CharacterRace, GameMode = req.CharacterGameMode,
                Level = 1, Status = CharacterStatus.Active, OnlineStatus = req.OnlineStatus, Metadata = req.Metadata ?? new() };
            var data = Unpack<SerializedCharacterData.SerializedData>(Pack(req.Data.Data));
            data.CharacterId = id; data.AccountId = owner.ToString();
            SetLastActiveEpoch(data);
            var isSeason = IsSeasonMode(req.CharacterGameMode);
            if (isSeason) { EnsureSeasonExperienceBuff(data); ApplySeasonExperienceBonus(data); }
            var experience = GetAttribute(data, AttrExperience) ?? 0;
            header.Level = Progression.LevelForExperience(experience);
            SetAttribute(data, AttrLevel, header.Level);
            var account = Defaults.Create<SerializedPlayerGameModeAccountData>();
            account.GameMode = req.CharacterGameMode; account.NumStashPages = 1; account.NumPrivateStashPages = 1;
            account.NumActiveSkillSlots = 3; account.NumPassiveSkillSlots = 3; account.NumPotionSlots = 2;
            s.Characters.Add(id, new SavedCharacter { Owner = owner, Header = Pack(header), Data = Pack(data), GameModeAccount = Pack(account),
                Experience = experience, SeasonBonusApplied = isSeason });
            return header;
        });
    }
    public EnterGameWithCharacterResponse Enter(Guid owner, Guid id) => Change(s => {
        var character = Owned(s, owner, id);
        var header = Unpack<CharacterHeaderDto>(character.Header); header.LastLogin = Now;
        header.Level = Progression.LevelForExperience(character.Experience);
        character.Header = Pack(header);
        var data = Unpack<SerializedCharacterData.SerializedData>(character.Data);
        SetAttribute(data, AttrLevel, header.Level);
        SetAttribute(data, AttrExperience, character.Experience);
        if (IsSeasonMode(header.GameMode))
        {
            EnsureSeasonExperienceBuff(data);
            if (!character.SeasonBonusApplied) { ApplySeasonExperienceBonus(data); character.SeasonBonusApplied = true; }
        }
        character.Data = Pack(data);
        // Stamp the *stored* copy for the next login while still returning the previous
        // last-active value, so this login's offline window measures the real gap.
        var stored = Unpack<SerializedCharacterData.SerializedData>(character.Data);
        SetLastActiveEpoch(stored);
        character.Data = Pack(stored);
        Console.WriteLine($"[ENTER] character={id} level={header.Level} exp={character.Experience:F1} tierUnlocked={GetAttribute(data, AttrWorldTierUnlocked)} waypoints={data.Waypoints?.WaypointMap?.Count ?? 0}");
        return new EnterGameWithCharacterResponse { Character = data,
            GameModeAccountData = Unpack<SerializedPlayerGameModeAccountData>(character.GameModeAccount) };
    });
    private static SavedCharacter Owned(State s, Guid owner, Guid id) =>
        s.Characters.TryGetValue(id, out var c) && c.Owner == owner ? c : throw new RpcException(new Status(StatusCode.NotFound, "Character not found"));
    public void Delete(Guid owner, Guid id) => Change(s => { Owned(s, owner, id); s.Characters.Remove(id); return true; });

    /// <summary>Returns the stored realtime progress, seeding it from the serialized
    /// character the first time the socket opens for this character.</summary>
    public RealtimeProgress GetRealtimeProgress(Guid owner, Guid id)
    {
        lock (gate)
        {
            var c = Owned(state, owner, id);
            return new RealtimeProgress(c.Experience, c.Silver, c.Opals, c.LastRealtimeUpdate);
        }
    }

    public RealtimeProgress SaveRealtimeProgress(Guid owner, Guid id, double experience, int silver, int opals)
        => Change(s =>
        {
            var c = Owned(s, owner, id);
            c.HasRealtimeProgress = true;
            c.Experience = Math.Max(0, experience);
            c.Silver = Math.Max(0, silver);
            c.Opals = Math.Max(0, opals);
            c.LastRealtimeUpdate = Now;
            // Level is a pure function of total experience; keep the header (shown on the
            // character-select screen and used by the leaderboards) in sync with it.
            var header = Unpack<CharacterHeaderDto>(c.Header);
            header.Level = Progression.LevelForExperience(c.Experience);
            c.Header = Pack(header);
            // Also keep the serialized character in sync so a relogin starts from the
            // authoritative level/experience instead of the creation-time values.
            var data = Unpack<SerializedCharacterData.SerializedData>(c.Data);
            SetAttribute(data, AttrLevel, header.Level);
            SetAttribute(data, AttrExperience, c.Experience);
            // Refresh the "last active" stamp on every realtime sync so the next login's
            // offline progress window measures only the real offline gap.
            SetLastActiveEpoch(data);
            c.Data = Pack(data);
            return new RealtimeProgress(c.Experience, c.Silver, c.Opals, c.LastRealtimeUpdate);
        });

    /// <summary>Flattened view of every character, for the leaderboard services.</summary>
    public IReadOnlyList<CharacterStanding> Standings()
    {
        lock (gate)
            return state.Characters.Values.Select(c =>
            {
                var h = Unpack<CharacterHeaderDto>(c.Header);
                var data = Unpack<SerializedCharacterData.SerializedData>(c.Data);
                var tier = (int)(GetAttribute(data, AttrWorldTierUnlocked) ?? 0);
                var waypoint = data.Waypoints?.WaypointMap != null && data.Waypoints.WaypointMap.Count > 0
                    ? data.Waypoints.WaypointMap.Values.Max(w => w?.HighestWaypointCleared ?? w?.MaxWaypoint ?? 0)
                    : 0;
                return new CharacterStanding(c.Owner, h.CharacterId, h.DisplayName, h.Level, c.Experience, c.Silver,
                    c.Opals, tier, waypoint, h.GameMode, h.Class, h.Race, h.LastLogin ?? DateTime.UtcNow);
            }).ToList();
    }

    private static readonly HashSet<SharedNet.Constants.Game.ItemSlotTypes> EquipmentSlots = new()
    {
        SharedNet.Constants.Game.ItemSlotTypes.Head, SharedNet.Constants.Game.ItemSlotTypes.Neck,
        SharedNet.Constants.Game.ItemSlotTypes.Shoulders, SharedNet.Constants.Game.ItemSlotTypes.Chest,
        SharedNet.Constants.Game.ItemSlotTypes.Back, SharedNet.Constants.Game.ItemSlotTypes.Wrists,
        SharedNet.Constants.Game.ItemSlotTypes.Gloves, SharedNet.Constants.Game.ItemSlotTypes.Waist,
        SharedNet.Constants.Game.ItemSlotTypes.Legs, SharedNet.Constants.Game.ItemSlotTypes.Boots,
        SharedNet.Constants.Game.ItemSlotTypes.RightRing, SharedNet.Constants.Game.ItemSlotTypes.LeftRing,
        SharedNet.Constants.Game.ItemSlotTypes.MainHand, SharedNet.Constants.Game.ItemSlotTypes.OffHand,
    };

    private static bool IsEquipmentSlot(SharedNet.Constants.Game.ItemSlotTypes slot) => EquipmentSlots.Contains(slot);

    private static SerializedItemInventoryLocation FreeInventoryLocation(List<SerializedItem> items, SerializedItem exclude)
    {
        var used = new HashSet<(int Page, int Row, int Column)>(items
            .Where(i => i?.Location != null && i != exclude && i.Slot == SharedNet.Constants.Game.ItemSlotTypes.Inventory)
            .Select(i => (i.Location.Page, i.Location.Row, i.Location.Column)));
        for (var page = 0; page < 20; page++)
            for (var row = 0; row < 40; row++)
                for (var col = 0; col < 20; col++)
                    if (!used.Contains((page, row, col)))
                        return new SerializedItemInventoryLocation { Page = page, Row = row, Column = col };
        return new SerializedItemInventoryLocation { Page = 99, Row = 0, Column = 0 };
    }

    /// <summary>Builds the public "inspect character" payload the leaderboard/inspect UI
    /// consumes. Equipped items are the ones the client journals into slots 0–13.</summary>
    public SharedNet.Dto.CharacterInspectionDto Inspection(Guid targetCharacterId)
    {
        lock (gate)
        {
            if (!state.Characters.TryGetValue(targetCharacterId, out var c)) return null;
            var header = Unpack<CharacterHeaderDto>(c.Header);
            var data = Unpack<SerializedCharacterData.SerializedData>(c.Data);
            var cs = data.CombatStats;
            var equipped = (data.Items?.Items ?? new List<SerializedItem>())
                .Where(i => i != null && EquipmentSlots.Contains(i.Slot)).ToList();
            var tier = (int)(GetAttribute(data, AttrWorldTierUnlocked) ?? 0);
            var bestTime = data.Waypoints?.WaypointMap?.Values
                .Where(w => w?.HighestWaypointBestClearTime != null)
                .Select(w => w.HighestWaypointBestClearTime!.Value)
                .DefaultIfEmpty(TimeSpan.Zero).Min() ?? TimeSpan.Zero;
            var waypoint = data.Waypoints?.WaypointMap != null && data.Waypoints.WaypointMap.Count > 0
                ? data.Waypoints.WaypointMap.Values.Max(w => w?.HighestWaypointCleared ?? w?.MaxWaypoint ?? 0)
                : 0;
            var accountName = state.Users.Values.FirstOrDefault(u => u.UserId == c.Owner)?.DisplayName;
            return new SharedNet.Dto.CharacterInspectionDto
            {
                Name = header.DisplayName,
                AccountName = accountName,
                GuildTag = header.GuildTag,
                GuildName = header.GuildName,
                RaceIntegerId = (int)header.Race,
                GameMode = header.GameMode,
                ClassIntegerId = (int)header.Class,
                AvatarIntegerId = header.AvatarDefinitionIntegerId ?? -1,
                AvatarFrameIntegerId = header.AvatarFrameDefinitionIntegerId ?? -1,
                Level = header.Level,
                Offense = cs?.Offense ?? 0,
                Defense = cs?.Defense ?? 0,
                Recovery = cs?.Recovery ?? 0,
                EquippedItems = equipped,
                HighestWorldTier = tier,
                HighestWorldLevel = waypoint,
                HighestWorldLevelBestTime = bestTime,
                ActiveSkills = new List<SharedNet.Dto.CharacterActiveSkillMetadata>(),
                PassiveSkills = new List<SharedNet.Dto.CharacterPassiveSkillMetadata>(),
            };
        }
    }

    /// <summary>Applies the client's incremental inventory journal to the persisted
    /// character. Add operations carry the full item, so the server never needs item
    /// definitions; delete/move/sort only carry ids + destinations.</summary>
    public void ApplyItemOperations(Guid owner, Guid characterId, IList<ItemOperationEntry> operations)
        => Change(s =>
        {
            var c = Owned(s, owner, characterId);
            var data = Unpack<SerializedCharacterData.SerializedData>(c.Data);
            data.Items ??= new SerializedItems();
            data.Items.Items ??= new List<SerializedItem>();
            foreach (var op in operations ?? Array.Empty<ItemOperationEntry>())
            {
                switch (op)
                {
                    case AddItemOperationEntry add when add.Item != null:
                        data.Items.Items.RemoveAll(i => i != null && i.Id == add.Item.Id);
                        data.Items.Items.Add(add.Item);
                        break;
                    case DeleteItemOperationEntry del:
                        data.Items.Items.RemoveAll(i => i != null && i.Id == del.ItemId);
                        break;
                    case MoveItemOperationEntry move:
                        foreach (var i in data.Items.Items.Where(i => i != null && i.Id == move.ItemId))
                        {
                            i.Slot = move.ToSlot;
                            i.Location = move.ToLocation;
                        }
                        break;
                    case SortItemOperationEntry sort:
                        foreach (var i in data.Items.Items.Where(i => i != null && i.Id == sort.ItemId))
                        {
                            i.Slot = sort.ToSlot;
                            i.Location = sort.ToLocation;
                        }
                        break;
                }
            }
            c.Data = Pack(data);
            return true;
        });

    /// <summary>Reads the item list currently persisted for a character (used by tests).</summary>
    public List<SerializedItem> GetItems(Guid owner, Guid id)
    {
        lock (gate)
        {
            var c = Owned(state, owner, id);
            return Unpack<SerializedCharacterData.SerializedData>(c.Data)?.Items?.Items ?? new List<SerializedItem>();
        }
    }

    public readonly record struct MerchantPurchase(SerializedItems Items, int NewBalance);

    /// <summary>
    /// Atomically charges a character and adds a merchant item. The merchant UI replaces
    /// inventory page 1 with the returned list, so return a complete snapshot of that page.
    /// </summary>
    public MerchantPurchase BuyMerchantItem(Guid owner, Guid characterId, SerializedItem item, int price, bool useOpals)
    {
        if (item == null || price < 0)
            throw new RpcException(new Status(StatusCode.InvalidArgument, "Invalid merchant purchase"));

        return Change(s =>
        {
            var c = Owned(s, owner, characterId);
            var balance = useOpals ? c.Opals : c.Silver;
            if (balance < price)
                throw new RpcException(new Status(StatusCode.FailedPrecondition,
                    useOpals ? "Not enough opals" : "Not enough silver"));

            var data = Unpack<SerializedCharacterData.SerializedData>(c.Data);
            data.Items ??= new SerializedItems();
            data.Items.Items ??= new List<SerializedItem>();

            var used = new HashSet<(int Row, int Column)>(data.Items.Items
                .Where(i => i?.Location != null && i.Slot == SharedNet.Constants.Game.ItemSlotTypes.Inventory && i.Location.Page == 1)
                .Select(i => (i.Location.Row, i.Location.Column)));
            SerializedItemInventoryLocation location = null;
            for (var row = 0; row < 40 && location == null; row++)
                for (var column = 0; column < 20; column++)
                    if (!used.Contains((row, column)))
                    {
                        location = new SerializedItemInventoryLocation { Page = 1, Row = row, Column = column };
                        break;
                    }
            if (location == null)
                throw new RpcException(new Status(StatusCode.ResourceExhausted, "Inventory is full"));

            item = Unpack<SerializedItem>(Pack(item));
            item.Id = item.Id == Guid.Empty ? Guid.NewGuid() : item.Id;
            item.Slot = SharedNet.Constants.Game.ItemSlotTypes.Inventory;
            item.Location = location;
            data.Items.Items.Add(item);

            if (useOpals) c.Opals -= price;
            else c.Silver -= price;
            c.HasRealtimeProgress = true;
            c.LastRealtimeUpdate = Now;
            c.Data = Pack(data);

            var page = data.Items.Items
                .Where(i => i?.Location != null && i.Slot == SharedNet.Constants.Game.ItemSlotTypes.Inventory && i.Location.Page == 1)
                .Select(i => Unpack<SerializedItem>(Pack(i))).ToList();
            return new MerchantPurchase(new SerializedItems { Items = page }, useOpals ? c.Opals : c.Silver);
        });
    }

    public int AddSilver(Guid owner, Guid id, int amount) => Change(s =>
    {
        var c = Owned(s, owner, id);
        c.Silver = (int)Math.Clamp((long)c.Silver + amount, 0, int.MaxValue);
        c.HasRealtimeProgress = true;
        c.LastRealtimeUpdate = Now;
        return c.Silver;
    });

    public int AddOpals(Guid owner, Guid id, int amount) => Change(s =>
    {
        var c = Owned(s, owner, id);
        c.Opals = (int)Math.Clamp((long)c.Opals + amount, 0, int.MaxValue);
        c.HasRealtimeProgress = true;
        c.LastRealtimeUpdate = Now;
        return c.Opals;
    });

    public int GetSilver(Guid owner, Guid id) { lock (gate) return Owned(state, owner, id).Silver; }
    public int GetOpals(Guid owner, Guid id) { lock (gate) return Owned(state, owner, id).Opals; }
    public string CharacterDisplayName(Guid owner, Guid id)
    {
        lock (gate) return state.Characters.TryGetValue(id, out var c) && c.Owner == owner
            ? Unpack<CharacterHeaderDto>(c.Header).DisplayName : null;
    }

    /// <summary>Account-level data blob (loot filters, season progress, cosmetics). The
    /// client reads it from <c>GetUserAccountData.AccountData</c>; missing blobs get a
    /// fully-populated default so the client never walks a null graph.</summary>
    public SerializedUserAccountData GetAccountData(Guid userId)
    {
        lock (gate)
        {
            var data = state.UserAccountData.TryGetValue(userId, out var bytes) && bytes is { Length: > 0 }
                ? Unpack<SerializedUserAccountData>(bytes)
                : null;
            data = NormalizeAccountData(data);

            // The client's PlayerAccount derives NumCharactersOnAccount from
            // CharactersByGameMode and the OnlineProfilePuller walks the same map when it
            // builds the local device profile. Leaving it empty (the historical behaviour)
            // makes the online account look character-less and breaks profile sync, so
            // rebuild it from the character headers we actually store.
            var mine = state.Characters.Values.Where(c => c.Owner == userId).ToList();
            var byMode = new Dictionary<int, List<Guid>>();
            foreach (var c in mine)
            {
                var h = Unpack<CharacterHeaderDto>(c.Header);
                var mode = (int)h.GameMode;
                if (!byMode.TryGetValue(mode, out var list)) byMode[mode] = list = new();
                list.Add(h.CharacterId);
            }
            data.CharactersByGameMode = byMode;
            return data;
        }
    }

    private static SerializedUserAccountData NormalizeAccountData(SerializedUserAccountData data)
    {
        data ??= new SerializedUserAccountData();
        data.LootFilters ??= new SerializedLootFilters { Filters = new() };
        data.SeasonData ??= new Game.SerializedPlayerAccountData.SerializedSeasonData
        {
            ClaimedSeasonRewardsByLevel = new(), ClaimedSeasonPassRewardsByLevel = new(),
        };
        data.SeasonalInfo ??= new Game.SerializedPlayerAccountData.SerializedSeasonalInfo();
        data.CharactersByGameMode ??= new();
        data.UnlockedAvatarIntegerIds ??= new();
        data.UnlockedAvatarFrameIntegerIds ??= new();
        data.UnclaimedPurchases ??= new();
        data.PermanentPurchases ??= new();
        data.RewardedAdsWatched ??= new();
        data.NumCharacterSlots ??= 3;
        return data;
    }

    public void SaveCharacterLootFilters(Guid owner, Guid characterId, SerializedCharacterData.SerializedCharacterLootFilters filters) => Change(s =>
    {
        var c = Owned(s, owner, characterId);
        var data = Unpack<SerializedCharacterData.SerializedData>(c.Data);
        data.LootFilters = filters;
        c.Data = Pack(data);
        return true;
    });

    /// <summary>Persists the global rarity thresholds (the Settings dropdowns) on the
    /// character header so they survive a relogin.</summary>
    public void SaveCharacterSettings(Guid owner, Guid characterId, SharedNet.Constants.Game.Rarity low, SharedNet.Constants.Game.Rarity high) => Change(s =>
    {
        var c = Owned(s, owner, characterId);
        var header = Unpack<CharacterHeaderDto>(c.Header);
        header.Metadata ??= new();
        header.Metadata["LowRarityThreshold"] = (int)low;
        header.Metadata["HighRarityThreshold"] = (int)high;
        c.Header = Pack(header);
        return true;
    });

    public void SaveUserLootFilters(Guid userId, SerializedLootFilters filters) => Change(s =>
    {
        var data = NormalizeAccountData(s.UserAccountData.TryGetValue(userId, out var bytes) && bytes is { Length: > 0 }
            ? Unpack<SerializedUserAccountData>(bytes) : null);
        data.LootFilters = filters;
        s.UserAccountData[userId] = Pack(data);
        return true;
    });

    /// <summary>Consumes the offering's opals and stamps a blessing expiry. The client owns
    /// the blessing duration/effect; the server only needs to remember whether one is active.</summary>
    public (int NewOpals, bool Applied) MakeOffering(Guid owner, Guid characterId, int offeringType, int offeringSize, int offeredOpals) => Change(s =>
    {
        var c = Owned(s, owner, characterId);
        var spent = Math.Clamp(offeredOpals, 0, c.Opals);
        c.Opals -= spent;
        c.HasRealtimeProgress = true;
        c.LastRealtimeUpdate = Now;
        c.Blessings ??= new();
        var duration = offeringSize switch
        {
            1 => TimeSpan.FromHours(1),
            2 => TimeSpan.FromHours(6),
            3 => TimeSpan.FromDays(1),
            4 => TimeSpan.FromDays(3),
            _ => TimeSpan.FromHours(1),
        };
        if (offeringType > 0) c.Blessings[offeringType] = Now.Add(duration);
        return (c.Opals, spent > 0);
    });

    private static readonly Dictionary<int, int> BlessingBuffIds = new()
    {
        [1] = 276, // AesirOdinBuff
        [2] = 278, // AesirTyrBuff
        [3] = 280, // AesirFriggBuff
        [4] = 282, // AesirThorBuff
    };

    /// <summary>Returns the four Aesir blessing buffs that have not expired, pruning the
    /// expired ones. The buff carries only the definition id; the client applies its effect.</summary>
    public (SerializedCharacterData.SerializedBuff Odin, SerializedCharacterData.SerializedBuff Tyr,
        SerializedCharacterData.SerializedBuff Frigg, SerializedCharacterData.SerializedBuff Thor)
        GetActiveBlessings(Guid owner, Guid characterId) => Change(s =>
        {
            var c = Owned(s, owner, characterId);
            c.Blessings ??= new();
            var now = Now;
            foreach (var expired in c.Blessings.Where(kv => kv.Value <= now).Select(kv => kv.Key).ToList())
                c.Blessings.Remove(expired);
            SerializedCharacterData.SerializedBuff Buff(int type) => c.Blessings.ContainsKey(type) && BlessingBuffIds.TryGetValue(type, out var id)
                ? new SerializedCharacterData.SerializedBuff
                {
                    DefinitionIntegerId = id,
                    IsCharacterContext = true,
                    Attributes = new SerializedAttributes { Values = new(), MultiplicativeValues = new() },
                }
                : null;
            return (Buff(1), Buff(2), Buff(3), Buff(4));
        });

    private static SerializedCharacterData.SerializedPets EnsurePets(SerializedCharacterData.SerializedData data)
    {
        data.Pets ??= new SerializedCharacterData.SerializedPets { Pets = new() };
        data.Pets.Pets ??= new();
        return data.Pets;
    }

    private static SerializedCharacterData.SerializedCombatPets EnsureCombatPets(SerializedCharacterData.SerializedData data)
    {
        data.CombatPets ??= new SerializedCharacterData.SerializedCombatPets { CombatPets = new() };
        data.CombatPets.CombatPets ??= new();
        return data.CombatPets;
    }

    public void UpdatePet(Guid owner, Guid characterId, int petDefinitionIntegerId) => Change(s =>
    {
        var c = Owned(s, owner, characterId);
        var data = Unpack<SerializedCharacterData.SerializedData>(c.Data);
        var pets = EnsurePets(data);
        if (pets.Pets.Any(p => p != null && p.DefinitionIntegerId == petDefinitionIntegerId))
            pets.CurrentPetDefinitionIntegerId = petDefinitionIntegerId;
        c.Data = Pack(data);
        return true;
    });

    public int UnlockPet(Guid owner, Guid characterId, int petDefinitionIntegerId, int opalCost) => Change(s =>
    {
        var c = Owned(s, owner, characterId);
        var data = Unpack<SerializedCharacterData.SerializedData>(c.Data);
        var pets = EnsurePets(data);
        if (!pets.Pets.Any(p => p != null && p.DefinitionIntegerId == petDefinitionIntegerId))
        {
            c.Opals -= Math.Clamp(opalCost, 0, c.Opals);
            pets.Pets.Add(new SerializedCharacterData.SerializedPet
            {
                DefinitionIntegerId = petDefinitionIntegerId,
                Items = new SerializedItems { Items = new() },
                InventoryColumns = 10,
            });
        }
        pets.CurrentPetDefinitionIntegerId = petDefinitionIntegerId;
        c.HasRealtimeProgress = true;
        c.LastRealtimeUpdate = Now;
        c.Data = Pack(data);
        return c.Opals;
    });

    public void UpdateCombatPet(Guid owner, Guid characterId, int combatPetDefinitionIntegerId) => Change(s =>
    {
        var c = Owned(s, owner, characterId);
        var data = Unpack<SerializedCharacterData.SerializedData>(c.Data);
        var pets = EnsureCombatPets(data);
        if (pets.CombatPets.Any(p => p != null && p.DefinitionIntegerId == combatPetDefinitionIntegerId))
            pets.CurrentCombatPetDefinitionIntegerId = combatPetDefinitionIntegerId;
        c.Data = Pack(data);
        return true;
    });

    public (int NewCurrency, bool PayWithOpals) UnlockCombatPet(Guid owner, Guid characterId, bool payWithOpals, int combatPetDefinitionIntegerId, int cost) => Change(s =>
    {
        var c = Owned(s, owner, characterId);
        var data = Unpack<SerializedCharacterData.SerializedData>(c.Data);
        var pets = EnsureCombatPets(data);
        if (!pets.CombatPets.Any(p => p != null && p.DefinitionIntegerId == combatPetDefinitionIntegerId))
        {
            var spend = Math.Max(0, cost);
            if (payWithOpals) c.Opals -= Math.Clamp(spend, 0, c.Opals);
            else c.Silver -= Math.Clamp(spend, 0, c.Silver);
            pets.CombatPets.Add(new SerializedCharacterData.SerializedCombatPet
            {
                DefinitionIntegerId = combatPetDefinitionIntegerId,
                Level = 1,
                Experience = 0,
                IsAlive = true,
                AdsLeftToWatch = 3,
            });
        }
        pets.CurrentCombatPetDefinitionIntegerId = combatPetDefinitionIntegerId;
        c.HasRealtimeProgress = true;
        c.LastRealtimeUpdate = Now;
        c.Data = Pack(data);
        return (payWithOpals ? c.Opals : c.Silver, payWithOpals);
    });

    public (bool IsAlive, DateTime? LastDeath, int AdsLeft) CheckCombatPet(Guid owner, Guid characterId, int petDefinitionId)
    {
        lock (gate)
        {
            var c = Owned(state, owner, characterId);
            var pets = Unpack<SerializedCharacterData.SerializedData>(c.Data).CombatPets?.CombatPets;
            var pet = pets?.FirstOrDefault(x => x != null && x.DefinitionIntegerId == petDefinitionId);
            return pet == null ? (false, null, 0) : (pet.IsAlive, pet.LastDeathTime, pet.AdsLeftToWatch);
        }
    }

    public void MutateCombatPet(Guid owner, Guid characterId, int petDefinitionId, Action<SerializedCharacterData.SerializedCombatPet> mutate) => Change(s =>
    {
        var c = Owned(s, owner, characterId);
        var data = Unpack<SerializedCharacterData.SerializedData>(c.Data);
        var pet = data.CombatPets?.CombatPets?.FirstOrDefault(x => x != null && x.DefinitionIntegerId == petDefinitionId);
        if (pet != null) mutate(pet);
        c.Data = Pack(data);
        return true;
    });

    /// <summary>Adds items to a character's inventory (used by season rewards). Returns the
    /// exact instances persisted so the caller can echo them to the client.</summary>
    public List<SerializedItem> GrantItems(Guid owner, Guid characterId, IList<SerializedItem> items) => Change(s =>
    {
        var c = Owned(s, owner, characterId);
        var data = Unpack<SerializedCharacterData.SerializedData>(c.Data);
        data.Items ??= new SerializedItems { Items = new() };
        data.Items.Items ??= new();
        foreach (var item in items ?? Array.Empty<SerializedItem>())
            if (item != null) data.Items.Items.Add(item);
        c.Data = Pack(data);
        return (items ?? new List<SerializedItem>()).Where(i => i != null).ToList();
    });

    /// <summary>Highest level among the account's Season / Season-Hardcore characters, used
    /// as the season reward track level.</summary>
    public int SeasonLevel(Guid owner)
    {
        lock (gate)
        {
            var max = 0.0;
            foreach (var c in state.Characters.Values)
            {
                if (c.Owner != owner) continue;
                var header = Unpack<CharacterHeaderDto>(c.Header);
                if (header.GameMode is SharedNet.Constants.Game.GameMode.Season or SharedNet.Constants.Game.GameMode.SeasonHardcore)
                    max = Math.Max(max, header.Level);
            }
            return (int)max;
        }
    }

    /// <summary>Records a season reward level as claimed. Returns false if it was already
    /// claimed (so the caller does not double-grant).</summary>
    public bool ClaimSeasonReward(Guid userId, int level, bool seasonPass) => Change(s =>
    {
        var data = NormalizeAccountData(s.UserAccountData.TryGetValue(userId, out var bytes) && bytes is { Length: > 0 }
            ? Unpack<SerializedUserAccountData>(bytes) : null);
        var list = seasonPass
            ? data.SeasonData.ClaimedSeasonPassRewardsByLevel ??= new()
            : data.SeasonData.ClaimedSeasonRewardsByLevel ??= new();
        if (list.Contains(level)) return false;
        list.Add(level);
        s.UserAccountData[userId] = Pack(data);
        return true;
    });

    public bool SetSeasonPass(Guid userId, bool owned) => Change(s =>
    {
        var data = NormalizeAccountData(s.UserAccountData.TryGetValue(userId, out var bytes) && bytes is { Length: > 0 }
            ? Unpack<SerializedUserAccountData>(bytes) : null);
        data.SeasonData.HasSeasonPass = owned;
        s.UserAccountData[userId] = Pack(data);
        return true;
    });

    /// <summary>Season UI metadata: the stored claim state plus the current season level
    /// (highest Season/Season-Hardcore character).</summary>
    public SerializedPlayerAccountData.SerializedSeasonData GetSeasonData(Guid owner)
    {
        var season = GetAccountData(owner).SeasonData;
        season.ClaimedSeasonRewardsByLevel ??= new();
        season.ClaimedSeasonPassRewardsByLevel ??= new();
        // The client stores/looks up the season level as a combined value: seasonNumber*1000
        // plus the reward level (see WindowSeasonProgress.LoadSeasonRewards and the
        // combinedMilestoneLevel it sends back on claim). Keep the stored value in the same
        // encoding so reward milestones unlock and claimed state matches.
        var combined = CurrentSeasonNumber() * 1000 + SeasonLevel(owner);
        season.SeasonLevel = Math.Max(season.SeasonLevel, combined);
        return season;
    }

    /// <summary>Linked external identities shown on the account screen. An empty list makes
    /// the client show the account as "unregistered", so `LoginWithSteam*` also links Steam.</summary>
    public List<UserLinkedAccountDto> GetLinkedAccounts(Guid userId)
    {
        lock (gate)
            return state.LinkedAccounts.TryGetValue(userId, out var list) && list != null
                ? list.Select(Clone).ToList()
                : new List<UserLinkedAccountDto>();
    }

    public UserLinkedAccountDto LinkAccount(Guid userId, SharedNet.Constants.LoginIdentityProvider platform,
        string platformUserId, string username, string email) => Change(s =>
    {
        if (!s.Users.Values.Any(u => u.UserId == userId)) throw new InvalidOperationException("Unknown user");
        if (!s.LinkedAccounts.TryGetValue(userId, out var list) || list == null)
            s.LinkedAccounts[userId] = list = new List<UserLinkedAccountDto>();
        var account = list.FirstOrDefault(a => a.Platform == platform);
        if (account == null) list.Add(account = new UserLinkedAccountDto { Platform = platform });
        account.PlatformUserId = platformUserId;
        account.Username = username;
        account.Email = email;
        account.LastLogin = Now;
        return Clone(account);
    });

    public void UnlinkAccount(Guid userId, SharedNet.Constants.LoginIdentityProvider platform) => Change(s =>
    {
        if (s.LinkedAccounts.TryGetValue(userId, out var list) && list != null)
            list.RemoveAll(a => a.Platform == platform);
        return true;
    });

    public void SetLinkedPassword(Guid userId, SharedNet.Constants.LoginIdentityProvider platform, string password) => Change(s =>
    {
        if (s.LinkedAccounts.TryGetValue(userId, out var list) && list != null)
        {
            var account = list.FirstOrDefault(a => a.Platform == platform);
            if (account != null) account.Password = password;
        }
        return true;
    });

    private static UserLinkedAccountDto Clone(UserLinkedAccountDto a) => new()
    {
        Email = a.Email, Platform = a.Platform, PlatformUserId = a.PlatformUserId,
        Username = a.Username, LastLogin = a.LastLogin,
    };

    /// <summary>Persists the player's stat allocation into the character's serialized
    /// attributes. The client sends absolute allocated values, not deltas. Returns the
    /// total points pool to echo back in the response.</summary>
    public (double Available, double Strength, double Dexterity, double Intelligence, double Vitality,
        double Constitution, double Agility, double Mindpower)
        ApplyAllocatedAttributes(Guid owner, Guid characterId, AllocateCharacterAttributesRequest req)
        => Change(s =>
        {
            var c = Owned(s, owner, characterId);
            var data = Unpack<SerializedCharacterData.SerializedData>(c.Data);
            // The client's request carries only the *pending* preview: TabPlayerBaseAttributes
            // .AssignAttributes seeds _AllocatedValues at 0 and adds the click delta, then the
            // confirm handler clears the dictionary. So the server must accumulate onto the
            // stored allocation instead of overwriting it, or every confirm wipes the previous
            // ones (this was the "allocation regresses" bug).
            var str = (GetAttribute(data, AttrStrengthAllocated) ?? 0) + req.Strength;
            var dex = (GetAttribute(data, AttrDexterityAllocated) ?? 0) + req.Dexterity;
            var intel = (GetAttribute(data, AttrIntelligenceAllocated) ?? 0) + req.Intelligence;
            var vit = (GetAttribute(data, AttrVitalityAllocated) ?? 0) + req.Vitality;
            var con = (GetAttribute(data, AttrConstitutionAllocated) ?? 0) + req.Constitution;
            var agi = (GetAttribute(data, AttrAgilityAllocated) ?? 0) + req.Agility;
            var mind = (GetAttribute(data, AttrMindpowerAllocated) ?? 0) + req.Mindpower;
            SetAttribute(data, AttrStrengthAllocated, str);
            SetAttribute(data, AttrDexterityAllocated, dex);
            SetAttribute(data, AttrIntelligenceAllocated, intel);
            SetAttribute(data, AttrVitalityAllocated, vit);
            SetAttribute(data, AttrConstitutionAllocated, con);
            SetAttribute(data, AttrAgilityAllocated, agi);
            SetAttribute(data, AttrMindpowerAllocated, mind);
            c.Data = Pack(data);
            // AttributePoints is the *remaining* pool (Character.LevelUp adds 5/level, and the
            // client subtracts its pending preview).
            var level = Unpack<CharacterHeaderDto>(c.Header).Level;
            var totalPoints = Math.Max(0, (level - 1) * 5);
            var allocated = str + dex + intel + vit + con + agi + mind;
            var available = Math.Max(0, totalPoints - allocated);
            Console.WriteLine($"[ATTR] char={characterId} +({req.Strength},{req.Dexterity},{req.Intelligence},{req.Vitality},{req.Constitution},{req.Agility},{req.Mindpower}) => str={str} dex={dex} int={intel} vit={vit} con={con} agi={agi} mind={mind} total={totalPoints} available={available}");
            return (available, str, dex, intel, vit, con, agi, mind);
        });

    /// <summary>Raises the persisted world progression ("area level") when a run completes.
    /// Stores the unlocked tier, the per-tier waypoint record, and (for Helheim) the depth.</summary>
    /// <summary>Persists a skill-bar / passive-tree assignment. The client sends the
    /// power's definition id and the slot index; the wire format the character uses stores
    /// an integer <c>PowerHashSafe</c>, resolved through <see cref="PowerCatalog"/>.</summary>
    public void ApplySkillAssignment(Guid owner, Guid characterId, IList<CharacterSkillEntry> skills,
        SharedNet.Constants.Game.PowerSlotTypes slotType) => Change(s =>
    {
        var c = Owned(s, owner, characterId);
        var data = Unpack<SerializedCharacterData.SerializedData>(c.Data);
        data.Skills ??= new SerializedCharacterData.SerializedSkills { Skills = new() };
        data.Skills.Skills ??= new List<SerializedCharacterData.SerializedSkill>();
        data.Powers ??= new SerializedCharacterData.SerializedPowers { Powers = new() };
        data.Powers.Powers ??= new List<SerializedCharacterData.SerializedPower>();
        int SlotIndex(SerializedCharacterData.SerializedSkill k) =>
            slotType == SharedNet.Constants.Game.PowerSlotTypes.SkillTraining ? k.Row : k.Column;

        // The client sends only the slot(s) it changed, not the whole bar, so merge by slot
        // index. Wiping the slot type first erased every previously assigned skill (the
        // "only the last modified skill survives" bug). An empty PowerId clears the slot.
        foreach (var entry in skills ?? Array.Empty<CharacterSkillEntry>())
        {
            if (entry == null) continue;
            var existing = data.Skills.Skills.FirstOrDefault(k => k != null && k.Slot == slotType && SlotIndex(k) == entry.SkillSlot);
            if (entry.PowerId == Guid.Empty)
            {
                if (existing != null) data.Skills.Skills.Remove(existing);
                continue;
            }
            if (!PowerCatalog.TryGet(entry.PowerId, out var info)) continue;
            if (existing == null)
            {
                existing = new SerializedCharacterData.SerializedSkill { Slot = slotType };
                data.Skills.Skills.Add(existing);
            }
            existing.PowerHash = info.HashSafe;
            existing.PowerHashSafe = info.HashSafe;
            if (slotType == SharedNet.Constants.Game.PowerSlotTypes.SkillTraining) existing.Row = entry.SkillSlot;
            else existing.Column = entry.SkillSlot;
            if (!data.Powers.Powers.Any(p => p != null && p.PowerHashSafe == info.HashSafe))
                data.Powers.Powers.Add(new SerializedCharacterData.SerializedPower
                {
                    PowerHash = info.HashSafe, PowerHashSafe = info.HashSafe, Power_Rank = 1,
                    Power_Masteries = new SerializedCharacterData.SerializedPowerMasteries { Masteries = new() },
                });
        }
        c.Data = Pack(data);
        return true;
    });

    public WorldAnnouncement ApplyWorldProgress(Guid owner, Guid characterId, int worldTier, int worldWaypoint, TimeSpan duration, int depth = 0)
        => Change(s =>
        {
            var c = Owned(s, owner, characterId);
            var header = Unpack<CharacterHeaderDto>(c.Header);
            var data = Unpack<SerializedCharacterData.SerializedData>(c.Data);
            if (worldTier > 0)
            {
                var unlocked = Math.Max(GetAttribute(data, AttrWorldTierUnlocked) ?? 0, worldTier);
                SetAttribute(data, AttrWorldTierUnlocked, unlocked);
                SetAttribute(data, AttrWorldTier, Math.Max(GetAttribute(data, AttrWorldTier) ?? 0, worldTier));

                data.Waypoints ??= new SerializedCharacterData.SerializedWaypoints { WaypointMap = new() };
                data.Waypoints.WaypointMap ??= new Dictionary<int, SerializedCharacterData.SerializedWorldTierWaypoint>();
                if (!data.Waypoints.WaypointMap.TryGetValue(worldTier, out var waypoint) || waypoint == null)
                    data.Waypoints.WaypointMap[worldTier] = waypoint = new SerializedCharacterData.SerializedWorldTierWaypoint();
                if (worldWaypoint > 0)
                {
                    waypoint.CurrentWaypoint = Math.Max(waypoint.CurrentWaypoint, worldWaypoint);
                    waypoint.MaxWaypoint = Math.Max(waypoint.MaxWaypoint, worldWaypoint + 1);
                    waypoint.HighestWaypointCleared = Math.Max(waypoint.HighestWaypointCleared ?? 0, worldWaypoint);
                    if (duration > TimeSpan.Zero && (waypoint.HighestWaypointBestClearTime == null || duration < waypoint.HighestWaypointBestClearTime))
                        waypoint.HighestWaypointBestClearTime = duration;
                }
            }
            if (depth > 0)
            {
                SetAttribute(data, AttrHelheimDepth, depth);
                SetAttribute(data, AttrMaxHelheimDepth, Math.Max(GetAttribute(data, AttrMaxHelheimDepth) ?? 0, depth));
            }
            c.Data = Pack(data);

            // The first character ever to reach a given world level gets a global
            // announcement. Keyed per game mode so Season/Hardcore have separate races.
            if (worldTier > 0 && worldWaypoint > 0)
            {
                var key = $"{(int)header.GameMode}|{worldTier}|{worldWaypoint}";
                if (!s.WorldFirsts.ContainsKey(key))
                {
                    s.WorldFirsts[key] = characterId;
                    return new WorldAnnouncement("first", (int)header.GameMode, worldTier, worldWaypoint,
                        header.DisplayName ?? "Someone", characterId);
                }
            }
            return null;
        });

    /// <summary>Signals a hardcore character's death; returns an announcement when the
    /// character really is hardcore (normal deaths are not public).</summary>
    public WorldAnnouncement RegisterHardcoreDeath(Guid owner, Guid characterId)
        => Change(s =>
        {
            var c = Owned(s, owner, characterId);
            var header = Unpack<CharacterHeaderDto>(c.Header);
            if (header.GameMode is not (SharedNet.Constants.Game.GameMode.NormalHardcore
                or SharedNet.Constants.Game.GameMode.SeasonHardcore
                or SharedNet.Constants.Game.GameMode.ChallengeHardcore)) return null;
            return new WorldAnnouncement("hardcore", (int)header.GameMode, 0, 0,
                header.DisplayName ?? "Someone", characterId);
        });

    /// <summary>Copies the authoritative progress back into a character's serialized data
    /// immediately before it is handed to the client (so a relogin sees the latest values).</summary>
    public SerializedCharacterData.SerializedData ProjectProgress(Guid owner, Guid id)
        => Change(s =>
        {
            var c = Owned(s, owner, id);
            var data = Unpack<SerializedCharacterData.SerializedData>(c.Data);
            var header = Unpack<CharacterHeaderDto>(c.Header);
            header.Level = Progression.LevelForExperience(c.Experience);
            c.Header = Pack(header);
            SetAttribute(data, AttrLevel, header.Level);
            SetAttribute(data, AttrExperience, c.Experience);
            c.Data = Pack(data);
            return data;
        });

    public bool OwnsCharacter(Guid owner, Guid id)
    {
        lock (gate) return state.Characters.TryGetValue(id, out var c) && c.Owner == owner;
    }

    /// <summary>Recomputes every character's header level from its persisted experience.
    /// Run once at startup so snapshots written before the level curve existed are fixed.</summary>
    public void RecalculateLevels() => Change(s =>
    {
        foreach (var c in s.Characters.Values)
        {
            if (!c.HasRealtimeProgress) continue;
            var header = Unpack<CharacterHeaderDto>(c.Header);
            header.Level = Progression.LevelForExperience(c.Experience);
            c.Header = Pack(header);
        }
        return true;
    });

    public void Dispose() => lease.Dispose();
}
