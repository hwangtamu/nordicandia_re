using System.Security.Cryptography;
using System.Text.Json;
using Game;
using Grpc.Core;
using MessagePack;
using Nordicandia.Server.WebApi;
using Nordicandia.Simulation;
using SharedNet.Api;
using SharedNet.Constants;
using SharedNet.Dto;

namespace Nordicandia.Server.State;

/// <summary>One power (passive-skill) entry in a client snapshot.</summary>
public sealed class PowerSyncEntry
{
    public int PowerHashSafe { get; set; }
    public double Rank { get; set; }
    public int TrainingStart { get; set; }
    public int TrainingEnd { get; set; }
}

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

    /// <summary>Durable record of one processed web command, so retries replay the original
    /// result and commands outside the retained window can be rejected as stale.</summary>
    public sealed class CommandRecord
    {
        public string CommandId { get; set; } = string.Empty;
        public long ExpectedVersion { get; set; }
        public bool Applied { get; set; }
        public string Reason { get; set; } = string.Empty;
        // Server-assigned version at which this command was processed. Unlike
        // <see cref="ExpectedVersion"/> this is controlled by the server and is strictly
        // non-decreasing, so it is a sound retry-window boundary (R4/P2).
        public long ProcessedVersion { get; set; }
    }

    /// <summary>How many recent commands are retained per character for idempotent replay.</summary>
    public const int MaxCommandLog = 256;

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
        // v1.9.3 consumes slot capacity from character attributes, not account fields.
        public bool SlotCapacitiesMigrated { get; set; }
        // Aesir blessing expiries keyed by AesirOfferingTypes (1=Odin, 2=Tyr, 3=Frigg, 4=Thor).
        public Dictionary<int, DateTime> Blessings { get; set; } = new();
        // Unlocked achievement names (UnlockAchievement RPC). HashSet for idempotency.
        public HashSet<string> Achievements { get; set; } = new();
        // Allocated skill-tree mastery ranks, keyed by PowerMasteries IntegerId.
        public Dictionary<int, int> MasteryRanks { get; set; } = new();
        // Recent processed web commands, oldest first. Bounded by MaxCommandLog.
        public List<CommandRecord> CommandLog { get; set; } = new();
        // Persistent, monotonically increasing combat version. Restored into the in-memory
        // CombatInstance so the retained command log's boundary cannot regress across a
        // server restart (P1).
        public long CombatVersion { get; set; }
        // Server-controlled stale boundary. Raised only when a command record is evicted, so
        // a command whose id is gone *and* whose expected version is below the floor is
        // reliably rejected instead of re-executed (R4/P2), while genuine commands that still
        // share a recent expected version keep the lenient retry window.
        public long StaleFloorVersion { get; set; }
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

    // Stack size/max for stackable items, stored on the item's own attribute map at the Item
    // origin (verified from a live character save: e.g. Iron {18:1000, 19:6}; gear has neither).
    private const int ItemMaxStackAttributeId = 18;
    private const int ItemStackAttributeId = 19;

    private static double? GetItemAttribute(SerializedItem item, SharedNet.Constants.Game.AttributeOrigin origin, int id)
    {
        if (item?.Attributes?.Values != null && item.Attributes.Values.TryGetValue(origin, out var map)
            && map != null && map.TryGetValue(id, out var value)) return value.ValueD;
        return null;
    }

    private static void SetItemAttribute(SerializedItem item, SharedNet.Constants.Game.AttributeOrigin origin, int id, double value)
    {
        item.Attributes ??= new SerializedAttributes { Values = new(), MultiplicativeValues = new() };
        item.Attributes.Values ??= new();
        if (!item.Attributes.Values.TryGetValue(origin, out var map) || map == null)
            item.Attributes.Values[origin] = map = new Dictionary<int, GameAttributeValue>();
        map[id] = new GameAttributeValue { Value = (int)value, ValueD = value };
    }
    // Verified on Android 1.9.3: GameAttributeMap.get_Item at 0x2A86F50;
    // SkillGrid writes these ids at Character origin. SkillSlotRules caps are 6/3/3,
    // StashRules.CanExpandPotionSlots caps at 3. Legacy account defaults were wrong.
    private static void SyncSlotCapacities(SavedCharacter c, SerializedCharacterData.SerializedData data,
        SerializedPlayerGameModeAccountData account)
    {
        int Capacity(int id, int initial, int maximum, int recorded, int legacyInitial)
        {
            var saved = (int)(GetAttribute(data, id) ?? initial);
            var count = c.SlotCapacitiesMigrated ? Math.Max(saved, recorded)
                : Math.Max(saved, initial + Math.Max(0L, (long)recorded - legacyInitial));
            var value = (int)Math.Clamp(count, initial, maximum);
            SetAttribute(data, id, value);
            return value;
        }
        account.NumActiveSkillSlots = Capacity(45, 4, 6, account.NumActiveSkillSlots, 3);
        account.NumPassiveSkillSlots = Capacity(56, 1, 3, account.NumPassiveSkillSlots, 3);
        account.NumPassiveTrainingSlots = Capacity(49, 1, 3, account.NumPassiveTrainingSlots, 0);
        account.NumPotionSlots = Capacity(44, 2, 3, account.NumPotionSlots, 2);
        c.SlotCapacitiesMigrated = true;
        c.GameModeAccount = Pack(account);
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

    /// <summary>True when the credential for <paramref name="identity"/> belongs to this user.</summary>
    public bool CredentialBelongsTo(string identity, Guid userId)
    {
        lock (gate)
            return state.Credentials.TryGetValue(identity, out var c) && c.UserId == userId;
    }

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
            return Unpack<UserDto>(Pack(state.Users.Values.FirstOrDefault(u => u.UserId == session.UserId)));
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
            // Seed the client's global Base_* attribute defaults so the recovered attribute
            // synthesis engine has its inputs. Any client-provided value wins.
            foreach (var (attrId, value) in CharacterBaseline.BaseAttributes)
                if (GetAttribute(data, attrId) is null) SetAttribute(data, attrId, value);
            var isSeason = IsSeasonMode(req.CharacterGameMode);
            if (isSeason) { EnsureSeasonExperienceBuff(data); ApplySeasonExperienceBonus(data); }
            var experience = GetAttribute(data, AttrExperience) ?? 0;
            header.Level = Progression.LevelForExperience(experience);
            SetAttribute(data, AttrLevel, header.Level);
            var account = Defaults.Create<SerializedPlayerGameModeAccountData>();
            account.GameMode = req.CharacterGameMode; account.NumStashPages = 1; account.NumPrivateStashPages = 1;
            account.NumActiveSkillSlots = (int)(GetAttribute(data, 45) ?? 4);
            account.NumPassiveSkillSlots = (int)(GetAttribute(data, 56) ?? 1);
            account.NumPassiveTrainingSlots = (int)(GetAttribute(data, 49) ?? 1);
            account.NumPotionSlots = (int)(GetAttribute(data, 44) ?? 2);
            s.Characters.Add(id, new SavedCharacter { Owner = owner, Header = Pack(header), Data = Pack(data), GameModeAccount = Pack(account),
                Experience = experience, SeasonBonusApplied = isSeason, SlotCapacitiesMigrated = true });
            return header;
        });
    }
    /// <summary>
    /// The client measures its own offline window (IdleProgress.LastActiveEpoch vs now)
    /// and posts the reward it wants applied. The generated service used to answer with a
    /// default (all-zero) response, which made the client raise its generic "Error" dialog
    /// on claim and left the character with an overflowing experience bar but a stuck
    /// level. Apply the delta, recompute the level from the authoritative experience and
    /// answer with the final values.
    /// </summary>
    public ClaimCharacterOfflineRewardsResponse ClaimOfflineRewards(Guid owner, ClaimCharacterOfflineRewardsRequest req) => Change(s =>
    {
        var character = Owned(s, owner, req.CharacterId);
        var gained = double.IsNaN(req.ExperienceGained) || req.ExperienceGained < 0 ? 0 : req.ExperienceGained;
        var levels = double.IsNaN(req.LevelsGained) || req.LevelsGained < 0 ? 0 : req.LevelsGained;

        var data = Unpack<SerializedCharacterData.SerializedData>(character.Data);
        var experience = (GetAttribute(data, AttrExperience) ?? 0) + gained;
        var level = Progression.LevelForExperience(experience);
        SetAttribute(data, AttrExperience, experience);
        SetAttribute(data, AttrLevel, level);
        SetLastActiveEpoch(data);
        character.Data = Pack(data);
        character.Experience = experience;

        var header = Unpack<CharacterHeaderDto>(character.Header);
        header.Level = level;
        character.Header = Pack(header);

        if (req.FromAdReward && req.OpalCost > 0)
            character.Opals -= Math.Clamp(req.OpalCost, 0, character.Opals);

        Console.WriteLine($"[OFFLINE] character={req.CharacterId} +exp={gained:F1} level={level} opals={character.Opals}");
        return new ClaimCharacterOfflineRewardsResponse
        {
            FinalExperienceGained = gained,
            FinalLevelsGained = levels,
            NewOpals = character.Opals,
        };
    });
    public EnterGameWithCharacterResponse Enter(Guid owner, Guid id) => Change(s => {
        var character = Owned(s, owner, id);
        var header = Unpack<CharacterHeaderDto>(character.Header); header.LastLogin = Now;
        header.Level = Progression.LevelForExperience(character.Experience);
        character.Header = Pack(header);
        var data = Unpack<SerializedCharacterData.SerializedData>(character.Data);
        SyncSlotCapacities(character, data, Unpack<SerializedPlayerGameModeAccountData>(character.GameModeAccount));
        SetAttribute(data, AttrLevel, header.Level);
        SetAttribute(data, AttrExperience, character.Experience);
        EnsureBlessingBuffs(character, data);
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

    /// <summary>Combat numbers the client reports with every realtime metadata sync
    /// (CharacterMetadataPayload). Offense/Defense/Recovery are the client's current totals;
    /// kills/loot are deltas since the previous sync.</summary>
    public readonly record struct CombatSnapshot(double Offense, double Defense, double Recovery, int MonsterKills, int ItemsLooted);

    // Generous ceilings: they only reject corrupt/absurd client values, not real builds.
    private const double MaxCombatStat = 1e15;
    private const int MaxCountDelta = 1_000_000;

    private static bool ValidStat(double v) => !double.IsNaN(v) && !double.IsInfinity(v) && v > 0 && v < MaxCombatStat;

    /// <summary>Persists reported combat numbers into SerializedData.CombatStats, which the
    /// inspect window (CharacterInspectionDto) reads. A zero/invalid stat means "not
    /// reported in this message" and keeps the stored value.</summary>
    private static void ApplyCombat(SerializedCharacterData.SerializedData data, CombatSnapshot combat)
    {
        var cs = data.CombatStats ??= new SerializedCharacterData.SerializedCombatStats();
        if (ValidStat(combat.Offense)) cs.Offense = combat.Offense;
        if (ValidStat(combat.Defense)) cs.Defense = combat.Defense;
        if (ValidStat(combat.Recovery)) cs.Recovery = combat.Recovery;
        if (combat.MonsterKills is > 0 and <= MaxCountDelta)
            cs.MonsterKills += combat.MonsterKills;
        if (combat.ItemsLooted is > 0 and <= MaxCountDelta)
            cs.ItemsFound = (int)Math.Min(int.MaxValue, (long)cs.ItemsFound + combat.ItemsLooted);
    }

    public RealtimeProgress SaveRealtimeProgress(Guid owner, Guid id, double experience, int silver, int opals,
        CombatSnapshot? combat = null, long combatVersion = 0)
        => Change(s =>
        {
            var c = Owned(s, owner, id);
            c.HasRealtimeProgress = true;
            // Monotonic: never let a stale flush lower the persisted combat version.
            if (combatVersion > c.CombatVersion) c.CombatVersion = combatVersion;
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
            if (combat is { } snapshot) ApplyCombat(data, snapshot);
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
                var (tier, waypoint) = HighestCompletedWorld(data);
                return new CharacterStanding(c.Owner, h.CharacterId, h.DisplayName, h.Level, c.Experience, c.Silver,
                    c.Opals, tier, waypoint, h.GameMode, h.Class, h.Race, h.LastLogin ?? DateTime.UtcNow);
            }).ToList();
    }

    /// <summary>Highest completed world as a (tier, waypoint) pair taken from the same
    /// tier entry. Pairing the highest unlocked tier with the max waypoint across all
    /// tiers produced phantom combinations like "3-10" for a character that had just
    /// entered 3-1 (tier 3 unlocked, but the max cleared waypoint 10 came from tier 2).</summary>
    private static (int Tier, int Waypoint) HighestCompletedWorld(SerializedCharacterData.SerializedData data)
    {
        var tier = (int)(GetAttribute(data, AttrWorldTierUnlocked) ?? 0);
        var map = data.Waypoints?.WaypointMap;
        if (map == null || map.Count == 0) return (tier, 0);
        if (!map.TryGetValue(tier, out var entry) || entry == null)
        {
            tier = map.Keys.Max();
            entry = map[tier];
        }
        return (tier, entry?.HighestWaypointCleared ?? entry?.MaxWaypoint ?? 0);
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
            (tier, var waypoint) = HighestCompletedWorld(data);
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

    /// <summary>Consumes stack(s) of an inventory item. The client journals item movement
    /// through ItemOperation but uses a separate ConsumeItem RPC, which used to be an
    /// unimplemented stub returning 0. Because the client aborts a "use" when the server
    /// reports 0 consumed, this broke every consume-gated action (e.g. the Niflheim portal)
    /// and left consumables un-deducted server-side. Returns the actually-consumed amount and
    /// whether the stack(s) were exhausted (item removed).</summary>
    public (int Consumed, bool AllStacks) ConsumeItem(Guid owner, Guid characterId, Guid itemId, int amount)
        => Change(s =>
        {
            var c = Owned(s, owner, characterId);
            var data = Unpack<SerializedCharacterData.SerializedData>(c.Data);
            data.Items ??= new SerializedItems();
            data.Items.Items ??= new List<SerializedItem>();
            var item = data.Items.Items.FirstOrDefault(i => i != null && i.Id == itemId);
            if (item == null) return (0, false);
            var stack = (int)(GetItemAttribute(item, SharedNet.Constants.Game.AttributeOrigin.Item, ItemStackAttributeId) ?? 1);
            if (stack <= 0) stack = 1;
            var consumed = amount > 0 ? Math.Min(amount, stack) : stack;
            var remaining = stack - consumed;
            if (remaining <= 0) data.Items.Items.Remove(item);
            else SetItemAttribute(item, SharedNet.Constants.Game.AttributeOrigin.Item, ItemStackAttributeId, remaining);
            c.Data = Pack(data);
            Console.WriteLine($"[CONSUME] character={characterId} item={itemId} requested={amount} consumed={consumed} remaining={Math.Max(0, remaining)}");
            return (consumed, remaining <= 0);
        });

    /// <summary>Aggregated attribute map for a character: Character-origin attributes plus each
    /// item's Item-origin attributes (id -> summed value). Feeds the attribute synthesis engine.</summary>
    public Dictionary<int, double> GetAttributeMap(Guid owner, Guid id)
    {
        lock (gate)
        {
            var result = new Dictionary<int, double>();
            var data = Unpack<SerializedCharacterData.SerializedData>(Owned(state, owner, id).Data);
            void Add(SerializedAttributes attributes)
            {
                if (attributes?.Values == null) return;
                foreach (var map in attributes.Values.Values)
                {
                    if (map == null) continue;
                    foreach (var (attrId, value) in map)
                        result[attrId] = result.GetValueOrDefault(attrId) + value.ValueD;
                }
            }
            var setCounts = new Dictionary<int, int>();
            Add(data?.Attributes);
            if (data?.Items?.Items != null)
                foreach (var item in data.Items.Items)
                {
                    // Only equipped items contribute (slots 0..13); bag items must not.
                    if (item == null || (int)item.Slot is < 0 or > 13) continue;
                    Add(item.Attributes);
                    if (item.Affixes != null)
                        foreach (var affix in item.Affixes) Add(affix?.Attributes);
                    if (item.Attributes?.Values != null
                        && item.Attributes.Values.TryGetValue(SharedNet.Constants.Game.AttributeOrigin.Item, out var map)
                        && map != null && map.TryGetValue(LootTable.AttrSetId, out var setId))
                    {
                        var setIdValue = (int)setId.ValueD;
                        setCounts[setIdValue] = setCounts.GetValueOrDefault(setIdValue) + 1;
                    }
                }
            // Item-set bonuses: a breakpoint grants its attributes once enough pieces are equipped.
            foreach (var (setId, count) in setCounts)
                foreach (var (attrId, value) in SetCatalog.ActiveBonuses(setId, count))
                    result[attrId] = result.GetValueOrDefault(attrId) + value;
            return result;
        }
    }

    /// <summary>Moves the given owned items into the requested slots (used by the web NPC windows,
    /// which place items into the Blacksmith/YourTrade slots before the operation runs).</summary>
    public void SetItemSlots(Guid owner, Guid characterId, IReadOnlyDictionary<Guid, SharedNet.Constants.Game.ItemSlotTypes> slots) => Change(s =>
    {
        var c = Owned(s, owner, characterId);
        var data = Unpack<SerializedCharacterData.SerializedData>(c.Data);
        if (data?.Items?.Items == null || slots == null) return false;
        foreach (var item in data.Items.Items)
            if (item != null && slots.TryGetValue(item.Id, out var slot)) item.Slot = slot;
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

    // Recovered item integer ids (gamedata_decrypted/Items.json).
    private const int ItemIronId = 63;
    private const int ItemSteelId = 589;

    /// <summary>Essence->steel smelting coefficient by rarity, recovered from
    /// CraftingUtils.GetEssenceToSteelSmeltingValueFromRarity (0x02C834FC / its cctor).</summary>
    private static double EssenceToSteel(int rarity) => rarity switch
    {
        2 => 0.05, 3 => 0.1, 4 => 0.15, 5 => 0.2, 6 => 0.25,
        7 => 0.5, 8 => 1.0, 9 => 3.0, >= 10 => 10.0, _ => 0.0,
    };

    private static SerializedItem CloneItem(SerializedItem item) => Unpack<SerializedItem>(Pack(item));

    /// <summary>Creates a stackable material stack (Iron/Steel/...). The web slice does not carry
    /// the client's item definitions, so these reuse the persisted item shape with the client's
    /// stack attributes (18 max stack, 19 stack) and the recovered item integer ids.</summary>
    private static SerializedItem CreateMaterial(int definitionIntegerId, string name, int count)
    {
        var item = new SerializedItem
        {
            Id = Guid.NewGuid(), Name = name, Slot = SharedNet.Constants.Game.ItemSlotTypes.Inventory,
            DefinitionIntegerId = definitionIntegerId, BaseRarity = 0,
            Location = new SerializedItemInventoryLocation { Page = 1, Row = 0, Column = 0 },
        };
        SetItemAttribute(item, SharedNet.Constants.Game.AttributeOrigin.Item, ItemMaxStackAttributeId, 1000);
        SetItemAttribute(item, SharedNet.Constants.Game.AttributeOrigin.Item, ItemStackAttributeId, count);
        return item;
    }

    /// <summary>Smelts the items the client placed in the Blacksmith source slot: the output is the
    /// recovered essence->steel sum (floored, at least 1) and the sources are consumed.</summary>
    public (bool Successful, SerializedItems SourceItems, SerializedItems Result) SmeltItems(Guid owner, Guid characterId) => Change(s =>
    {
        var c = Owned(s, owner, characterId);
        var data = Unpack<SerializedCharacterData.SerializedData>(c.Data);
        data.Items ??= new SerializedItems { Items = new List<SerializedItem>() };
        data.Items.Items ??= new List<SerializedItem>();
        var sources = data.Items.Items.Where(i => i != null && i.Slot == SharedNet.Constants.Game.ItemSlotTypes.Blacksmith_SourceItem).ToList();
        var sourceSnapshot = new SerializedItems { Items = sources.Select(CloneItem).ToList() };
        if (sources.Count == 0) return (false, sourceSnapshot, new SerializedItems { Items = new List<SerializedItem>() });

        var output = Math.Max(1, (int)Math.Floor(sources.Sum(i => EssenceToSteel((int)i.BaseRarity))));
        data.Items.Items.RemoveAll(i => i != null && i.Slot == SharedNet.Constants.Game.ItemSlotTypes.Blacksmith_SourceItem);
        var steel = CreateMaterial(ItemSteelId, "Steel", output);
        data.Items.Items.Add(steel);
        c.Data = Pack(data);
        return (true, new SerializedItems { Items = new List<SerializedItem>() }, new SerializedItems { Items = new List<SerializedItem> { steel } });
    });

    /// <summary>Disassembles the Blacksmith source items. The client's rule is "not unique and not a
    /// set item" (web items carry neither), so every source qualifies; the source is consumed and
    /// Iron is granted. The material amount is Provisional until the client's disassemble output
    /// formula is recovered.</summary>
    public (bool Successful, SerializedItems SourceItems, SerializedItems Result) DisassembleItems(Guid owner, Guid characterId) => Change(s =>
    {
        var c = Owned(s, owner, characterId);
        var data = Unpack<SerializedCharacterData.SerializedData>(c.Data);
        data.Items ??= new SerializedItems { Items = new List<SerializedItem>() };
        data.Items.Items ??= new List<SerializedItem>();
        var sources = data.Items.Items.Where(i => i != null && i.Slot == SharedNet.Constants.Game.ItemSlotTypes.Blacksmith_SourceItem).ToList();
        var sourceSnapshot = new SerializedItems { Items = sources.Select(CloneItem).ToList() };
        if (sources.Count == 0) return (false, sourceSnapshot, new SerializedItems { Items = new List<SerializedItem>() });

        var output = Math.Max(1, (int)Math.Floor(sources.Sum(i => 1 + (int)i.BaseRarity * 0.5)));
        data.Items.Items.RemoveAll(i => i != null && i.Slot == SharedNet.Constants.Game.ItemSlotTypes.Blacksmith_SourceItem);
        var iron = CreateMaterial(ItemIronId, "Iron", output);
        data.Items.Items.Add(iron);
        c.Data = Pack(data);
        return (true, new SerializedItems { Items = new List<SerializedItem>() }, new SerializedItems { Items = new List<SerializedItem> { iron } });
    });

    /// <summary>Merchant barter (TradeWithMerchant): consumes the items the client placed in the
    /// YourTrade slot (ItemSlotTypes.YourTrade = 31) and grants the requested product stacks. The
    /// client owns the value maths (PlayerOfferValue/TradePercentage); the server only requires a
    /// non-empty offer and never trusts an unbounded stack count.</summary>
    public (bool Applied, SerializedItems YourOfferItems, SerializedItems InventoryItems) TradeWithMerchant(
        Guid owner, Guid characterId, int productDefinitionIntegerId, string productName, int stacks) => Change(s =>
    {
        var c = Owned(s, owner, characterId);
        var data = Unpack<SerializedCharacterData.SerializedData>(c.Data);
        data.Items ??= new SerializedItems { Items = new List<SerializedItem>() };
        data.Items.Items ??= new List<SerializedItem>();
        var offer = data.Items.Items
            .Where(i => i != null && i.Slot == SharedNet.Constants.Game.ItemSlotTypes.YourTrade).ToList();
        if (offer.Count == 0)
            return (false, new SerializedItems { Items = new List<SerializedItem>() },
                new SerializedItems { Items = data.Items.Items.ToList() });

        data.Items.Items.RemoveAll(i => i != null && i.Slot == SharedNet.Constants.Game.ItemSlotTypes.YourTrade);
        var product = CreateMaterial(productDefinitionIntegerId, productName, Math.Clamp(stacks, 1, 100));
        data.Items.Items.Add(product);
        c.Data = Pack(data);
        return (true, new SerializedItems { Items = offer.Select(CloneItem).ToList() },
            new SerializedItems { Items = data.Items.Items.ToList() });
    });

    /// <summary>Allocated mastery ranks for a character (mastery integerId -> rank).</summary>
    public Dictionary<int, int> GetMasteryRanks(Guid owner, Guid id)
    {
        lock (gate)
        {
            var c = Owned(state, owner, id);
            return c.MasteryRanks == null ? new Dictionary<int, int>() : new Dictionary<int, int>(c.MasteryRanks);
        }
    }

    /// <summary>Sets a mastery rank (0 removes it). Returns the stored rank.</summary>
    public int SetMasteryRank(Guid owner, Guid id, int masteryId, int rank) => Change(s =>
    {
        var c = Owned(s, owner, id);
        c.MasteryRanks ??= new Dictionary<int, int>();
        if (rank <= 0) c.MasteryRanks.Remove(masteryId);
        else c.MasteryRanks[masteryId] = rank;
        return Math.Max(0, rank);
    });

    /// <summary>Total mastery points currently spent by a character.</summary>
    public int MasteryPointsSpent(Guid owner, Guid id)
    {
        lock (gate)
        {
            var c = Owned(state, owner, id);
            return c.MasteryRanks?.Values.Sum() ?? 0;
        }
    }

    /// <summary>Looks up a previous web command. <c>OldestBoundaryVersion</c> is the persisted
    /// stale floor: a command whose id is gone and whose expected version is below it is
    /// outside the retry window and must be rejected rather than re-executed (R4/P2).</summary>
    public (CommandRecord Found, long OldestBoundaryVersion) LookupCommand(Guid owner, Guid id, string commandId)
    {
        lock (gate)
        {
            var c = Owned(state, owner, id);
            c.CommandLog ??= new List<CommandRecord>();
            var found = string.IsNullOrEmpty(commandId)
                ? null
                : c.CommandLog.FirstOrDefault(r => r.CommandId == commandId);
            return (found, c.StaleFloorVersion);
        }
    }

    // The version to record as stale when a command record is evicted. Legacy records
    // (written before ProcessedVersion existed) fall back to their expected version.
    private static long RetireVersionOf(CommandRecord record)
        => record.ProcessedVersion > 0 ? record.ProcessedVersion : record.ExpectedVersion;

    /// <summary>Appends a processed command, advances the persisted combat version, and trims
    /// the log to the retained window. Evicted records raise the stale floor (R4/P2).</summary>
    public void AppendCommand(Guid owner, Guid id, CommandRecord record, long combatVersion = 0) => Change(s =>
    {
        var c = Owned(s, owner, id);
        c.CommandLog ??= new List<CommandRecord>();
        if (combatVersion > c.CombatVersion) c.CombatVersion = combatVersion;
        c.CommandLog.Add(record);
        while (c.CommandLog.Count > MaxCommandLog)
        {
            var evicted = c.CommandLog[0];
            var retire = RetireVersionOf(evicted);
            if (retire > c.StaleFloorVersion) c.StaleFloorVersion = retire;
            c.CommandLog.RemoveAt(0);
        }
        return true;
    });

    /// <summary>The persisted, monotonically increasing combat version used to seed a rebuilt
    /// <c>CombatInstance</c> on restart.</summary>
    public long GetCombatVersion(Guid owner, Guid id)
    {
        lock (gate)
        {
            var c = Owned(state, owner, id);
            return c.CombatVersion;
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
        // Client durations for Small/Medium/Large/ExtraLarge: 10m / 30m / 1h / 4h.
        var duration = offeringSize switch
        {
            1 => TimeSpan.FromMinutes(10),
            2 => TimeSpan.FromMinutes(30),
            3 => TimeSpan.FromHours(1),
            4 => TimeSpan.FromHours(4),
            _ => TimeSpan.FromMinutes(10),
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

    /// <summary>Match FinalizeAesirBuffOffline/Serialize on Android 1.9.3. Attribute
    /// 166 is Buff_Duration; 465 is the scripted Buff_Duration_Total and cannot be
    /// assigned directly. All values belong to the buff's own attribute map.</summary>
    private static SerializedCharacterData.SerializedBuff CreateBlessingBuff(int definitionIntegerId, double seconds)
    {
        seconds = Math.Max(0, seconds);
        return new SerializedCharacterData.SerializedBuff
        {
            DefinitionIntegerId = definitionIntegerId,
            IsCharacterContext = false,
            Attributes = new SerializedAttributes
            {
                Values = new()
                {
                    [SharedNet.Constants.Game.AttributeOrigin.Buff] = new Dictionary<int, GameAttributeValue>
                    {
                        [166] = new GameAttributeValue { ValueD = seconds }, // Buff_Duration (seconds remaining)
                        [279] = new GameAttributeValue { ValueD = 1 },       // Buff_Context_Hash
                        [289] = new GameAttributeValue { ValueD = 6 },       // Buff_Context_Type
                    },
                },
                MultiplicativeValues = new(),
            },
        };
    }

    /// <summary>Returns the four Aesir blessing buffs that have not expired, pruning the
    /// expired ones. The buff carries the definition id plus the remaining duration.</summary>
    public (SerializedCharacterData.SerializedBuff Odin, SerializedCharacterData.SerializedBuff Tyr,
        SerializedCharacterData.SerializedBuff Frigg, SerializedCharacterData.SerializedBuff Thor)
        GetActiveBlessings(Guid owner, Guid characterId) => Change(s =>
        {
            var c = Owned(s, owner, characterId);
            c.Blessings ??= new();
            var now = Now;
            foreach (var expired in c.Blessings.Where(kv => kv.Value <= now).Select(kv => kv.Key).ToList())
                c.Blessings.Remove(expired);
            SerializedCharacterData.SerializedBuff Buff(int type) => c.Blessings.TryGetValue(type, out var expiry) && BlessingBuffIds.TryGetValue(type, out var id)
                ? CreateBlessingBuff(id, (expiry - now).TotalSeconds)
                : null;
            return (Buff(1), Buff(2), Buff(3), Buff(4));
        });

    /// <summary>The active Aesir blessing buffs as a list, for the realtime push. The client
    /// applies buffs from <c>SerializedData.Buffs</c> on load but races the buff bar's start,
    /// so (like the SeasonBuff) we also push them when the socket opens.</summary>
    public List<SerializedCharacterData.SerializedBuff> ActiveBlessingBuffs(Guid owner, Guid characterId) => Change(s =>
    {
        var c = Owned(s, owner, characterId);
        c.Blessings ??= new();
        var now = Now;
        foreach (var expired in c.Blessings.Where(kv => kv.Value <= now).Select(kv => kv.Key).ToList())
            c.Blessings.Remove(expired);
        var list = new List<SerializedCharacterData.SerializedBuff>();
        foreach (var type in c.Blessings.Keys.Where(BlessingBuffIds.ContainsKey))
            list.Add(CreateBlessingBuff(BlessingBuffIds[type], (c.Blessings[type] - now).TotalSeconds));
        return list;
    });

    /// <summary>Mirrors the active Aesir blessings onto the character's serialized buffs so the
    /// client re-applies them on login (the GetCurrentBlessings response alone does not survive a
    /// relogin). Expired blessings are pruned; existing blessing entries are replaced, so repeated
    /// logins neither duplicate buffs nor extend their stored expiry.</summary>
    private void EnsureBlessingBuffs(SavedCharacter c, SerializedCharacterData.SerializedData data)
    {
        data.Buffs ??= new SerializedCharacterData.SerializedBuffs();
        data.Buffs.Buffs ??= new List<SerializedCharacterData.SerializedBuff>();
        c.Blessings ??= new();
        var now = Now;
        foreach (var expired in c.Blessings.Where(kv => kv.Value <= now).Select(kv => kv.Key).ToList())
            c.Blessings.Remove(expired);
        data.Buffs.Buffs.RemoveAll(b => b != null && BlessingBuffIds.ContainsValue(b.DefinitionIntegerId));
        foreach (var type in c.Blessings.Keys.Where(BlessingBuffIds.ContainsKey))
            data.Buffs.Buffs.Add(CreateBlessingBuff(BlessingBuffIds[type], (c.Blessings[type] - now).TotalSeconds));
    }

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

    /// <summary>Persists a combat pet's locally-earned level/experience. The shipped
    /// client only mutates the pet in memory, so without this the pet reset to the
    /// server's stored level on the next login.</summary>
    public void SaveCombatPetProgress(Guid owner, Guid characterId, int combatPetDefinitionIntegerId,
        double level, double experience) => Change(s =>
    {
        var c = Owned(s, owner, characterId);
        var data = Unpack<SerializedCharacterData.SerializedData>(c.Data);
        var pets = EnsureCombatPets(data);
        var pet = pets.CombatPets.FirstOrDefault(p => p != null && p.DefinitionIntegerId == combatPetDefinitionIntegerId);
        if (pet == null)
        {
            pet = new SerializedCharacterData.SerializedCombatPet
            {
                DefinitionIntegerId = combatPetDefinitionIntegerId,
                IsAlive = true,
            };
            pets.CombatPets.Add(pet);
        }
        if (level > 0 && !double.IsNaN(level) && !double.IsInfinity(level)) pet.Level = level;
        if (experience >= 0 && !double.IsNaN(experience) && !double.IsInfinity(experience)) pet.Experience = experience;
        c.Data = Pack(data);
        Console.WriteLine($"[COMPET] character={characterId} pet={combatPetDefinitionIntegerId} level={pet.Level:F0} exp={pet.Experience:F0}");
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

    /// <summary>
    /// Unlock an achievement for a character. Idempotent: re-unlocking an already
    /// unlocked achievement is a no-op. Returns the character's current opal balance.
    /// </summary>
    public int UnlockAchievement(Guid owner, Guid characterId, string achievementName) => Change(s =>
    {
        var c = Owned(s, owner, characterId);
        if (!string.IsNullOrEmpty(achievementName))
        {
            c.Achievements.Add(achievementName);
        }
        return c.Opals;
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

    /// <summary>Set-item merchant: pairs each item the player placed in the YourTrade slot with a
    /// set item of the same equip slot. The generation is deterministic per offered item, so the
    /// offer shown to the client matches the item granted by the trade.</summary>
    public (bool Applied, SerializedItems YourOfferItems, SerializedItems OfferedItems)
        GenerateSetItemMerchantOffers(Guid owner, Guid characterId) => Change(s =>
    {
        var c = Owned(s, owner, characterId);
        var data = Unpack<SerializedCharacterData.SerializedData>(c.Data);
        var items = data.Items?.Items ?? new List<SerializedItem>();
        var offers = items
            .Where(i => i != null && i.Slot == SharedNet.Constants.Game.ItemSlotTypes.YourTrade).ToList();
        return (offers.Count > 0,
            new SerializedItems { Items = offers.Select(CloneItem).ToList() },
            new SerializedItems { Items = offers.Select(SetOfferFor).ToList() });
    });

    /// <summary>Trades one offered item for its set item (the deterministic offer).</summary>
    public (bool Applied, SerializedItems ResultItem) TradeWithSetItemMerchant(Guid owner, Guid characterId, Guid itemId) => Change(s =>
    {
        var c = Owned(s, owner, characterId);
        var data = Unpack<SerializedCharacterData.SerializedData>(c.Data);
        var items = data.Items?.Items ?? new List<SerializedItem>();
        var offer = items.FirstOrDefault(i => i != null && i.Id == itemId
            && i.Slot == SharedNet.Constants.Game.ItemSlotTypes.YourTrade);
        if (offer == null) return (false, new SerializedItems { Items = new List<SerializedItem>() });
        var setItem = SetOfferFor(offer);
        items.Remove(offer);
        items.Add(setItem);
        c.Data = Pack(data);
        return (true, new SerializedItems { Items = new List<SerializedItem> { setItem } });
    });

    /// <summary>Builds the set item offered for an equipped-slot item (deterministic by item id).</summary>
    private static SerializedItem SetOfferFor(SerializedItem source)
    {
        var slot = LootTable.EquipSlotOf(source);
        var level = (int)(GetItemAttribute(source, SharedNet.Constants.Game.AttributeOrigin.Item, LootTable.AttrRequiredLevel) ?? 1);
        var seed = (ulong)(uint)source.Id.GetHashCode() * 2654435761UL + 1;
        return LootTable.CreateItem(
            new LootDrop(slot, (int)source.BaseRarity, Math.Max(1, level), false, seed),
            ItemCatalog.RarityType.Set);
    }

    // ---- Essence craft -------------------------------------------------------------

    /// <summary>Per-rarity material cost, recovered from HeatingStone.getCostFromRarity: 1 except
    /// 8 -> 2, 9 -> 3, 10 -> 10.</summary>
    private static int CostFromRarity(int rarity) => rarity switch { 8 => 2, 9 => 3, 10 => 10, _ => 1 };

    /// <summary>Consumes <paramref name="amount"/> across the given stacks (attribute 19 holds the
    /// stack size); stacks emptied to zero are removed.</summary>
    private static void ConsumeStacks(List<SerializedItem> items, List<SerializedItem> stacks, int amount)
    {
        var remaining = amount;
        foreach (var stack in stacks)
        {
            if (remaining <= 0) break;
            var current = (int)(GetItemAttribute(stack, SharedNet.Constants.Game.AttributeOrigin.Item, ItemStackAttributeId) ?? 1);
            if (current <= 0) continue;
            var take = Math.Min(current, remaining);
            remaining -= take;
            if (current - take <= 0) items.Remove(stack);
            else SetItemAttribute(stack, SharedNet.Constants.Game.AttributeOrigin.Item, ItemStackAttributeId, current - take);
        }
    }

    /// <summary>
    /// Simplified essence craft. Consumes Iron using the recovered cost formula
    /// (<c>GetNumIronCost = (int)(overheat * getCostFromRarity(rarity) * 0.5)</c>) and rolls the
    /// recovered success chance (<c>GetHighestChanceToSucceed = rarity &lt; 10 ? 0.25 : 1.0</c>).
    /// On success the source items' affixes are merged onto the Blacksmith target item, up to its
    /// rarity's affix capacity. The merge is a documented simplification of the client's affix
    /// pipeline (which uses full item generation); the cost and success formulas are ClientVerified.
    /// </summary>
    public (bool OperationSuccessful, bool Success, SerializedItem Result, SerializedItems SourceItems, int IronConsumed)
        CraftEssenceItem(Guid owner, Guid characterId, int overheatSliderValue) => Change(s =>
    {
        var c = Owned(s, owner, characterId);
        var data = Unpack<SerializedCharacterData.SerializedData>(c.Data);
        data.Items ??= new SerializedItems { Items = new List<SerializedItem>() };
        data.Items.Items ??= new List<SerializedItem>();
        var items = data.Items.Items;
        var target = items.FirstOrDefault(i => i != null && i.Slot == SharedNet.Constants.Game.ItemSlotTypes.Blacksmith_TargetItem);
        var sources = items.Where(i => i != null && i.Slot == SharedNet.Constants.Game.ItemSlotTypes.Blacksmith_SourceItem).ToList();
        var sourceSnapshot = new SerializedItems { Items = sources.Select(CloneItem).ToList() };
        if (target == null || sources.Count == 0)
            return (false, false, null, sourceSnapshot, 0);

        var rarity = (int)target.BaseRarity;
        var ironCost = Math.Max(1, (int)(Math.Max(1, overheatSliderValue) * CostFromRarity(rarity) * 0.5));
        var ironStacks = items.Where(i => i != null && i.DefinitionIntegerId == ItemIronId).ToList();
        var available = ironStacks.Sum(i => (int)(GetItemAttribute(i, SharedNet.Constants.Game.AttributeOrigin.Item, ItemStackAttributeId) ?? 1));
        if (available < ironCost)
            return (false, false, null, sourceSnapshot, 0);
        ConsumeStacks(items, ironStacks, ironCost);

        var rate = rarity < 10 ? 0.25 : 1.0;
        var success = Random.Shared.NextDouble() <= rate;
        if (success)
        {
            // Merge the source items' affixes onto the target: an affix that already exists is
            // upgraded only when the incoming affix is rarer (client OnMergeAffix -> RemoveAffix /
            // ResetRarity / AddAffix); a new affix fills capacity (documented web approximation of
            // GetMergableAffixes' IsPrefixOrSuffix + IsOpenAffix open-slot test).
            target.Affixes ??= new List<SerializedAffix>();
            var capacity = 2 + Math.Clamp(rarity / 3, 0, 4);
            foreach (var source in sources)
                foreach (var affix in (source.Affixes ?? new List<SerializedAffix>()).Where(x => x != null))
                {
                    var existing = target.Affixes.FirstOrDefault(x => x != null && x.DefinitionIntegerId == affix.DefinitionIntegerId);
                    if (existing != null)
                    {
                        if ((int)affix.Rarity > (int)existing.Rarity)
                            target.Affixes[target.Affixes.IndexOf(existing)] = affix;
                        continue;
                    }
                    if (target.Affixes.Count >= capacity) continue;
                    target.Affixes.Add(affix);
                }
            items.RemoveAll(i => i != null && i.Slot == SharedNet.Constants.Game.ItemSlotTypes.Blacksmith_SourceItem);
        }

        c.Data = Pack(data);
        return (true, success, CloneItem(target), sourceSnapshot, ironCost);
    });

    /// <summary>Server attribute id marking a blessed affix (the client stores the bless flag on
    /// the Affix; this reuses the affix's attribute map under a web-only id).</summary>
    private const int AffixBlessedAttributeId = 99010;

    /// <summary>Relic craft (Game.Items.Implementations.RelicOfBlessing): consumes the Blacksmith
    /// source items (relics) and blesses the target item's affixes. The client's InternalCraft calls
    /// <c>Affix.Bless</c> for each affix <c>ShouldBless</c> accepts; here every target affix is blessed
    /// (ShouldBless's fine-grained filter is not yet recovered).</summary>
    public (bool Success, SerializedItems SourceItems, SerializedItem Result) CraftRelicItem(Guid owner, Guid characterId) => Change(s =>
    {
        var c = Owned(s, owner, characterId);
        var data = Unpack<SerializedCharacterData.SerializedData>(c.Data);
        data.Items ??= new SerializedItems { Items = new List<SerializedItem>() };
        data.Items.Items ??= new List<SerializedItem>();
        var items = data.Items.Items;
        var target = items.FirstOrDefault(i => i != null && i.Slot == SharedNet.Constants.Game.ItemSlotTypes.Blacksmith_TargetItem);
        var sources = items.Where(i => i != null && i.Slot == SharedNet.Constants.Game.ItemSlotTypes.Blacksmith_SourceItem).ToList();
        var sourceSnapshot = new SerializedItems { Items = sources.Select(CloneItem).ToList() };
        if (target == null || sources.Count == 0) return (false, sourceSnapshot, null);

        items.RemoveAll(i => i != null && i.Slot == SharedNet.Constants.Game.ItemSlotTypes.Blacksmith_SourceItem);
        target.Affixes ??= new List<SerializedAffix>();
        foreach (var affix in target.Affixes)
        {
            if (affix == null) continue;
            affix.Attributes ??= new SerializedAttributes { Values = new(), MultiplicativeValues = new() };
            affix.Attributes.Values ??= new();
            if (!affix.Attributes.Values.TryGetValue(SharedNet.Constants.Game.AttributeOrigin.Item, out var map) || map == null)
                affix.Attributes.Values[SharedNet.Constants.Game.AttributeOrigin.Item] = map = new Dictionary<int, GameAttributeValue>();
            map[AffixBlessedAttributeId] = new GameAttributeValue { Value = 1, ValueD = 1 };
        }
        c.Data = Pack(data);
        return (true, sourceSnapshot, CloneItem(target));
    });

    private const int ItemTitanSteelId = 592;

    private static int StackOf(SerializedItem item)
        => (int)(GetItemAttribute(item, SharedNet.Constants.Game.AttributeOrigin.Item, ItemStackAttributeId) ?? 1);

    /// <summary>Adds an empty socket to the Blacksmith target item, consuming Titansteel with the
    /// recovered count (<c>GetRequiredTitansteelReagentsForAddingSockets = numExistingSockets + 1</c>).</summary>
    public (bool Success, SerializedItems SourceItems, SerializedItem Result) ItemAddNewSocket(Guid owner, Guid characterId) => Change(s =>
    {
        var c = Owned(s, owner, characterId);
        var data = Unpack<SerializedCharacterData.SerializedData>(c.Data);
        data.Items ??= new SerializedItems { Items = new List<SerializedItem>() };
        data.Items.Items ??= new List<SerializedItem>();
        var items = data.Items.Items;
        var target = items.FirstOrDefault(i => i != null && i.Slot == SharedNet.Constants.Game.ItemSlotTypes.Blacksmith_TargetItem);
        if (target == null) return (false, new SerializedItems { Items = new List<SerializedItem>() }, null);

        target.Sockets ??= new List<SerializedSocket>();
        var existing = target.Sockets.Count(socket => socket != null);
        var cost = existing + 1;
        var titan = items.Where(i => i != null && i.DefinitionIntegerId == ItemTitanSteelId).ToList();
        if (titan.Sum(StackOf) < cost) return (false, new SerializedItems { Items = new List<SerializedItem>() }, CloneItem(target));
        ConsumeStacks(items, titan, cost);
        if (target.Sockets.All(socket => socket != null && socket.SocketedItem != null))
            target.Sockets.Add(new SerializedSocket { SocketIndex = existing });
        c.Data = Pack(data);
        return (true, new SerializedItems { Items = new List<SerializedItem>() }, CloneItem(target));
    });

    /// <summary>Inserts the gem in the Blacksmith source slot into an empty socket on the target
    /// item (SerializedSocket.SocketedItem).</summary>
    public (bool Success, SerializedItems SourceItems, SerializedItem Result) SocketItem(Guid owner, Guid characterId) => Change(s =>
    {
        var c = Owned(s, owner, characterId);
        var data = Unpack<SerializedCharacterData.SerializedData>(c.Data);
        data.Items ??= new SerializedItems { Items = new List<SerializedItem>() };
        data.Items.Items ??= new List<SerializedItem>();
        var items = data.Items.Items;
        var target = items.FirstOrDefault(i => i != null && i.Slot == SharedNet.Constants.Game.ItemSlotTypes.Blacksmith_TargetItem);
        var gem = items.FirstOrDefault(i => i != null && i.Slot == SharedNet.Constants.Game.ItemSlotTypes.Blacksmith_SourceItem);
        if (target == null || gem == null) return (false, new SerializedItems { Items = new List<SerializedItem>() }, null);

        target.Sockets ??= new List<SerializedSocket>();
        var free = target.Sockets.FirstOrDefault(socket => socket != null && socket.SocketedItem == null);
        if (free == null) return (false, new SerializedItems { Items = new List<SerializedItem>() }, CloneItem(target));
        free.SocketedItem = gem;
        items.Remove(gem);
        c.Data = Pack(data);
        return (true, new SerializedItems { Items = new List<SerializedItem> { gem } }, CloneItem(target));
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

    /// <summary>Current attribute allocation: the remaining point pool plus the allocated values
    /// for each of the seven attributes (web attribute panel).</summary>
    public (double Available, double Strength, double Dexterity, double Intelligence, double Vitality,
        double Constitution, double Agility, double Mindpower) GetAttributeAllocation(Guid owner, Guid characterId)
    {
        lock (gate)
        {
            var c = Owned(state, owner, characterId);
            var data = Unpack<SerializedCharacterData.SerializedData>(c.Data);
            var str = GetAttribute(data, AttrStrengthAllocated) ?? 0;
            var dex = GetAttribute(data, AttrDexterityAllocated) ?? 0;
            var intel = GetAttribute(data, AttrIntelligenceAllocated) ?? 0;
            var vit = GetAttribute(data, AttrVitalityAllocated) ?? 0;
            var con = GetAttribute(data, AttrConstitutionAllocated) ?? 0;
            var agi = GetAttribute(data, AttrAgilityAllocated) ?? 0;
            var mind = GetAttribute(data, AttrMindpowerAllocated) ?? 0;
            var level = Unpack<CharacterHeaderDto>(c.Header).Level;
            var totalPoints = Math.Max(0, (level - 1) * 5);
            var available = Math.Max(0, totalPoints - (str + dex + intel + vit + con + agi + mind));
            return (available, str, dex, intel, vit, con, agi, mind);
        }
    }

    /// <summary>Raises the persisted world progression ("area level") when a run completes.
    /// Stores the unlocked tier, the per-tier waypoint record, and (for Helheim) the depth.</summary>
    /// <summary>Persists a skill-bar / passive-tree assignment. The client sends the
    /// power's definition id and the slot index; the wire format the character uses stores
    /// an integer <c>PowerHashSafe</c>, resolved through <see cref="PowerCatalog"/>.</summary>
    public List<CharacterSkillTrainingEntry> ApplySkillAssignment(Guid owner, Guid characterId, IList<CharacterSkillEntry> skills,
        SharedNet.Constants.Game.PowerSlotTypes slotType) => Change(s =>
    {
        var c = Owned(s, owner, characterId);
        var data = Unpack<SerializedCharacterData.SerializedData>(c.Data);
        data.Skills ??= new SerializedCharacterData.SerializedSkills { Skills = new() };
        data.Skills.Skills ??= new List<SerializedCharacterData.SerializedSkill>();
        data.Powers ??= new SerializedCharacterData.SerializedPowers { Powers = new() };
        data.Powers.Powers ??= new List<SerializedCharacterData.SerializedPower>();
        var trainings = new List<CharacterSkillTrainingEntry>();
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
            var power = data.Powers.Powers.FirstOrDefault(p => p != null && p.PowerHashSafe == info.HashSafe);
            if (power == null)
            {
                power = new SerializedCharacterData.SerializedPower
                {
                    PowerHash = info.HashSafe, PowerHashSafe = info.HashSafe, Power_Rank = 1,
                    Power_Masteries = new SerializedCharacterData.SerializedPowerMasteries { Masteries = new() },
                };
                data.Powers.Powers.Add(power);
            }
            // Passive training is time-gated client-side; persist the window so it can be
            // resumed/collected after a relogin, and tell the client the level it will reach.
            if (slotType == SharedNet.Constants.Game.PowerSlotTypes.SkillTraining)
            {
                var start = (int)DateTimeOffset.UtcNow.ToUnixTimeSeconds();
                var end = start + 60;
                power.Power_Training_Start = start;
                power.Power_Training_End = end;
                trainings.Add(new CharacterSkillTrainingEntry
                {
                    PowerId = entry.PowerId,
                    TrainingStarted = DateTimeOffset.FromUnixTimeSeconds(start).UtcDateTime,
                    TrainingEnds = DateTimeOffset.FromUnixTimeSeconds(end).UtcDateTime,
                    TrainingToLevel = (int)power.Power_Rank + 1,
                });
            }
        }
        c.Data = Pack(data);
        return trainings;
    });

    /// <summary>Buy a slot and persist the capacity in both wire representations.</summary>
    public ExpandCharacterSkillSlotsResponse ExpandSkillSlots(Guid owner, Guid characterId,
        ExpandCharacterSkillSlotTypes expandType, int opalCost) => Change(s =>
    {
        var (attribute, maximum) = expandType switch
        {
            ExpandCharacterSkillSlotTypes.Active => (45, 6),
            ExpandCharacterSkillSlotTypes.Passive => (56, 3),
            ExpandCharacterSkillSlotTypes.PassiveTraining => (49, 3),
            _ => throw new RpcException(new Status(StatusCode.InvalidArgument, "Unknown skill slot type")),
        };
        if (opalCost < 0)
            throw new RpcException(new Status(StatusCode.InvalidArgument, "Invalid opal cost"));
        var c = Owned(s, owner, characterId);
        if (c.Opals < opalCost)
            throw new RpcException(new Status(StatusCode.FailedPrecondition, "Not enough opals"));
        var data = Unpack<SerializedCharacterData.SerializedData>(c.Data);
        var account = Unpack<SerializedPlayerGameModeAccountData>(c.GameModeAccount);
        SyncSlotCapacities(c, data, account);
        var current = (int)GetAttribute(data, attribute).Value;
        if (current >= maximum)
            throw new RpcException(new Status(StatusCode.FailedPrecondition, "All slots are unlocked"));
        int newNumSlots = current + 1;
        SetAttribute(data, attribute, newNumSlots);
        SyncSlotCapacities(c, data, account);
        c.Data = Pack(data);
        c.Opals -= opalCost;
        Console.WriteLine($"[SKILL] character={characterId} expand={expandType} cost={opalCost} slots={newNumSlots} opals={c.Opals}");
        return new ExpandCharacterSkillSlotsResponse { NewOpals = c.Opals, NewNumSlots = newNumSlots };
    });

    /// <summary>Persists an opal stash-page purchase.
    /// This RPC used to be an unimplemented stub, so the client applied the new page
    /// locally and it vanished on the next login (the server re-sent the old
    /// GameModeAccount with the old NumStashPages). The request carries the page number
    /// being unlocked (client's pageToUnlock); the RPC has no stash-type discriminator
    /// (unlike skill slots), so it expands the shared stash. One paid call unlocks
    /// exactly one page: a page number at or below the current count is treated as an
    /// idempotent replay of an already-owned page and is not charged again.</summary>
    public ExpandCharacterStashPageResponse ExpandStashPage(Guid owner, Guid characterId,
        int opalCost, int page) => Change(s =>
    {
        if (opalCost < 0)
            throw new RpcException(new Status(StatusCode.InvalidArgument, "Invalid opal cost"));
        var c = Owned(s, owner, characterId);
        var account = Unpack<SerializedPlayerGameModeAccountData>(c.GameModeAccount);
        if (page <= account.NumStashPages)
            return new ExpandCharacterStashPageResponse { NewOpals = c.Opals, UnlockedPage = account.NumStashPages };
        if (c.Opals < opalCost)
            throw new RpcException(new Status(StatusCode.FailedPrecondition, "Not enough opals"));
        c.Opals -= opalCost;
        account.NumStashPages++;
        c.GameModeAccount = Pack(account);
        Console.WriteLine($"[STASH] character={characterId} page={account.NumStashPages} cost={opalCost} opals={c.Opals}");
        return new ExpandCharacterStashPageResponse { NewOpals = c.Opals, UnlockedPage = account.NumStashPages };
    });

    /// <summary>Persists an opal inventory-row purchase.
    /// Same stub history as stash pages: unimplemented server handler, client applied
    /// locally. Row counts had no server-side storage at all, so per-type purchased-row
    /// counters were added to the game-mode account (potions reuses NumPotionSlots).
    /// NOTE: NewNumRows reports the server-recorded purchased rows for the type; the
    /// client UI owns the base (unpurchased) row count.</summary>
    public ExpandCharacterInventoryRowResponse ExpandInventoryRow(Guid owner, Guid characterId,
        ExpandInventoryRowType expandType, int opalCost) => Change(s =>
    {
        if (opalCost < 0)
            throw new RpcException(new Status(StatusCode.InvalidArgument, "Invalid opal cost"));
        if (expandType is not (ExpandInventoryRowType.Inventory or ExpandInventoryRowType.PetLoot
            or ExpandInventoryRowType.CraftingInventory or ExpandInventoryRowType.Potions))
            throw new RpcException(new Status(StatusCode.InvalidArgument, "Unknown inventory row type"));
        var c = Owned(s, owner, characterId);
        if (c.Opals < opalCost)
            throw new RpcException(new Status(StatusCode.FailedPrecondition, "Not enough opals"));
        var account = Unpack<SerializedPlayerGameModeAccountData>(c.GameModeAccount);
        var data = Unpack<SerializedCharacterData.SerializedData>(c.Data);
        SyncSlotCapacities(c, data, account);
        if (expandType == ExpandInventoryRowType.Potions && account.NumPotionSlots >= 3)
            throw new RpcException(new Status(StatusCode.FailedPrecondition, "All potion slots are unlocked"));
        int newNumRows = expandType switch
        {
            ExpandInventoryRowType.Inventory => ++account.NumInventoryRows,
            ExpandInventoryRowType.PetLoot => ++account.NumPetLootRows,
            ExpandInventoryRowType.CraftingInventory => ++account.NumCraftingRows,
            ExpandInventoryRowType.Potions => ++account.NumPotionSlots,
            _ => throw new RpcException(new Status(StatusCode.InvalidArgument, "Unknown inventory row type")),
        };
        if (expandType == ExpandInventoryRowType.Potions) SetAttribute(data, 44, newNumRows);
        c.Data = Pack(data);
        c.GameModeAccount = Pack(account);
        c.Opals -= opalCost;
        Console.WriteLine($"[STASH] character={characterId} rows={expandType} cost={opalCost} newRows={newNumRows} opals={c.Opals}");
        return new ExpandCharacterInventoryRowResponse { NewOpals = c.Opals, NewNumRows = newNumRows };
    });

    /// <summary>Persists a client-side skill rank upgrade.
    /// The official API has no rank-upload RPC: the client's LivingPowers.RankUp only
    /// mutates the in-memory rank, so the server kept Power_Rank = 1 and the next login
    /// overwrote the upgraded rank. A patched client calls this right after RankUp.
    /// Gameplay math (cost, max rank) stays client-authoritative, same trust model as
    /// drops/buffs; the server only enforces monotonicity and a sanity cap.</summary>
    public UpgradeCharacterSkillRankResponse UpgradeSkillRank(Guid owner, Guid characterId,
        int powerHashSafe, double newRank) => Change(s =>
    {
        if (newRank <= 1 || newRank > 200 || double.IsNaN(newRank) || double.IsInfinity(newRank))
            throw new RpcException(new Status(StatusCode.InvalidArgument, "Invalid rank"));
        var c = Owned(s, owner, characterId);
        var data = Unpack<SerializedCharacterData.SerializedData>(c.Data);
        data.Powers ??= new SerializedCharacterData.SerializedPowers { Powers = new() };
        data.Powers.Powers ??= new List<SerializedCharacterData.SerializedPower>();
        var power = data.Powers.Powers.FirstOrDefault(p => p != null && p.PowerHashSafe == powerHashSafe);
        if (power == null)
        {
            // Skill the server never saw assigned (e.g. learned while offline): create it
            // rather than dropping the upgrade.
            power = new SerializedCharacterData.SerializedPower
            {
                PowerHash = powerHashSafe, PowerHashSafe = powerHashSafe, Power_Rank = 1,
                Power_Masteries = new SerializedCharacterData.SerializedPowerMasteries { Masteries = new() },
            };
            data.Powers.Powers.Add(power);
        }
        if (newRank <= power.Power_Rank)
            throw new RpcException(new Status(StatusCode.FailedPrecondition, "Rank must increase"));
        power.Power_Rank = newRank;
        c.Data = Pack(data);
        Console.WriteLine($"[SKILL] character={characterId} rankup hash={powerHashSafe} rank={newRank}");
        return new UpgradeCharacterSkillRankResponse { NewRank = newRank };
    });

    /// <summary>Replaces the persisted power (passive-skill) state with the client's
    /// serialized snapshot: ranks and training windows. Passive skills level through the
    /// training system (StartTraining -> HasFinishedTraining), which has no upload RPC, so
    /// without this the ranks reverted to Power_Rank=1 on every login. The client POSTs
    /// the snapshot from LivingPowers.Serialize. Ranks only move forward; training windows
    /// are overwritten (they are transient).</summary>
    public bool SavePowers(Guid owner, Guid characterId, IList<PowerSyncEntry> powers) => Change(s =>
    {
        var c = Owned(s, owner, characterId);
        var data = Unpack<SerializedCharacterData.SerializedData>(c.Data);
        data.Powers ??= new SerializedCharacterData.SerializedPowers { Powers = new() };
        data.Powers.Powers ??= new List<SerializedCharacterData.SerializedPower>();
        foreach (var e in powers ?? Array.Empty<PowerSyncEntry>())
        {
            if (e == null || e.PowerHashSafe == 0) continue;
            var p = data.Powers.Powers.FirstOrDefault(p => p != null && p.PowerHashSafe == e.PowerHashSafe);
            if (p == null)
            {
                p = new SerializedCharacterData.SerializedPower
                {
                    PowerHash = e.PowerHashSafe, PowerHashSafe = e.PowerHashSafe, Power_Rank = 1,
                    Power_Masteries = new SerializedCharacterData.SerializedPowerMasteries { Masteries = new() },
                };
                data.Powers.Powers.Add(p);
            }
            if (e.Rank > p.Power_Rank) p.Power_Rank = e.Rank;
            p.Power_Training_Start = e.TrainingStart;
            p.Power_Training_End = e.TrainingEnd;
        }
        c.Data = Pack(data);
        Console.WriteLine($"[POWERS] character={characterId} powers={data.Powers.Powers.Count} synced={powers?.Count ?? 0}");
        return true;
    });

    public WorldAnnouncement ApplyWorldProgress(Guid owner, Guid characterId, int worldTier, int worldWaypoint, TimeSpan duration, int depth = 0)        => Change(s =>
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

    /// <summary>
    /// Builds the compact, authoritative snapshot the browser client consumes. Unlike the
    /// full character blob this exposes only what the web UI needs, keeps the internal
    /// MessagePack shape out of the HTTP contract, and never mutates persisted state.
    /// </summary>
    public WebSnapshot ProjectWebSnapshot(Guid owner, Guid id)
    {
        lock (gate)
        {
            var c = Owned(state, owner, id);
            var data = Unpack<SerializedCharacterData.SerializedData>(c.Data);
            var header = Unpack<CharacterHeaderDto>(c.Header);
            var level = Progression.LevelForExperience(c.Experience);

            var attributes = new SortedDictionary<int, double>();
            if (data.Attributes?.Values != null)
                foreach (var origin in data.Attributes.Values.Values)
                    foreach (var kv in origin)
                        attributes[kv.Key] = attributes.TryGetValue(kv.Key, out var existing)
                            ? existing + kv.Value.ValueD
                            : kv.Value.ValueD;

            var items = data.Items?.Items ?? new List<SerializedItem>();
            var equipped = items
                .Where(i => (int)i.Slot is >= 0 and <= 13)
                .Select(i => new WebItem(i.Id, i.Name, (int)i.Slot, i.DefinitionIntegerId, (int)i.BaseRarity))
                .ToList();
            var inventoryCount = items.Count(i => (int)i.Slot < 0 || (int)i.Slot >= 20);

            var skills = (data.Skills?.Skills ?? new List<SerializedCharacterData.SerializedSkill>())
                .OrderBy(s => (int)s.Slot).ThenBy(s => s.Column).ThenBy(s => s.Row)
                .Select(s => s.PowerHashSafe)
                .ToList();

            var combat = data.CombatStats;
            return new WebSnapshot(
                header.CharacterId,
                header.DisplayName,
                (int)header.Class,
                (int)header.Race,
                (int)header.GameMode,
                level,
                c.Experience,
                Progression.ExperienceForLevel((int)level),
                Progression.ExperienceForLevel((int)level + 1),
                c.Silver,
                c.Opals,
                combat?.Offense ?? 0,
                combat?.Defense ?? 0,
                combat?.Recovery ?? 0,
                combat?.MonsterKills ?? 0,
                attributes,
                equipped,
                inventoryCount,
                skills,
                DateTime.UtcNow);
        }
    }

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
