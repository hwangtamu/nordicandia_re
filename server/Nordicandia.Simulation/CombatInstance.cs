namespace Nordicandia.Simulation;

/// <summary>Per-archetype monster tuning so an instance can spawn several kinds of enemy.</summary>
public readonly record struct MonsterProfile(
    string Name, double HpMult = 1.0, double OffenseMult = 1.0, double DefenseMult = 1.0, double Speed = 2.4,
    DamageBundle Damage = default, ResistanceBundle Resistances = default,
    // Data-driven behaviour (Monsters.json Ranged/Caster): ranged/caster packs stop at
    // AttackRange and back off inside PreferredDistance instead of closing to melee.
    bool Ranged = false, double AttackRange = 0, double PreferredDistance = 0,
    // Brains.json action tree name (Monsters.json BrainId); null uses the Standard brain.
    string Brain = null,
    // Champion/elite monsters satisfy ImNotNormalMonster (needed by the curser brains).
    bool Champion = false,
    // D04: monster rarity (Monsters.json AvailableRarities / MonsterRarity enum:
    // 0 Normal, 1 Magic, 2 Rare, 4 Champion, 6 Boss). Selects the finalMult stat curve and
    // the attack-interval range. ExpMult is the world/global difficulty multiplier.
    int Rarity = 0, double ExpMult = 1.0,
    // Overrides the rarity-selected finalMult when > 0.
    double FinalMult = 0);

/// <summary>One monster inside an authoritative combat instance.</summary>
public sealed class CombatMonster
{
    public int Index { get; init; }
    public string Name { get; init; } = "Monster";
    public int Level { get; init; }
    public bool IsBoss { get; init; }
    public double X { get; set; }
    public double Z { get; set; }
    public double Hp { get; set; }
    public double MaxHp { get; set; }
    public double Offense { get; set; }
    public double Defense { get; set; }
    public double Armor { get; set; }
    /// <summary>D04 recovered monster ratings (Monster.GetEvasion / GetMaxAttackRating).</summary>
    public double Evasion { get; set; }
    public double AttackRating { get; set; }
    public DamageBundle Damage { get; set; }
    public ResistanceBundle Resistances { get; set; }
    public double Speed { get; set; } = 2.4;
    public double AttackInterval { get; set; } = 1.6;
    public double AttackCooldown { get; set; }
    /// <summary>True for Ranged/Caster monsters (they keep distance instead of meleeing).</summary>
    public bool Ranged { get; set; }
    public double AttackRange { get; set; }
    public double PreferredDistance { get; set; }
    /// <summary>Brains.json action tree name.</summary>
    public string Brain { get; set; }
    /// <summary>Champion/elite (satisfies ImNotNormalMonster).</summary>
    public bool Champion { get; set; }
    /// <summary>Power selected by the brain for the current think window.</summary>
    public string BrainAction { get; set; }
    public double ActionTimer { get; set; }
    public bool WasInCombat { get; set; }
    /// <summary>Seconds until the current boss power can be used again.</summary>
    public double SpecialCooldown { get; set; }
    /// <summary>Remaining seconds of an ongoing boss power (nova sequence / beam / charge).</summary>
    public double SpecialTimer { get; set; }
    public double StunTimer { get; set; }
    /// <summary>W02: A* waypoints toward the player (grid cells), refreshed periodically.</summary>
    public List<(int X, int Z)>? Path { get; set; }
    public double PathTimer { get; set; }
    /// <summary>C02: per-monster buff container (poison, stuns-as-buffs, ...).
    /// Replaces the old PoisonTimer/PoisonDps pair; poison is now a "poison" buff
    /// instance keyed by source.</summary>
    public BuffManager Buffs { get; } = new();
    public bool Alive { get; set; } = true;
    public double RespawnTimer { get; set; }
    public double WanderTimer { get; set; }
    public double TargetX { get; set; }
    public double TargetZ { get; set; }
}

/// <summary>A drop rolled on a kill. Slot/rarity/level are resolved into a concrete item by
/// the host's loot table so this layer stays content-free and deterministic.</summary>
public readonly record struct LootDrop(int Slot, int Rarity, int Level, bool Boss, ulong Seed,
    // E02: the loot table (Droprates.LootTables name) and the player's class for weighting.
    string Table = "Default", int ClassId = -1);

/// <summary>Read-only projection of an <see cref="CombatInstance"/> handed to callers/tests.</summary>
public readonly record struct MonsterSnapshot(
    int Index, string Name, int Level, bool IsBoss, double X, double Z, double Hp, double MaxHp, bool Alive, double StunTimer,
    string Action = "");

public readonly record struct CombatSnapshot(
    long Version,
    double PlayerX,
    double PlayerZ,
    double PlayerHp,
    double PlayerMaxHp,
    double PlayerMana,
    double PlayerMaxMana,
    double PlayerShield,
    int PlayerLevel,
    double Experience,
    int Silver,
    int Opals,
    int Kills,
    IReadOnlyList<SkillStatus> Skills,
    double OffenseBuffRemaining,
    int DungeonsCleared,
    int BossKillsRemaining,
    bool BossAlive,
    IReadOnlyList<MonsterSnapshot> Monsters,
    int PacksRemaining = 0,
    int TotalPacks = 0);

/// <summary>Result of a skill command.</summary>
public readonly record struct SkillOutcome(bool Cast, double Damage, int TargetIndex, string Reason);

/// <summary>
/// Server-authoritative combat for one character. Deterministic in fixed steps: any
/// sequence of <see cref="Advance"/> calls is split into a fixed timestep, so the same
/// inputs and seed always produce the same outcome regardless of caller frame rate.
///
/// Three active skills (single strike, AoE nova, rally) plus one passive (+10% offence and
/// health). The dungeon loop: kill <see cref="BossKillGoal"/> trash monsters to summon a
/// boss; killing the boss clears the dungeon and starts the next cycle. Progression is
/// written back through the host (see the server's CombatRegistry).
/// </summary>
public sealed partial class CombatInstance
{
    public const double StepSeconds = 0.05;
    public const double PlayerAttackRange = 3.6;
    public const double PlayerAttackInterval = 0.75;
    public const double MonsterAggroRange = 16;
    public const double MonsterAttackRange = 2.1;
    public const double MonsterRespawnSeconds = 6.0;
    public const double PlayerRespawnSeconds = 3.0;
    public const double ArenaHalf = 20.0;
    /// <summary>Trash kills needed to summon the boss. Mutable so a portal run can size the
    /// dungeon from the portal's recovered <c>NumMonsterPacks</c> attribute.</summary>
    public int BossKillGoal { get; private set; } = DefaultBossKillGoal;
    public const int DefaultBossKillGoal = 8;
    public const int BossIndex = 100;
    public const double TrashDropChance = 0.45;

    /// <summary>Maximum equipped active skills / passive skills (client SkillSlotRules 6/3).</summary>
    public const int MaxActiveSkills = 6;
    public const int MaxPassiveSkills = 3;

    private static readonly ClassPowerPool DefaultPool = new(
        "Basic",
        new[]
        {
            new SkillProfile(0, "Strike", "", "", "strike", 2.4, 4.0, 0, 0, 0, 0, 0, "provisional-behaviour", new Dictionary<string, double>()),
            new SkillProfile(1, "Nova", "", "", "nova", 1.5, 8.0, 5.5, 0, 0, 0, 0, "provisional-behaviour", new Dictionary<string, double>()),
            new SkillProfile(2, "Rally", "", "", "rally", 0, 20.0, 0, 0, 0.30, 0.25, 6.0, "provisional-behaviour", new Dictionary<string, double>()),
        },
        new[] { new PassiveProfile("Might", "", "", "might", 0.10, 0.10, "provisional-behaviour") });

    private static readonly int[] DropSlots =
    {
        12, 13, 0, 2, 3, 4, 5, 6, 7, 8, 9, 1, 10, 11,
    };

    // Rarity weights are taken verbatim from gamedata_decrypted/Droprates.json (F..SS),
    // replacing the earlier invented table. The drop *chance* is still provisional.
    private static readonly int[] RarityWeights = { 10000, 1100000, 10000, 3000, 1500, 750, 250, 100, 40, 10, 3, 1 };

    private readonly CombatRandom rng;
    private double playerSpeed = 6.5;
    private int monsterCount;
    private MonsterProfile[] profiles;
    private SkillProfile[] skills;
    private PassiveProfile[] passives;

    private double targetX;
    private double targetZ;
    private bool hasTarget;
    // W02: the player also follows an A* path so walls do not block auto-battle movement.
    private List<(int X, int Z)>? playerPath;
    private double playerPathTimer;
    private double attackCooldown;
    private double[] skillCooldowns;
    // C02: player buffs live in PlayerBuffs (offense, movespeed, curses). The old
    // per-buff timer/amount field pairs are gone; see BuffManager for the recovered
    // stacking/refresh/strength rules.
    /// <summary>C02: the player's buff container (self-buffs keyed "offense|", "movespeed|",
    /// curses "curse-*|").</summary>
    public BuffManager PlayerBuffs { get; } = new();
    private double playerRespawnTimer;
    private int dungeonKills;
    private CombatMonster boss;
    private readonly List<LootDrop> pendingDrops = new();

    // Niflheim portal runs. Recovered from NiflheimPortalGameMode + GameWorld.GetRandomPackSize:
    // TotalPacks = max(Num_Monster_Packs, 2); every pack's size is a stochastic rounding of
    // Rand.RangeExclusive(2*mult, 4*mult) (Area_Pack_Size_Bonus_Percent_Final), carrying the
    // fractional remainder into the next pack.
    private bool packMode;
    private int totalPacks;
    private int packsCleared;
    private double packSizeRemainder;
    private int packIndex;
    // Pack members spawn in one at a time (client: _SpawnDelay >= 0.1s), so a pack is not a
    // single instantaneous wave.
    private int pendingPackSize;
    private double packSpawnTimer;
    private double packOriginX;
    private double packOriginZ;
    private const double PackMemberSpawnInterval = 0.1;
    // Deferred so a pack is never cleared/respawned while an iteration over `monsters` is live.
    private bool pendingPackSpawn;
    // Boss summon powers queue minions here; spawned after the monster loop.
    private int pendingSummons;
    private string pendingSummonMinion;

    // Recovered MonsterCurse* debuffs on the player. Each curse is one BuffManager
    // instance ("curse-slow|", "curse-resist|", ...); the strongest active curse of each
    // kind wins per Buff.IsStrongerThan (duration, then stacks). Magnitudes/durations
    // are Provisional (they come from monster-skill definitions).
    // The leech curse additionally needs the exact monster reference, kept here and
    // cleared when the curse buff expires.
    private CombatMonster curseLeechSource;

    public int TotalPacks => totalPacks;
    public int PacksRemaining => Math.Max(0, totalPacks - packsCleared);

    public double PlayerX { get; private set; }
    public double PlayerZ { get; private set; }
    public double PlayerHp { get; private set; }
    public double PlayerMaxHp { get; private set; }
    public double PlayerMana { get; private set; }
    public double PlayerMaxMana { get; private set; }
    public double PlayerShield { get; private set; }
    public double Offense { get; private set; }
    public double Defense { get; private set; }
    public double Recovery { get; private set; }
    // Recovered ratings (0 when a character has no synthesised attribute map yet).
    public double AttackRating { get; private set; }
    public double Armor { get; private set; }
    public double Evasion { get; private set; }
    public double CritChance { get; private set; }
    public double LifeMax { get; private set; }
    public double ManaMax { get; private set; }
    public DamageBundle Damage { get; private set; }
    public ResistanceBundle Resistances { get; private set; }
    // Recovered on-hit chances (Poison_Chance_On_Hit 428, Double_Damage_..._Poisoned 429,
    // Projectile_Auto_Attacks_Fork_Chance 431).
    public double ForkChance { get; private set; }
    public double ChainChance { get; private set; }
    public double PoisonChance { get; private set; }
    public bool PoisonOnHit { get; private set; }
    public bool ProjectileAutoAttack { get; private set; }
    public double DoubleDamageOnCritPoisoned { get; private set; }
    // C01 damage-pipeline inputs that must reach the resolver (audit F01).
    public DamageBundle Conversion { get; private set; }
    public ResistanceBundle Penetration { get; private set; }
    public double ArmorPenetration { get; private set; }
    public double DodgeChance { get; private set; }
    public double BlockChance { get; private set; }
    public double BlockedDamageMultiplier { get; private set; } = 1;
    public double HitChanceBonus { get; private set; }
    public double HitChanceCap { get; private set; } = 1;
    public bool AlwaysHits { get; private set; }
    public bool IgnoresCrits { get; private set; }
    public double DeadlyStrikeChance { get; private set; }
    public double DamageTakenAmplifyPercent { get; private set; }
    public int PlayerLevel { get; private set; }
    public double Experience { get; private set; }
    public int Silver { get; private set; }
    public int Opals { get; private set; }
    public int Kills { get; private set; }
    public int DungeonsCleared { get; private set; }
    public long Version { get; private set; }

    public IReadOnlyList<CombatMonster> Monsters => monsters;
    private readonly List<CombatMonster> monsters = new();

    /// <summary>W04: the dungeon layout whose spawn areas the packs use (null = the arena ring).</summary>
    public MapLayout Layout { get; }
    /// <summary>E02: the player's class (CharacterClass) for loot item-type weighting.</summary>
    public int ClassId { get; private set; }
    /// <summary>E02: the loot table (Droprates.LootTables name) new drops draw from.</summary>
    public string LootTableName { get; set; } = "Default";

    public CombatInstance(
        CombatantStats stats,
        double experience,
        int silver,
        int opals,
        int kills,
        ulong seed,
        int monsterCount = 5,
        IReadOnlyList<MonsterProfile> monsterProfiles = null,
        ClassPowerPool classPowers = null,
        long initialVersion = 1,
        MapLayout layout = null,
        int classId = -1)
    {
        PlayerLevel = stats.Level;
        Offense = stats.Offense;
        Defense = stats.Defense;
        Recovery = stats.Recovery;
        ApplyRatings(stats);
        Experience = experience;
        Silver = silver;
        Opals = opals;
        Kills = kills;
        this.monsterCount = Math.Clamp(monsterCount, 1, 24);
        profiles = monsterProfiles is { Count: > 0 }
            ? monsterProfiles.ToArray()
            : new[] { new MonsterProfile("Draugr") };
        var powers = classPowers ?? DefaultPool;
        skills = powers.Active.Take(MaxActiveSkills).ToArray();
        passives = powers.Passive.Take(MaxPassiveSkills).ToArray();
        skillCooldowns = new double[skills.Length];
        PlayerMaxHp = EffectiveMaxHealth();
        PlayerHp = PlayerMaxHp;
        PlayerMaxMana = EffectiveMaxMana();
        PlayerMana = PlayerMaxMana;
        ClassId = classId;
        Layout = layout;
        // W04: if the arena centre is a wall in this layout, start the player at the first room so
        // monsters can path to it (otherwise auto-battle stalls outside the walls).
        if (layout is { RoomCenters.Count: > 0 } && !layout.IsFloor(layout.Cell(0, 0, ArenaHalf).X, layout.Cell(0, 0, ArenaHalf).Z))
        {
            var (px, pz) = layout.World(layout.RoomCenters[0].X, layout.RoomCenters[0].Z, ArenaHalf);
            PlayerX = px;
            PlayerZ = pz;
        }
        rng = new CombatRandom(seed == 0 ? 0x9E3779B97F4A7C15UL : seed);
        SpawnMonsters();
        // The version is a persistent, monotonically increasing counter. Restoring it from
        // the store keeps it from regressing to 1 across a server restart, which would make
        // the retained command log's boundary reject fresh commands (P1).
        Version = Math.Max(1, initialVersion);
    }

    private void ApplyRatings(CombatantStats stats)
    {
        AttackRating = stats.AttackRating;
        Armor = stats.Armor;
        Evasion = stats.Evasion;
        CritChance = stats.CritChance;
        LifeMax = stats.LifeMax;
        ManaMax = stats.ManaMax;
        Damage = stats.Damage;
        Resistances = stats.Resistances;
        ForkChance = stats.ForkChance;
        ChainChance = stats.ChainChance;
        PoisonChance = stats.PoisonChance;
        PoisonOnHit = stats.PoisonOnHit;
        ProjectileAutoAttack = stats.ProjectileAutoAttack;
        DoubleDamageOnCritPoisoned = stats.DoubleDamageOnCritPoisoned;
        // C01 damage-pipeline ratings (audit F01): keep conversion, penetration and the
        // hit-resolution rolls so the resolver sees what the attribute engine produced.
        Conversion = stats.Conversion;
        Penetration = stats.Penetration;
        ArmorPenetration = stats.ArmorPenetration;
        DodgeChance = stats.DodgeChance;
        BlockChance = stats.BlockChance;
        BlockedDamageMultiplier = stats.BlockedDamageMultiplier;
        HitChanceBonus = stats.HitChanceBonus;
        HitChanceCap = stats.HitChanceCap;
        AlwaysHits = stats.AlwaysHits;
        IgnoresCrits = stats.IgnoresCrits;
        DeadlyStrikeChance = stats.DeadlyStrikeChance;
        DamageTakenAmplifyPercent = stats.DamageTakenAmplifyPercent;
        // Base 6.5 u/s scaled by the recovered Movement_Speed total (Aesir Tyr +40% etc.).
        playerSpeed = 6.5 * Math.Clamp(stats.MoveSpeedMultiplier, 0.1, 10.0);
    }

    /// <summary>The player's stats as the resolver sees them (F01 verification entry point).</summary>
    public CombatantStats PlayerStatsSnapshot() => PlayerStats();

    private CombatantStats PlayerStats() => new(EffectiveOffense(), Defense, Recovery, PlayerLevel,
        AttackRating, Armor, Evasion, CritChance, LifeMax, ManaMax, Damage, Resistances,
        ForkChance: ForkChance, ChainChance: ChainChance, PoisonChance: PoisonChance,
        DoubleDamageOnCritPoisoned: DoubleDamageOnCritPoisoned,
        ProjectileAutoAttack: ProjectileAutoAttack, PoisonOnHit: PoisonOnHit,
        Conversion: Conversion, Penetration: Penetration, ArmorPenetration: ArmorPenetration,
        DodgeChance: DodgeChance, BlockChance: BlockChance, BlockedDamageMultiplier: BlockedDamageMultiplier,
        HitChanceBonus: HitChanceBonus, HitChanceCap: HitChanceCap, AlwaysHits: AlwaysHits,
        IgnoresCrits: IgnoresCrits, DeadlyStrikeChance: DeadlyStrikeChance,
        DamageTakenAmplifyPercent: DamageTakenAmplifyPercent);

    /// <summary>The player's typed damage (weapon bundle, or a physical bundle from the
    /// provisional Offense when no weapon attributes are present), including the passive/buff bonus.</summary>
    private DamageBundle PlayerDamageBundle()
    {
        var baseBundle = Damage.Total > 0 ? Damage : new DamageBundle(Offense);
        // Weapon_Damage_Percent_Bonus_Final reduction from MonsterCurseReducedWeaponDamage.
        return baseBundle.Scale((1 + PassiveOffense() + PlayerBuffs.MagnitudeOf("offense"))
            * (1 - PlayerBuffs.MagnitudeOf("curse-reducedmg")));
    }

    private static CombatantStats MonsterStats(CombatMonster monster)
        => new(monster.Offense, monster.Defense, 0, monster.Level, AttackRating: monster.AttackRating,
            Armor: monster.Armor, Evasion: monster.Evasion, Resistances: monster.Resistances);

    private double EffectiveMaxHealth() => CombatModel.MaxHealth(PlayerStats())
        * (1 + PassiveHealth());

    /// <summary>Mana pool: the recovered Mana_Max_Total when available, else 40 + 10/level.</summary>
    private double EffectiveMaxMana() => ManaMax > 0 ? ManaMax : 40 + 10 * Math.Max(1, PlayerLevel);

    private double PassiveOffense() => passives.Sum(p => p.OffenseBonus);

    private double PassiveHealth() => passives.Sum(p => p.HealthBonus);

    private double EffectiveOffense() => (Damage.Total > 0 ? Damage.Total : Offense)
        * (1 + PassiveOffense() + PlayerBuffs.MagnitudeOf("offense"));

    /// <summary>Replace the equipped loadout (e.g. after a mastery point or loadout change).</summary>
    public void UpdatePowers(ClassPowerPool powers)
    {
        skills = powers.Active.Take(MaxActiveSkills).ToArray();
        passives = powers.Passive.Take(MaxPassiveSkills).ToArray();
        // Cooldown slots follow the skill list; preserve running cooldowns by slot.
        var resized = new double[skills.Length];
        Array.Copy(skillCooldowns, resized, Math.Min(skillCooldowns.Length, resized.Length));
        skillCooldowns = resized;
        PlayerMaxHp = EffectiveMaxHealth();
        PlayerHp = Math.Min(PlayerHp, PlayerMaxHp);
        Version++;
    }

    /// <summary>Replace the player's combat stats (e.g. after equipping an item). Level stays
    /// experience-derived and is not changed here.</summary>
    public void UpdateStats(CombatantStats stats)
    {
        Offense = stats.Offense;
        Defense = stats.Defense;
        Recovery = stats.Recovery;
        ApplyRatings(stats);
        PlayerMaxHp = EffectiveMaxHealth();
        PlayerHp = Math.Min(PlayerHp, PlayerMaxHp);
        PlayerMaxMana = EffectiveMaxMana();
        PlayerMana = Math.Min(PlayerMana, PlayerMaxMana);
        Version++;
    }

    public List<LootDrop> DrainDrops()
    {
        if (pendingDrops.Count == 0) return new List<LootDrop>();
        var copy = new List<LootDrop>(pendingDrops);
        pendingDrops.Clear();
        return copy;
    }

    private int MonsterLevel => Math.Max(1, PlayerLevel + (int)(rng.NextDouble() * 3) - 1);

    private void SpawnMonsters()
    {
        if (packMode) { SpawnPack(); return; }
        monsters.Clear();
        for (var i = 0; i < monsterCount; i++)
        {
            double x, z;
            if (Layout is { SpawnAnchors.Count: > 0 } layout)
            {
                // W04: spawn on the floor at the layout's room anchors so the player can reach them.
                var anchor = layout.SpawnAnchors[i % layout.SpawnAnchors.Count];
                var (ax, az) = layout.World(anchor.X, anchor.Z, ArenaHalf);
                var angle = rng.NextDouble() * Math.PI * 2;
                var r = rng.NextDouble() * 2.0;
                (x, z) = ConstrainMove(ax, az, ax + Math.Cos(angle) * r, az + Math.Sin(angle) * r);
            }
            else
            {
                var angle = (i / (double)monsterCount) * Math.PI * 2;
                var radius = 7 + (i % 3) * 3;
                x = Math.Cos(angle) * radius;
                z = Math.Sin(angle) * radius;
            }
            monsters.Add(CreateMonster(i, x, z, alive: true));
        }
    }

    /// <summary>Advanced by the client's GetRandomPackSize: next pack size is
    /// <c>floor(remainder + Rand.RangeExclusive(2*mult, 4*mult))</c> with the fraction carried
    /// forward, so the long-run mean matches the multiplier. The web multiplier is 1.</summary>
    private int NextPackSize(double multiplier = 1)
    {
        var mult = Math.Max(0, multiplier);
        var raw = mult * (2 + rng.NextDouble() * 2); // [2*mult, 4*mult)
        var total = packSizeRemainder + raw;
        var size = (int)Math.Floor(total);
        packSizeRemainder = Math.Round(total - size, 4);
        return Math.Max(1, size);
    }

    /// <summary>Clears the arena and starts the next Niflheim pack in a spawn zone. The first
    /// member appears immediately; the rest follow every 0.1s (client _SpawnDelay step).</summary>
    private void SpawnPack()
    {
        if (packsCleared >= totalPacks) return;
        monsters.Clear();
        // Client packs spawn inside one of the dungeon's spawn areas. W04: when a MapLayout is
        // supplied, use its room anchors; otherwise fall back to the provisional ring of zones.
        if (Layout is { SpawnAnchors.Count: > 0 } layout)
        {
            var anchor = layout.SpawnAnchors[(int)(rng.NextDouble() * layout.SpawnAnchors.Count) % layout.SpawnAnchors.Count];
            var (ax, az) = layout.World(anchor.X, anchor.Z, ArenaHalf);
            packOriginX = ax;
            packOriginZ = az;
        }
        else
        {
            var zone = (int)(rng.NextDouble() * SpawnZones.Length) % SpawnZones.Length;
            var (cx, cz) = SpawnZones[zone];
            packOriginX = cx;
            packOriginZ = cz;
        }
        pendingPackSize = NextPackSize();
        packSpawnTimer = 0;
        pendingPackSize--;
        SpawnOnePackMember();
        packIndex++;
    }

    /// <summary>Positions of the Niflheim spawn zones (the web stand-in for the client's
    /// DungeonMonsterSpawnArea volumes).</summary>
    private static readonly (double X, double Z)[] SpawnZones =
    {
        (0, 12), (11, 6), (11, -6), (0, -12), (-11, -6), (-11, 6),
    };

    private void SpawnOnePackMember()
    {
        var index = monsters.Count;
        var angle = rng.NextDouble() * Math.PI * 2;
        var r = 1.5 + rng.NextDouble() * 2.0;
        var x = Math.Clamp(packOriginX + Math.Cos(angle) * r, -ArenaHalf, ArenaHalf);
        var z = Math.Clamp(packOriginZ + Math.Sin(angle) * r, -ArenaHalf, ArenaHalf);
        // W02: keep pack members on the floor (a random offset can land in a wall).
        var (px, pz) = ConstrainMove(packOriginX, packOriginZ, x, z);
        monsters.Add(CreateMonster(index, px, pz, alive: true));
    }

    private CombatMonster CreateMonster(int index, double x, double z, bool alive, MonsterProfile? overrideProfile = null)
    {
        var level = MonsterLevel;
        var profile = overrideProfile ?? profiles[index % profiles.Length];
        // D04: client Monster.Get* curves. finalMult is rarity-selected (Boss/Champion/normal);
        // armor/evasion roll Rand(0.96,1.01), life/force-field Rand(0.99,1.01) (per-spawn variance).
        var finalMult = profile.FinalMult > 0 ? profile.FinalMult : MonsterScaling.RarityFinalMult(profile.Rarity);
        var stats = MonsterScaling.Stats(level, profile.ExpMult, finalMult);
        // The client rolls Rand(0.96,1.01) on armour/evasion and Rand(0.99,1.01) on life, but
        // using the shared combat RNG here would shift every downstream roll, so the average
        // value is used (the variance is cosmetic and the authoritative simulation stays stable).
        var armor = stats.Armor;
        var evasion = stats.Evasion;
        var attackRating = stats.MaxAttackRating;
        var maxHp = stats.Life * profile.HpMult;
        var offense = stats.WeaponDamage * profile.OffenseMult;
        // A profile's Damage is a distribution (fractions); scale it by this spawn's offence.
        var damage = profile.Damage.Total > 0 ? profile.Damage.Scale(offense) : new DamageBundle(offense);
        return new CombatMonster
        {
            Index = index,
            Name = profile.Name,
            Level = level,
            X = x,
            Z = z,
            Hp = alive ? maxHp : 0,
            MaxHp = maxHp,
            Offense = offense,
            Defense = evasion * profile.DefenseMult,
            Armor = armor * profile.DefenseMult,
            Evasion = evasion * profile.DefenseMult,
            AttackRating = attackRating,
            Damage = damage,
            Resistances = profile.Resistances,
            Speed = profile.Speed,
            Ranged = profile.Ranged,
            AttackRange = profile.AttackRange > 0 ? profile.AttackRange : MonsterAttackRange,
            PreferredDistance = profile.PreferredDistance > 0 ? profile.PreferredDistance : MonsterAttackRange * 0.5,
            Brain = profile.Brain ?? "Standard",
            Champion = profile.Champion,
            Alive = alive,
            AttackCooldown = rng.NextDouble() * 1.6,
            WanderTimer = rng.NextDouble() * 2,
        };
    }

    private CombatMonster CreateBoss()
    {
        var level = PlayerLevel + 3;
        return new CombatMonster
        {
            Index = BossIndex,
            Name = "Frostbound Jarl",
            Brain = "Boss_WolfKing",
            Level = level,
            IsBoss = true,
            X = 0,
            Z = -12,
            // D04: boss = rarity 6. The client selects a separate finalMult for bosses; its
            // value is the client rarity table entry 0x13880D8 = 1.2 (ClientVerified).
            MaxHp = MonsterScaling.Life(level, 1, MonsterScaling.RarityFinalMult(6)),
            Hp = MonsterScaling.Life(level, 1, MonsterScaling.RarityFinalMult(6)),
            Offense = MonsterScaling.WeaponDamage(level, 1, MonsterScaling.RarityFinalMult(6)),
            Defense = MonsterScaling.Evasion(level, 1, MonsterScaling.RarityFinalMult(6)),
            Armor = MonsterScaling.Armor(level, 1, MonsterScaling.RarityFinalMult(6)),
            Evasion = MonsterScaling.Evasion(level, 1, MonsterScaling.RarityFinalMult(6)),
            AttackRating = MonsterScaling.MaxAttackRating(level, 1, MonsterScaling.RarityFinalMult(6)),
            Damage = new DamageBundle(Physical: 0.5, Cold: 0.5).Scale(MonsterScaling.WeaponDamage(level, 1, MonsterScaling.RarityFinalMult(6))),
            Resistances = new ResistanceBundle(Fire: 0.2, Cold: 0.5, Lightning: 0.2, Poison: 0.2),
            Speed = 2.0,
            AttackInterval = 2.0,
            Alive = true,
            AttackCooldown = 1.5,
        };
    }

    public void Advance(double deltaSeconds)
    {
        if (deltaSeconds <= 0 || double.IsNaN(deltaSeconds)) return;
        var remaining = Math.Min(deltaSeconds, 5.0);
        while (remaining > 1e-9)
        {
            var step = Math.Min(StepSeconds, remaining);
            Step(step);
            remaining -= step;
        }
    }

    private void Step(double dt)
    {
        if (playerRespawnTimer > 0)
        {
            playerRespawnTimer -= dt;
            if (playerRespawnTimer <= 0)
            {
                PlayerHp = PlayerMaxHp;
                PlayerX = 0;
                PlayerZ = 0;
                hasTarget = false;
            }
        }

        if (PlayerHp > 0) UpdatePlayer(dt);
        UpdateMonsters(dt);
        if (boss is { Alive: true } && PlayerHp > 0) UpdateBoss(dt);
        TickMonsterBuffs(dt);
        TickClouds(dt);
        TickMinions(dt);
        TickTraps(dt);
        TickChannel(dt);
        if (pendingPackSize > 0)
        {
            packSpawnTimer += dt;
            while (pendingPackSize > 0 && packSpawnTimer >= PackMemberSpawnInterval)
            {
                packSpawnTimer -= PackMemberSpawnInterval;
                pendingPackSize--;
                SpawnOnePackMember();
            }
        }
        if (pendingSummons > 0)
        {
            var count = pendingSummons;
            var minion = pendingSummonMinion;
            pendingSummons = 0;
            pendingSummonMinion = null;
            SummonMinions(count, minion);
        }
        if (pendingPackSpawn)
        {
            pendingPackSpawn = false;
            SpawnPack();
        }
        Version++;
    }

    private void UpdatePlayer(double dt)
    {
        attackCooldown -= dt;
        for (var i = 0; i < skillCooldowns.Length; i++) skillCooldowns[i] = Math.Max(0, skillCooldowns[i] - dt);
        PlayerBuffs.Tick(dt);
        if (!PlayerBuffs.Has("curse-leech")) curseLeechSource = null;
        if (PlayerHp > 0)
            PlayerMana = Math.Min(PlayerMaxMana, PlayerMana + (4 + PlayerLevel) * dt);
        if (Recovery > 0 && PlayerHp > 0)
            PlayerHp = Math.Min(PlayerMaxHp, PlayerHp + Recovery * dt);

        if (hasTarget)
        {
            var dx = targetX - PlayerX;
            var dz = targetZ - PlayerZ;
            var distance = Math.Sqrt(dx * dx + dz * dz);
            if (distance < 0.15) hasTarget = false;
            else
            {
                double goalX = targetX, goalZ = targetZ;
                if (Layout is not null)
                {
                    playerPathTimer -= dt;
                    if (playerPath is null || playerPath.Count == 0 || playerPathTimer <= 0)
                    {
                        var path = Layout.FindPath(Layout.Cell(PlayerX, PlayerZ, ArenaHalf),
                            Layout.Cell(targetX, targetZ, ArenaHalf));
                        if (path.Count > 0) path.RemoveAt(0);
                        playerPath = path;
                        playerPathTimer = 0.4;
                    }
                    if (playerPath is { Count: > 0 })
                    {
                        var (wx, wz) = Layout.World(playerPath[0].X, playerPath[0].Z, ArenaHalf);
                        goalX = wx;
                        goalZ = wz;
                    }
                }
                var gdx = goalX - PlayerX;
                var gdz = goalZ - PlayerZ;
                var gdist = Math.Sqrt(gdx * gdx + gdz * gdz);
                if (gdist > 1e-6)
                {
                    var speed = playerSpeed * (1 + PlayerBuffs.MagnitudeOf("movespeed")) * (1 - CurseSlow);
                    var step = Math.Min(gdist, speed * dt);
                    // W02: the player respects the dungeon walls (slides along them).
                    var (nx, nz) = ConstrainMove(PlayerX, PlayerZ,
                        PlayerX + gdx / gdist * step, PlayerZ + gdz / gdist * step);
                    PlayerX = nx;
                    PlayerZ = nz;
                    ClampToArena();
                }
            }
        }

        var target = NearestAliveMonster(PlayerAttackRange);
        if (target is not null && attackCooldown <= 0)
        {
            attackCooldown = PlayerAttackInterval;
            if (ProjectileAutoAttack)
            {
                // C03: real projectile — flies from the player, collides geometrically,
                // fork/chain per HandleForkAndChain (no more nearest-enemy substitution).
                FireProjectiles(
                    new Projectile { X = PlayerX, Z = PlayerZ, Source = "autoattack" },
                    target.X, target.Z,
                    (m, mult) =>
                    {
                        var h = CombatModel.ResolveBundleAttack(
                            PlayerStats(), PlayerDamageBundle(), MonsterStats(m), m.Resistances,
                            new AttackProfile(mult, 0.08, 1.6, 0.12), rng);
                        var dealt = ApplyOnHit(m, h, autoAttack: false);
                        DamageMonster(m, dealt);
                        return dealt;
                    },
                    () => ForkChance, () => ChainChance, () => 0.0);
            }
            else
            {
                var hit = CombatModel.ResolveBundleAttack(
                    PlayerStats(), PlayerDamageBundle(), MonsterStats(target), target.Resistances,
                    new AttackProfile(1.0, 0.08, 1.6, 0.12),
                    rng);
                DamageMonster(target, ApplyOnHit(target, hit, autoAttack: true));
            }
        }
    }

    private void UpdateMonsters(double dt)
    {
        foreach (var monster in monsters)
        {
            if (!monster.Alive)
            {
                // Pack members are consumed when the pack is cleared; they do not respawn.
                if (packMode) continue;
                monster.RespawnTimer -= dt;
                if (monster.RespawnTimer <= 0)
                {
                    var angle = rng.NextDouble() * Math.PI * 2;
                    var radius = 10 + rng.NextDouble() * 6;
                    monster.X = Math.Cos(angle) * radius;
                    monster.Z = Math.Sin(angle) * radius;
                    monster.Hp = monster.MaxHp;
                    monster.Alive = true;
                    monster.Buffs.Clear();
                }
                continue;
            }
            UpdateBrain(monster, dt);
        }
    }

    private void UpdateBoss(double dt)
    {
        if (boss is null) return;
        // Bosses drive their recovered Brains.json tree (specials + DefaultAttackProxy).
        UpdateBrain(boss, dt);
    }

    /// <summary>Recovered WeightedActionBrain: pick a weighted action from the monster's
    /// Brains.json tree whose conditions hold, then execute the power's behaviour.</summary>
    private void UpdateBrain(CombatMonster monster, double dt)
    {
        if (monster.StunTimer > 0)
        {
            monster.StunTimer = Math.Max(0, monster.StunTimer - dt);
            return;
        }
        var inCombat = DistanceToPlayer(monster) <= MonsterAggroRange;
        if (inCombat) monster.WasInCombat = true;
        var stateCombat = inCombat;
        var stateRunOut = !inCombat && monster.WasInCombat;
        var stateWander = !inCombat && !monster.WasInCombat;
        // "Minions" are any other living non-boss monsters (the boss's summons).
        var hasMinions = monsters.Any(m => m.Alive && !m.IsBoss && !ReferenceEquals(m, monster));

        bool Condition(string name) => name switch
        {
            "StateCombat" => stateCombat,
            "StateRunOutOfCombat" => stateRunOut,
            "StateWander" => stateWander,
            "IHaveNoMinions" => !hasMinions,
            "IHaveMinions" => hasMinions,
            "NotOnFullLife" => monster.Hp < monster.MaxHp,
            "MoreThan50PercentLife" => monster.Hp > 0.5 * monster.MaxHp,
            "LessThan50PercentLife" => monster.Hp < 0.5 * monster.MaxHp,
            "NotIntimidated" => true,
            "TargetNear" => inCombat,
            "TargetFar" => !inCombat,
            "ImNotNormalMonster" => monster.IsBoss || monster.Champion,
            // The web slice has no world-tier progression; it represents the endgame.
            "ImAboveOrEqualToTier4" => true,
            // W03: Boss phase conditions. CanEnrage is Inferred (life threshold not recovered);
            // StateIdle is the pre-combat idle state; the minion checks reuse hasMinions.
            "StateIdle" => stateWander,
            "AliveIdleNonBossMonstersExist" => hasMinions,
            "CanEnrage" => monster.IsBoss && monster.Hp <= 0.5 * monster.MaxHp,
            // Summon lifetime / totem / pet conditions belong to P01-P04; hostile monsters ignore them.
            "ImExpired" => false,
            "WorldLootExists" => false,
            _ => false,
        };

        if (monster.BrainAction is null || monster.ActionTimer <= 0)
        {
            var brain = BrainCatalog.For(monster.Brain);
            monster.BrainAction = BrainCatalog.Choose(brain, Condition, rng.NextDouble)?.Power ?? "DefaultAttackProxy";
            monster.ActionTimer = 0.5 + rng.NextDouble() * 0.5;
        }
        else
        {
            monster.ActionTimer -= dt;
        }

        var power = MonsterPowerCatalog.For(monster.BrainAction);
        if (power is not null)
        {
            ExecuteMonsterPower(monster, power, dt);
            return;
        }
        switch (monster.BrainAction)
        {
            case "Wander":
                WanderStep(monster, dt);
                break;
            case "Flee":
            case "RunOutOfCombat":
                FleeStep(monster, dt);
                break;
            // W02: explicit non-power actions from Brains.json. Pet/totem actions (return to master,
            // loot, unique pull) fall through to the melee/range behaviour in the web slice.
            case "MeleeWeaponSwing":
            case "MinionReturnToMaster":
            case "PetLoot":
            case "UniqueItemHuginnPull":
            case "DefaultAttackProxy":
            default: // DefaultAttackProxy chases and attacks.
                ChaseAndAttack(monster, dt);
                break;
        }
    }

    /// <summary>Executes a recovered boss power. Effects hit the player (the only hostile target)
    /// or summon minions; the effect shape follows the client implementation classes.</summary>
    private void ExecuteMonsterPower(CombatMonster monster, MonsterPower power, double dt)
    {
        monster.SpecialCooldown = Math.Max(0, monster.SpecialCooldown - dt);
        if (monster.SpecialTimer > 0)
        {
            monster.SpecialTimer -= dt;
            switch (power.Kind)
            {
                case MonsterPowerKind.NovaSequence:
                case MonsterPowerKind.Beam:
                    if (PlayerInRange(monster, power.Radius)) DamagePlayer(monster, power);
                    break;
                case MonsterPowerKind.Charge:
                    if (ChargeStep(monster, dt) && PlayerInRange(monster, power.Radius))
                        DamagePlayer(monster, power);
                    break;
            }
            if (monster.SpecialTimer <= 0) monster.SpecialCooldown = power.Cooldown;
            return;
        }
        if (monster.SpecialCooldown > 0)
        {
            ChaseAndAttack(monster, dt);
            return;
        }
        switch (power.Kind)
        {
            case MonsterPowerKind.Nova:
            case MonsterPowerKind.TripleStrike:
                if (PlayerInRange(monster, power.Radius)) DamagePlayer(monster, power);
                monster.SpecialCooldown = power.Cooldown;
                break;
            case MonsterPowerKind.NovaSequence:
            case MonsterPowerKind.Beam:
            case MonsterPowerKind.Charge:
                monster.SpecialTimer = Math.Max(0.1, power.Duration);
                break;
            case MonsterPowerKind.Summon:
                pendingSummons += Math.Max(1, power.Count);
                pendingSummonMinion = power.Minion;
                monster.SpecialCooldown = power.Cooldown;
                break;
            case MonsterPowerKind.Curse:
                ApplyCurse(monster, power);
                break;
        }
    }

    /// <summary>Applies a {@link MonsterCurse*} debuff to the player as a BuffManager
    /// instance. Same-kind curses share one key; the stronger instance wins per the
    /// recovered Buff.IsStrongerThan rules (duration, then stacks).</summary>
    private void ApplyCurse(CombatMonster monster, MonsterPower power)
    {
        var def = power.Curse switch
        {
            CurseEffect.Slow => "curse-slow",
            CurseEffect.SlowProjectiles => "curse-slow",
            CurseEffect.LowerResistances => "curse-resist",
            CurseEffect.AmplifyDamageTaken => "curse-amplify",
            CurseEffect.ReducedWeaponDamage => "curse-reducedmg",
            CurseEffect.Leech => "curse-leech",
            _ => "",
        };
        if (def.Length > 0)
        {
            if (PlayerBuffs.Add(new BuffInstance
                {
                    DefinitionId = def,
                    Duration = power.Duration,
                    Remaining = power.Duration,
                    Magnitude = power.DamageMultiplier,
                }) && def == "curse-leech")
                curseLeechSource = monster;
        }
        monster.SpecialCooldown = power.Cooldown;
    }

    public double CurseSlow => PlayerBuffs.MagnitudeOf("curse-slow");
    public double CurseResistancePenalty => PlayerBuffs.MagnitudeOf("curse-resist");
    public double CurseAmplifyDamageTaken => PlayerBuffs.MagnitudeOf("curse-amplify");
    public double CurseWeaponDamageReduction => PlayerBuffs.MagnitudeOf("curse-reducedmg");

    private ResistanceBundle EffectivePlayerResistances()
    {
        var penalty = PlayerBuffs.MagnitudeOf("curse-resist");
        return penalty > 0
            ? new ResistanceBundle(
                Resistances.Fire - penalty, Resistances.Cold - penalty,
                Resistances.Lightning - penalty, Resistances.Poison - penalty)
            : Resistances;
    }

    /// <summary>Shield, amplify-damage-taken, leech and death for one hit on the player.</summary>
    private void ApplyPlayerDamage(CombatMonster source, double incoming)
    {
        incoming *= 1 + PlayerBuffs.MagnitudeOf("curse-amplify");
        // C06 batch 3: Intimidate — source monster deals 10% less.
        if (source is not null)
        {
            var intimidate = source.Buffs.MagnitudeOf("intimidate", "Intimidate");
            if (intimidate > 0) incoming *= 1 - intimidate;
        }
        // C06 batch 3: BoneLink — player takes 30% less.
        var bonelink = PlayerBuffs.MagnitudeOf("bonelink", "BoneLink");
        if (bonelink > 0) incoming *= 1 - bonelink;
        // C06: Thorns physical damage reduction.
        var thornsReduction = PlayerBuffs.MagnitudeOf("thorns-reduction", "Thorns");
        if (thornsReduction > 0) incoming *= 1 - thornsReduction;
        // C06 batch 3: ManaShield absorption — 20% of damage to shield (provisional).
        var manaShield = PlayerBuffs.MagnitudeOf("manashield", "ManaShield");
        if (manaShield > 0 && PlayerShield > 0)
        {
            var toShield = Math.Min(PlayerShield, incoming * manaShield);
            PlayerShield -= toShield;
            incoming -= toShield;
        }
        if (PlayerShield > 0)
        {
            var absorbed = Math.Min(PlayerShield, incoming);
            PlayerShield -= absorbed;
            incoming -= absorbed;
        }
        PlayerHp = Math.Max(0, PlayerHp - incoming);
        // C06: Thorns reflects melee damage back to the attacker.
        var thorns = PlayerBuffs.MagnitudeOf("thorns", "Thorns");
        if (thorns > 0 && source is not null && source.Alive)
        {
            var reflected = incoming * thorns;
            DamageMonster(source, reflected);
            EmitEvent("damage", source.Index, reflected, "thorns reflect");
        }
        // C06 batch 3: Retaliation — 0.3x weapon damage to attacker.
        var retaliation = PlayerBuffs.MagnitudeOf("retaliation", "Retaliation");
        if (retaliation > 0 && source is not null && source.Alive)
        {
            var retSkill = new SkillProfile(0, "Retaliation", "", "", "strike",
                retaliation, 0, 0, 0, 0, 0, 0, "c06", new Dictionary<string, double>());
            var retDealt = HitTarget(source, retSkill, retaliation);
            EmitEvent("damage", source.Index, retDealt, "retaliation");
        }
        var leech = PlayerBuffs.Get("curse-leech");
        if (leech is not null && source is not null && ReferenceEquals(curseLeechSource, source))
            source.Hp = Math.Min(source.MaxHp, source.Hp + incoming * leech.Magnitude);
        if (PlayerHp <= 0)
        {
            // Death clears non-persistent buffs (client rule); the leech source dies with it.
            PlayerBuffs.Clear();
            curseLeechSource = null;
            playerRespawnTimer = PlayerRespawnSeconds;
            hasTarget = false;
        }
    }

    private bool PlayerInRange(CombatMonster monster, double radius)
        => DistanceToPlayer(monster) <= Math.Max(0.5, radius);

    /// <summary>Charges the monster at the player; returns true once it is within contact range.</summary>
    private bool ChargeStep(CombatMonster monster, double dt)
    {
        var dx = PlayerX - monster.X;
        var dz = PlayerZ - monster.Z;
        var dist = Math.Max(1e-6, Math.Sqrt(dx * dx + dz * dz));
        if (dist <= MonsterAttackRange) return true;
        var step = Math.Min(dist, EffectiveMonsterSpeed(monster) * 3 * dt);
        monster.X = Math.Clamp(monster.X + dx / dist * step, -ArenaHalf, ArenaHalf);
        monster.Z = Math.Clamp(monster.Z + dz / dist * step, -ArenaHalf, ArenaHalf);
        return dist - step <= MonsterAttackRange;
    }

    /// <summary>Applies one power hit to the player with the power's element and multiplier.</summary>
    private void DamagePlayer(CombatMonster monster, MonsterPower power)
    {
        var element = power.Element switch
        {
            "Fire" => new DamageBundle(Fire: 1),
            "Cold" => new DamageBundle(Cold: 1),
            "Lightning" => new DamageBundle(Lightning: 1),
            "Poison" => new DamageBundle(Poison: 1),
            _ => new DamageBundle(Physical: 1),
        };
        var bundle = element.Scale(Math.Max(1e-6, monster.Offense) * Math.Max(0.1, power.DamageMultiplier));
        var hit = CombatModel.ResolveBundleAttack(
            MonsterStats(monster), bundle, PlayerStats(), EffectivePlayerResistances(),
            new AttackProfile(1.0, 0.03, 1.5, 0.0), rng);
        if (!hit.Hit) return;
        ApplyPlayerDamage(monster, hit.Damage);
    }

    /// <summary>Spawns <paramref name="count"/> minions near the pack/boss origin. Deferred so
    /// the monster list is never mutated while it is being iterated.</summary>
    private void SummonMinions(int count, string minion)
    {
        if (profiles.Length == 0) return;
        // Use the summon power's gamedata minion (for its avatar/name); stats stay Provisional.
        var name = string.IsNullOrEmpty(minion) ? profiles[0].Name : minion;
        var profile = new MonsterProfile(name, HpMult: 1.0, OffenseMult: 0.8, DefenseMult: 0.8,
            Speed: 2.6, Brain: "AggressiveMinion");
        for (var i = 0; i < count && monsters.Count < 40; i++)
        {
            var angle = rng.NextDouble() * Math.PI * 2;
            var radius = 2 + rng.NextDouble() * 2;
            monsters.Add(CreateMonster(monsters.Count,
                Math.Clamp(packOriginX + Math.Cos(angle) * radius, -ArenaHalf, ArenaHalf),
                Math.Clamp(packOriginZ + Math.Sin(angle) * radius, -ArenaHalf, ArenaHalf), alive: true, profile));
        }
    }

    private double DistanceToPlayer(CombatMonster monster)
        => Math.Sqrt((PlayerX - monster.X) * (PlayerX - monster.X) + (PlayerZ - monster.Z) * (PlayerZ - monster.Z));

    private void WanderStep(CombatMonster monster, double dt)
    {
        monster.WanderTimer -= dt;
        if (monster.WanderTimer <= 0)
        {
            monster.WanderTimer = 2 + rng.NextDouble() * 3;
            var angle = rng.NextDouble() * Math.PI * 2;
            var radius = 4 + rng.NextDouble() * 12;
            monster.TargetX = Math.Cos(angle) * radius;
            monster.TargetZ = Math.Sin(angle) * radius;
        }
        MoveToward(monster, monster.TargetX, monster.TargetZ, dt);
    }

    private void FleeStep(CombatMonster monster, double dt)
    {
        var dx = monster.X - PlayerX;
        var dz = monster.Z - PlayerZ;
        var dist = Math.Max(1e-6, Math.Sqrt(dx * dx + dz * dz));
        MoveToward(monster, monster.X + dx / dist * 5, monster.Z + dz / dist * 5, dt);
    }

    private void MoveToward(CombatMonster monster, double goalX, double goalZ, double dt)
    {
        var gx = goalX - monster.X;
        var gz = goalZ - monster.Z;
        var gdist = Math.Sqrt(gx * gx + gz * gz);
        if (gdist <= 0.3) return;
        var step = Math.Min(gdist, EffectiveMonsterSpeed(monster) * dt);
        MoveMonster(monster, gx / gdist, gz / gdist, step);
    }

    /// <summary>W02: move a monster along a direction with wall collision. A move into a wall
    /// slides along the free axis instead of stopping (no A* yet, but rooms/corridors stay usable).</summary>
    private void MoveMonster(CombatMonster monster, double dirX, double dirZ, double step)
    {
        var toX = Math.Clamp(monster.X + dirX * step, -ArenaHalf, ArenaHalf);
        var toZ = Math.Clamp(monster.Z + dirZ * step, -ArenaHalf, ArenaHalf);
        var (x, z) = ConstrainMove(monster.X, monster.Z, toX, toZ);
        monster.X = x;
        monster.Z = z;
    }

    private (double X, double Z) ConstrainMove(double fromX, double fromZ, double toX, double toZ)
    {
        if (Layout is null) return (toX, toZ);
        // Already off the floor (e.g. an edge spawn): let it move back toward the floor.
        if (!IsFloorWorld(fromX, fromZ)) return (toX, toZ);
        if (IsFloorWorld(toX, toZ)) return (toX, toZ);
        if (IsFloorWorld(toX, fromZ)) return (toX, fromZ);
        if (IsFloorWorld(fromX, toZ)) return (fromX, toZ);
        return (fromX, fromZ);
    }

    private bool IsFloorWorld(double x, double z)
    {
        var layout = Layout!;
        var tile = 2 * ArenaHalf / Math.Max(layout.Width, layout.Height);
        var gx = (int)Math.Floor(x / tile + layout.Width / 2.0);
        var gz = (int)Math.Floor(z / tile + layout.Height / 2.0);
        return layout.IsFloor(gx, gz);
    }

    private void ChaseAndAttack(CombatMonster monster, double dt)
    {
        if (monster.StunTimer > 0)
        {
            monster.StunTimer = Math.Max(0, monster.StunTimer - dt);
            return;
        }
        monster.AttackCooldown -= dt;
        var dx = PlayerX - monster.X;
        var dz = PlayerZ - monster.Z;
        var distance = Math.Max(1e-6, Math.Sqrt(dx * dx + dz * dz));
        // Ranged/Caster monsters attack from their own range; melee use the global range.
        var attackRange = monster.Ranged ? monster.AttackRange : MonsterAttackRange;

        if (distance <= attackRange)
        {
            if (monster.AttackCooldown <= 0)
            {
                // C06 batch 3: Decay slows attack speed by 20%.
                var atkSlow = monster.Buffs.MagnitudeOf("decay-slow", "Decay");
                monster.AttackCooldown = monster.AttackInterval * (1 + atkSlow);
                var monsterDamage = monster.Damage.Total > 0 ? monster.Damage : new DamageBundle(monster.Offense);
                var hit = CombatModel.ResolveBundleAttack(
                    MonsterStats(monster), monsterDamage, PlayerStats(), EffectivePlayerResistances(),
                    new AttackProfile(1.0, 0.03, 1.5, 0.12),
                    rng);
                ApplyPlayerDamage(monster, hit.Damage);
            }
            // Kiting: a ranged monster inside its preferred distance backs away.
            if (monster.Ranged && distance < monster.PreferredDistance)
            {
                var step = Math.Min(monster.PreferredDistance - distance + 1.0, EffectiveMonsterSpeed(monster) * dt);
                MoveMonster(monster, -dx / distance, -dz / distance, step);
            }
            return;
        }

        double goalX, goalZ;
        if (distance <= MonsterAggroRange)
        {
            goalX = PlayerX;
            goalZ = PlayerZ;
            // W02: follow an A* path around the walls, refreshed every 0.4s.
            if (Layout is not null)
            {
                monster.PathTimer -= dt;
                if (monster.Path is null || monster.PathTimer <= 0)
                {
                    var path = Layout.FindPath(Layout.Cell(monster.X, monster.Z, ArenaHalf),
                        Layout.Cell(PlayerX, PlayerZ, ArenaHalf));
                    if (path.Count > 0) path.RemoveAt(0);
                    monster.Path = path;
                    monster.PathTimer = 0.4;
                }
                if (monster.Path is { Count: > 0 })
                {
                    var (wx, wz) = Layout.World(monster.Path[0].X, monster.Path[0].Z, ArenaHalf);
                    goalX = wx;
                    goalZ = wz;
                }
            }
        }
        else
        {
            monster.WanderTimer -= dt;
            if (monster.WanderTimer <= 0)
            {
                monster.WanderTimer = 2 + rng.NextDouble() * 3;
                var angle = rng.NextDouble() * Math.PI * 2;
                var radius = 4 + rng.NextDouble() * 12;
                monster.TargetX = Math.Cos(angle) * radius;
                monster.TargetZ = Math.Sin(angle) * radius;
            }
            goalX = monster.TargetX;
            goalZ = monster.TargetZ;
        }

        var gx = goalX - monster.X;
        var gz = goalZ - monster.Z;
        var gdist = Math.Sqrt(gx * gx + gz * gz);
        if (gdist > 0.3)
        {
            var step = Math.Min(gdist, EffectiveMonsterSpeed(monster) * dt);
            MoveMonster(monster, gx / gdist, gz / gdist, step);
        }
    }

    private CombatMonster NearestAliveMonster(double range)
    {
        CombatMonster best = null;
        var bestDistance = range;
        if (boss is { Alive: true })
        {
            var bd = Math.Sqrt(Math.Pow(boss.X - PlayerX, 2) + Math.Pow(boss.Z - PlayerZ, 2));
            if (bd < bestDistance)
            {
                bestDistance = bd;
                best = boss;
            }
        }
        foreach (var monster in monsters)
        {
            if (!monster.Alive) continue;
            var distance = Math.Sqrt(Math.Pow(monster.X - PlayerX, 2) + Math.Pow(monster.Z - PlayerZ, 2));
            if (distance < bestDistance)
            {
                bestDistance = distance;
                best = monster;
            }
        }
        return best;
    }

    /// <summary>On-hit status handling. Poison duration/rate come from HitPayload.Apply;
    /// projectile branching uses independently resolved hits, never copied post-mitigation damage.</summary>
    private double ApplyOnHit(CombatMonster target, DamageResult hit, bool autoAttack)
    {
        if (!hit.Hit || !target.Alive) return 0;
        var damage = hit.Damage;
        if (hit.Critical && target.Buffs.Has("poison", "player") && CombatModel.RollChance(DoubleDamageOnCritPoisoned, rng))
            damage *= 2;
        if (damage > 0 && (PoisonOnHit || CombatModel.RollChance(PoisonChance, rng)))
        {
            // Native: Buff_Duration=1 and Tick_Damage_Per_Second=TotalDamage*0.2.
            // C02: one "poison" instance per source; a fresh instance (full 1s) is
            // stronger by duration than a partially ticked one, so re-application
            // replaces it (new DPS, full duration) per Buff.IsStrongerThan.
            target.Buffs.Add(new BuffInstance
            {
                DefinitionId = "poison",
                Source = "player",
                Duration = PoisonSeconds,
                Remaining = PoisonSeconds,
                TickDps = damage * PoisonHitDamageFactor,
            });
        }
        // C03: fork/chain now live in the projectile flight sim (CombatInstance.Projectiles);
        // ApplyOnHit no longer substitutes nearby enemies.
        return damage;
    }

    public const double PoisonSeconds = 1.0;
    public const double PoisonHitDamageFactor = 0.2;

    /// <summary>C02: ticks every monster's buff container. DoT buffs deal their
    /// TickDps*elapsed as damage; elapsed is clamped to the remaining duration so a
    /// critical tick never double-counts or drops the partial tick.</summary>
    private void TickMonsterBuffs(double dt)
    {
        foreach (var monster in monsters) TickOneMonsterBuffs(monster, dt);
        if (boss is { Alive: true }) TickOneMonsterBuffs(boss, dt);
    }

    private void TickOneMonsterBuffs(CombatMonster monster, double dt)
    {
        if (monster is null || !monster.Alive) return;
        monster.Buffs.Tick(dt, (buff, elapsed) =>
        {
            if (buff.DefinitionId != "poison") return;
            // DebuffPoisoned.DoWork sends Tick_Damage_Per_Second*dt as poison damage.
            // DoT cannot roll a new hit/crit, poison, fork or chain, or use the one-damage hit floor.
            var damage = CombatModel.EffectiveElementalDamage(buff.TickDps * elapsed, monster.Resistances.Poison);
            DamageMonster(monster, damage);
        });
    }

    private void DamageMonster(CombatMonster monster, double damage)
    {
        if (!monster.Alive) return;
        monster.Hp -= damage;
        if (monster.Hp > 0) return;
        monster.Alive = false;
        monster.Hp = 0;
        monster.Buffs.Clear();
        OnMonsterDeath(monster);
    }

    private LootDrop RollDrop(int level, int minRarity)
    {
        var slot = DropSlots[(int)(rng.NextDouble() * DropSlots.Length) % DropSlots.Length];
        var rarity = RollRarity(minRarity);
        return new LootDrop(slot, rarity, Math.Max(1, level), false, rng.NextUInt64(), LootTableName, ClassId);
    }

    private int RollRarity(int minRarity)
    {
        var total = 0;
        for (var i = minRarity; i < RarityWeights.Length; i++) total += RarityWeights[i];
        var roll = rng.NextDouble() * total;
        for (var i = minRarity; i < RarityWeights.Length; i++)
        {
            roll -= RarityWeights[i];
            if (roll <= 0) return i;
        }
        return RarityWeights.Length - 1;
    }

    private void LevelUpIfNeeded()
    {
        var newLevel = Progression.LevelForExperience(Experience);
        if (newLevel <= PlayerLevel) return;
        var gained = newLevel - PlayerLevel;
        PlayerLevel = newLevel;
        Offense += 1.5 * gained;
        PlayerMaxHp = EffectiveMaxHealth();
        PlayerHp = PlayerMaxHp;
    }

    /// <summary>Grants experience that does not come from a kill (offline/administrative) and
    /// applies any level-ups. Returns the amount actually granted.</summary>
    public double GrantExperience(double amount)
    {
        if (double.IsNaN(amount) || amount <= 0 || PlayerHp <= 0) return 0;
        Experience += amount;
        LevelUpIfNeeded();
        Version++;
        return amount;
    }

    /// <summary>Switches the monster archetypes (e.g. entering/leaving a Niflheim portal) and
    /// restarts the current wave. Progress (experience/currency/kills) is preserved. When
    /// <paramref name="packs"/> is given it sizes the wave and the boss goal from the portal's
    /// recovered NumMonsterPacks attribute.</summary>
    public void SetWorld(IReadOnlyList<MonsterProfile> worldProfiles, int? packs = null)
    {
        if (worldProfiles is { Count: > 0 }) profiles = worldProfiles.ToArray();
        boss = null;
        dungeonKills = 0;
        BossKillGoal = DefaultBossKillGoal;
        if (packs is > 0)
        {
            // Recovered Niflheim run: TotalPacks packs, each a stochastic 2..4 monsters.
            packMode = true;
            totalPacks = Math.Max(2, packs.Value);
            packsCleared = 0;
            packSizeRemainder = 0;
            packIndex = 0;
            SpawnPack();
        }
        else
        {
            packMode = false;
            totalPacks = 0;
            packsCleared = 0;
            monsterCount = 5;
            SpawnMonsters();
        }
        Version++;
    }

    // ------------------------------------------------------------------ commands

    public void MoveTo(double x, double z)
    {
        targetX = Math.Clamp(x, -ArenaHalf, ArenaHalf);
        targetZ = Math.Clamp(z, -ArenaHalf, ArenaHalf);
        hasTarget = true;
        Version++;
    }

    /// <summary>Cast active skill <paramref name="skillId"/> (index into the class kit).</summary>
    public SkillOutcome UseSkill(int skillId = 0)
    {
        if (PlayerHp <= 0) return new SkillOutcome(false, 0, -1, "dead");
        if (skillId < 0 || skillId >= skills.Length) return new SkillOutcome(false, 0, -1, "unknown_skill");
        if (skillCooldowns[skillId] > 0) return new SkillOutcome(false, 0, -1, "cooldown");
        var skill = skills[skillId];
        if (PlayerMana < skill.ManaCost) return new SkillOutcome(false, 0, -1, "no_mana");
        // C06 batch 3: Sacrifice — next offensive spell +40% (consumed).
        var sacrifice = PlayerBuffs.MagnitudeOf("sacrifice-dmg", "Sacrifice");
        if (sacrifice > 0 && skill.Effect is "strike" or "nova" or "projectile" or "chain")
        {
            skill = skill with { Multiplier = skill.Multiplier * (1 + sacrifice) };
            PlayerBuffs.Remove("sacrifice-dmg", "Sacrifice");
            EmitEvent("buff", -1, 0, "consume sacrifice");
        }

        switch (skill.Effect)
        {
            case "nova":
            {
                // C05: PoisonCloud is a persistent cloud, not an instant nova.
                if (skill.Name == "PoisonCloud")
                {
                    var cloudTarget = NearestAliveMonster(Math.Max(skill.Radius, 8));
                    if (cloudTarget is null) return new SkillOutcome(false, 0, -1, "no_target");
                    BeginCast(skillId, skill);
                    SpawnPoisonCloud(skill, cloudTarget.X, cloudTarget.Z);
                    Version++;
                    return new SkillOutcome(true, 0, cloudTarget.Index, "ok");
                }
                // C06: Whirlwind is a channeled AoE, not an instant nova.
                if (skill.Name == "Whirlwind")
                {
                    BeginCast(skillId, skill);
                    StartWhirlwind(skill);
                    Version++;
                    return new SkillOutcome(true, 0, -1, "ok");
                }
                // C06: Blizzard/ElementalSeal are persistent ground effects.
                if (skill.Name == "Blizzard" || skill.Name == "ElementalSeal")
                {
                    var groundTarget = NearestAliveMonster(Math.Max(skill.Radius, 8));
                    if (groundTarget is null) return new SkillOutcome(false, 0, -1, "no_target");
                    BeginCast(skillId, skill);
                    SpawnGroundEffect(skill, groundTarget.X, groundTarget.Z, skill.Name);
                    Version++;
                    return new SkillOutcome(true, 0, groundTarget.Index, "ok");
                }
                // C06: Slam is a line attack (15y), not a circular nova.
                if (skill.Name == "Slam")
                {
                    var slamTarget = NearestAliveMonster(15);
                    if (slamTarget is null) return new SkillOutcome(false, 0, -1, "no_target");
                    BeginCast(skillId, skill);
                    var lineTotal = HitLine(skill, slamTarget, 15.0, 1.0);
                    Version++;
                    return new SkillOutcome(true, lineTotal, slamTarget.Index, "ok");
                }
                // C06 batch 3: Retaliation (buff), Intimidate (AoE debuff), Decay (ground AoE).
                if (skill.Name == "Retaliation")
                {
                    BeginCast(skillId, skill);
                    ApplyRetaliation(skill);
                    Version++;
                    return new SkillOutcome(true, 0, -1, "ok");
                }
                if (skill.Name == "Intimidate")
                {
                    BeginCast(skillId, skill);
                    ApplyIntimidate(skill);
                    Version++;
                    return new SkillOutcome(true, 0, -1, "ok");
                }
                if (skill.Name == "Decay")
                {
                    var decayTarget = NearestAliveMonster(Math.Max(skill.Radius, 8));
                    if (decayTarget is null) return new SkillOutcome(false, 0, -1, "no_target");
                    BeginCast(skillId, skill);
                    SpawnGroundEffect(skill, decayTarget.X, decayTarget.Z, "Decay");
                    Version++;
                    return new SkillOutcome(true, 0, decayTarget.Index, "ok");
                }
                // C06 batch 3: Thunderstrike — 4s storm, 30y radius. No damage multiplier
                // recovered; uses provisional 1.0x per strike (needs client research).
                if (skill.Name == "Thunderstrike")
                {
                    BeginCast(skillId, skill);
                    SpawnGroundEffect(skill, PlayerX, PlayerZ, "Thunderstrike");
                    // Override to 1.0x provisional (no multiplier in data).
                    var storm = clouds[^1];
                    storm.DamageMult = 1.0;
                    storm.TickInterval = 1.0;
                    Version++;
                    return new SkillOutcome(true, 0, -1, "ok");
                }
                var (total, first, hit) = HitNova(skill, skill.Radius);
                if (first < 0) return new SkillOutcome(false, 0, -1, "no_target");
                BeginCast(skillId, skill);
                var stun = StunSecondsOf(skill);
                if (stun > 0)
                    foreach (var monster in hit)
                        monster.StunTimer = Math.Max(monster.StunTimer, stun);
                Version++;
                return new SkillOutcome(true, total, first, "ok");
            }
            case "chain":
            {
                var (total, first) = HitChain(skill, Math.Max(skill.Radius, 8), Math.Max(1, ChainsOf(skill)));
                if (first < 0) return new SkillOutcome(false, 0, -1, "no_target");
                BeginCast(skillId, skill);
                Version++;
                return new SkillOutcome(true, total, first, "ok");
            }
            case "summon":
            case "mobility":
            case "shield":
            case "aura":
            case "rally":
            {
                BeginCast(skillId, skill);
                // C06: Teleport is a blink, not a rally buff (fixes the C04 suspicious mapping).
                if (skill.Name == "Teleport")
                {
                    Blink(skill);
                    Version++;
                    return new SkillOutcome(true, 0, -1, "ok");
                }
                // C06: Mark of the Chosen is a damage-taken debuff on a target.
                if (skill.Name == "MarkOfTheChosen")
                {
                    var markTarget = NearestAliveMonster(14);
                    if (markTarget is null) return new SkillOutcome(false, 0, -1, "no_target");
                    ApplyMark(markTarget, skill);
                    Version++;
                    return new SkillOutcome(true, 0, markTarget.Index, "ok");
                }
                // C06: Thorns is a reflect + damage-reduction buff.
                if (skill.Name == "Thorns")
                {
                    ApplyThorns(skill);
                    Version++;
                    return new SkillOutcome(true, 0, -1, "ok");
                }
                // C06 batch 3: Fade (evasion buff), AstralWalk (move buff), Backflip (mirror).
                if (skill.Name == "Fade")
                {
                    ApplyFade(skill);
                    Version++;
                    return new SkillOutcome(true, 0, -1, "ok");
                }
                if (skill.Name == "AstralWalk")
                {
                    ApplyAstralWalk(skill);
                    Version++;
                    return new SkillOutcome(true, 0, -1, "ok");
                }
                if (skill.Name == "Backflip")
                {
                    Backflip(skill);
                    Version++;
                    return new SkillOutcome(true, 0, -1, "ok");
                }
                // C06 batch 3: Necromancer buffs.
                if (skill.Name == "BoneLink")
                {
                    ApplyBoneLink(skill);
                    Version++;
                    return new SkillOutcome(true, 0, -1, "ok");
                }
                if (skill.Name == "DemonicPresence")
                {
                    ApplyDemonicPresence(skill);
                    Version++;
                    return new SkillOutcome(true, 0, -1, "ok");
                }
                if (skill.Name == "Sacrifice")
                {
                    ApplySacrifice(skill);
                    Version++;
                    return new SkillOutcome(true, 0, -1, "ok");
                }
                if (skill.Name == "UnholyFocus")
                {
                    ApplyUnholyFocus(skill);
                    Version++;
                    return new SkillOutcome(true, 0, -1, "ok");
                }
                // C06 batch 3: DrainLife is a channeled drain (not a summon).
                if (skill.Name == "DrainLife")
                {
                    var drainTarget = NearestAliveMonster(14);
                    if (drainTarget is null) return new SkillOutcome(false, 0, -1, "no_target");
                    BeginCast(skillId, skill);
                    StartDrainLife(skill, drainTarget);
                    Version++;
                    return new SkillOutcome(true, 0, drainTarget.Index, "ok");
                }
                HealPercent(skill.HealPercent);
                if (skill.Effect == "shield" && skill.Values.TryGetValue("Mana_Shield_Life_Factor", out var lifeFactor))
                {
                    GainShield(lifeFactor);
                    // C06 batch 3: ManaShield absorption factor.
                    if (skill.Name == "ManaShield" && skill.Values.TryGetValue("Mana_Shield_Damage_Absorbtion_Factor", out var absorb))
                    {
                        PlayerBuffs.Add(new BuffInstance
                        {
                            DefinitionId = "manashield",
                            Source = "ManaShield",
                            Duration = 30, // Provisional: until shield depleted.
                            Remaining = 30,
                            Magnitude = absorb,
                        });
                    }
                }
                if (skill.Effect == "summon")
                {
                    // C05: SummonSkeleton spawns real minion entities; other summon
                    // skills keep the provisional offense-buff fallback (C06).
                    if (skill.Name == "SummonSkeleton")
                        SpawnSkeletons(skill);
                    else
                        SummonMinions(skill);
                }
                else
                    ApplyOffenseBuff(skill.BuffBonus, skill.BuffSeconds);
                if (skill.Effect == "mobility")
                {
                    var speed = skill.Values.TryGetValue("Movement_Speed_Bonus_Percent", out var s) ? s / 100.0 : 0.25;
                    ApplyMoveSpeedBuff(speed, skill.BuffSeconds);
                }
                Version++;
                return new SkillOutcome(true, 0, -1, "ok");
            }
            case "projectile":
            {
                // C06: Tornado is a persistent moving vortex (2y radius, 7s).
                if (skill.Name == "Tornado")
                {
                    var tornadoTarget = NearestAliveMonster(14);
                    if (tornadoTarget is null) return new SkillOutcome(false, 0, -1, "no_target");
                    BeginCast(skillId, skill);
                    var tornadoTotal = HitTornado(skill, tornadoTarget);
                    Version++;
                    return new SkillOutcome(true, tornadoTotal, tornadoTarget.Index, "ok");
                }
                // C06 batch 3: IceBlast/Shadowbolt/InfernalBlast are explosive.
                if (skill.Name == "IceBlast" || skill.Name == "Shadowbolt" || skill.Name == "InfernalBlast")
                {
                    BeginCast(skillId, skill);
                    var explosionMult = skill.Values.TryGetValue("Power_Weapon_Damage_Multiplier_2", out var m2) ? m2 : skill.Multiplier;
                    var (expTotal, expFirst) = HitExplosiveProjectile(skill, explosionMult);
                    if (expFirst < 0) return new SkillOutcome(false, 0, -1, "no_target");
                    // InfernalBlast: 4s burn (poison DoT mechanics, fire flavor).
                    if (skill.Name == "InfernalBlast")
                    {
                        var target = monsters.FirstOrDefault(m => m.Index == expFirst)
                            ?? (boss is { Alive: true } && boss.Index == expFirst ? boss : null);
                        if (target is not null && target.Alive)
                        {
                            target.Buffs.Add(new BuffInstance
                            {
                                DefinitionId = "poison",
                                Source = "InfernalBlast",
                                Duration = 4.0,
                                Remaining = 4.0,
                                TickDps = PlayerDamageBundle().Total * 0.2,
                            });
                            EmitEvent("debuff", target.Index, 0, "burn InfernalBlast");
                        }
                    }
                    Version++;
                    return new SkillOutcome(true, expTotal, expFirst, "ok");
                }
                // C06 batch 3: ManaArrows grants charges; the projectile consumes one.
                if (skill.Name == "ManaArrows")
                {
                    var charges = PlayerBuffs.MagnitudeOf("manaarrows", "ManaArrows");
                    if (charges <= 0)
                    {
                        // No charges: grant them (first cast) instead of firing.
                        BeginCast(skillId, skill);
                        ApplyManaArrows(skill);
                        Version++;
                        return new SkillOutcome(true, 0, -1, "ok");
                    }
                    // Consume a charge and fire.
                    var buff = PlayerBuffs.Get("manaarrows", "ManaArrows");
                    if (buff is not null) buff.Magnitude -= 1;
                }
                var (total, first) = HitProjectile(skill, 14);
                if (first < 0) return new SkillOutcome(false, 0, -1, "no_target");
                BeginCast(skillId, skill);
                // C06 batch 3: ManaArrows grants 4 mana per hit (provisional: per cast).
                if (skill.Name == "ManaArrows")
                {
                    var manaPerHit = skill.Values.TryGetValue("Power_Mana_Arrows_Mana_Per_Hit", out var mph) ? mph : 4.0;
                    PlayerMana = Math.Min(PlayerMaxMana, PlayerMana + manaPerHit);
                }
                // C06: FrozenArrow explodes in 4y on hit (primary excluded, already hit).
                if (skill.Name == "FrozenArrow")
                {
                    var primary = monsters.FirstOrDefault(m => m.Index == first)
                        ?? (boss is { Alive: true } && boss.Index == first ? boss : null);
                    if (primary is not null)
                    {
                        foreach (var m in AllCombatMonsters())
                        {
                            if (!m.Alive || ReferenceEquals(m, primary)) continue;
                            var d = Math.Sqrt(Math.Pow(m.X - primary.X, 2) + Math.Pow(m.Z - primary.Z, 2));
                            if (d > skill.Radius) continue;
                            total += HitTarget(m, skill, skill.Multiplier);
                        }
                        EmitEvent("projectile", first, 0, $"explode FrozenArrow {primary.X:F1},{primary.Z:F1}");
                    }
                }
                Version++;
                return new SkillOutcome(true, total, first, "ok");
            }
            default:
            {
                // C06: ImpalingTrap places a trap, it is not a direct strike.
                if (skill.Name == "ImpalingTrap")
                {
                    var trapTarget = NearestAliveMonster(14);
                    if (trapTarget is null) return new SkillOutcome(false, 0, -1, "no_target");
                    BeginCast(skillId, skill);
                    PlaceTrap(skill, trapTarget.X, trapTarget.Z);
                    Version++;
                    return new SkillOutcome(true, 0, trapTarget.Index, "ok");
                }
                var target = NearestAliveMonster(8);
                if (target is null) return new SkillOutcome(false, 0, -1, "no_target");
                BeginCast(skillId, skill);
                var dealt = HitTarget(target, skill, skill.Multiplier);
                var stun = StunSecondsOf(skill);
                if (stun > 0) target.StunTimer = Math.Max(target.StunTimer, stun);
                Version++;
                return new SkillOutcome(true, dealt, target.Index, "ok");
            }
        }
    }

    private void BeginCast(int skillId, SkillProfile skill)
    {
        skillCooldowns[skillId] = skill.Cooldown;
        PlayerMana = Math.Max(0, PlayerMana - skill.ManaCost);
    }

    private static double StunSecondsOf(SkillProfile skill)
    {
        if (skill.Values.TryGetValue("Power_Freeze_Duration", out var freeze)) return freeze;
        if (skill.Values.TryGetValue("Power_War_Stomp_Stun_Duration", out var stun)) return stun;
        return 0;
    }

    private DamageResult ResolveSkill(CombatMonster target, SkillProfile skill, double multiplier)
    {
        // A skill's element overrides the weapon's own split ("X% weapon damage as <element>").
        var weapon = PlayerDamageBundle();
        var bundle = SkillDamageTypes.For(skill.Name) switch
        {
            "Fire" => new DamageBundle(Fire: weapon.Total),
            "Cold" => new DamageBundle(Cold: weapon.Total),
            "Lightning" => new DamageBundle(Lightning: weapon.Total),
            "Poison" => new DamageBundle(Poison: weapon.Total),
            _ => weapon,
        };
        // C06: Mark of the Chosen amplifies the target's taken damage.
        var defender = MonsterStats(target) with
        {
            DamageTakenAmplifyPercent = target.Buffs.MagnitudeOf("mark", "MarkOfTheChosen") * 100.0,
        };
        return CombatModel.ResolveBundleAttack(
            PlayerStats(), bundle, defender, target.Resistances,
            new AttackProfile(multiplier, 0.15, 2.0, 0.08),
            rng);
    }

    private double Distance(CombatMonster monster)
        => Math.Sqrt(Math.Pow(monster.X - PlayerX, 2) + Math.Pow(monster.Z - PlayerZ, 2));

    private static int ChainsOf(SkillProfile skill)
        => skill.Values.TryGetValue("ChainLightning_Max_Num_Chains", out var chains) ? (int)chains : 0;

    /// <summary>Classification of a skill's special mechanic from its recovered attributes.</summary>
    private static string SpecialOf(SkillProfile skill)
    {
        foreach (var key in skill.Values.Keys)
        {
            if (key.Contains("Freeze")) return "freeze";
            if (key.Contains("Stun")) return "stun";
            if (key.Contains("Life_Leech")) return "leech";
            if (key.StartsWith("Minion_Inheritance") || key.Contains("Minion_Duration")) return "summon";
            if (key.Contains("Movement_Speed")) return "mobility";
            if (key.Contains("Mana_Shield")) return "shield";
            if (key.Contains("Pierce")) return "pierce";
            if (key.Contains("Num_Chains")) return "chain";
        }
        return "";
    }

    private List<CombatMonster> AliveMonstersInRadius(double radius)
    {
        var rows = new List<CombatMonster>();
        foreach (var monster in monsters)
        {
            if (!monster.Alive) continue;
            if (Math.Sqrt(Math.Pow(monster.X - PlayerX, 2) + Math.Pow(monster.Z - PlayerZ, 2)) <= radius)
                rows.Add(monster);
        }
        if (boss is { Alive: true } &&
            Math.Sqrt(Math.Pow(boss.X - PlayerX, 2) + Math.Pow(boss.Z - PlayerZ, 2)) <= radius)
            rows.Add(boss);
        return rows;
    }

    public CombatSnapshot Snapshot()
    {
        var rows = new List<MonsterSnapshot>(monsters.Count + 1);
        foreach (var monster in monsters)
            rows.Add(new MonsterSnapshot(monster.Index, monster.Name, monster.Level, false,
                Math.Round(monster.X, 3), Math.Round(monster.Z, 3), Math.Round(monster.Hp, 2), Math.Round(monster.MaxHp, 2), monster.Alive, Math.Round(monster.StunTimer, 2),
                monster.BrainAction ?? ""));
        if (boss is { Alive: true })
            rows.Add(new MonsterSnapshot(boss.Index, boss.Name, boss.Level, true,
                Math.Round(boss.X, 3), Math.Round(boss.Z, 3), Math.Round(boss.Hp, 2), Math.Round(boss.MaxHp, 2), true, Math.Round(boss.StunTimer, 2)));

        return new CombatSnapshot(Version, Math.Round(PlayerX, 3), Math.Round(PlayerZ, 3),
            Math.Round(PlayerHp, 2), Math.Round(PlayerMaxHp, 2),
            Math.Round(PlayerMana, 2), Math.Round(PlayerMaxMana, 2), Math.Round(PlayerShield, 2),
            PlayerLevel, Experience, Silver, Opals,
            Kills,
            skills.Select((s, i) => new SkillStatus(s.Slot, s.Name, s.Effect, Math.Round(skillCooldowns[i], 2), s.Cooldown, s.ManaCost, s.Confidence, ChainsOf(s), SpecialOf(s))).ToList(),
            Math.Round(PlayerBuffs.RemainingOf("offense"), 2),
            DungeonsCleared, Math.Max(0, BossKillGoal - dungeonKills), boss is { Alive: true }, rows,
            PacksRemaining, TotalPacks);
    }

    private void ClampToArena()
    {
        PlayerX = Math.Clamp(PlayerX, -ArenaHalf, ArenaHalf);
        PlayerZ = Math.Clamp(PlayerZ, -ArenaHalf, ArenaHalf);
    }
}
