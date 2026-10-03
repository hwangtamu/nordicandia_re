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
    bool Champion = false);

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
    /// <summary>Remaining poison duration; while &gt; 0 the target counts as poisoned.</summary>
    public double PoisonTimer { get; set; }
    /// <summary>Poison damage per second while <see cref="PoisonTimer"/> is active.</summary>
    public double PoisonDps { get; set; }
    public bool Alive { get; set; } = true;
    public double RespawnTimer { get; set; }
    public double WanderTimer { get; set; }
    public double TargetX { get; set; }
    public double TargetZ { get; set; }
}

/// <summary>A drop rolled on a kill. Slot/rarity/level are resolved into a concrete item by
/// the host's loot table so this layer stays content-free and deterministic.</summary>
public readonly record struct LootDrop(int Slot, int Rarity, int Level, bool Boss, ulong Seed);

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
public sealed class CombatInstance
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
    private double attackCooldown;
    private readonly double[] skillCooldowns;
    private double offenseBuffTimer;
    private double offenseBuffBonus;
    private double moveSpeedBuffTimer;
    private double moveSpeedBuffBonus;
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

    // Recovered MonsterCurse* debuffs on the player. Each has a remaining duration and magnitude;
    // the strongest active curse wins. Attributes/targets are ClientVerified, magnitudes/durations
    // are Provisional (they come from monster-skill definitions).
    private double curseSlowTimer, curseSlowAmount;
    private double curseResistTimer, curseResistAmount;
    private double curseAmplifyTimer, curseAmplifyAmount;
    private double curseReduceDamageTimer, curseReduceDamageAmount;
    private double curseLeechTimer, curseLeechAmount;
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
    public int PlayerLevel { get; private set; }
    public double Experience { get; private set; }
    public int Silver { get; private set; }
    public int Opals { get; private set; }
    public int Kills { get; private set; }
    public int DungeonsCleared { get; private set; }
    public long Version { get; private set; }

    public IReadOnlyList<CombatMonster> Monsters => monsters;
    private readonly List<CombatMonster> monsters = new();

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
        long initialVersion = 1)
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
        // Base 6.5 u/s scaled by the recovered Movement_Speed total (Aesir Tyr +40% etc.).
        playerSpeed = 6.5 * Math.Clamp(stats.MoveSpeedMultiplier, 0.1, 10.0);
    }

    private CombatantStats PlayerStats() => new(EffectiveOffense(), Defense, Recovery, PlayerLevel,
        AttackRating, Armor, Evasion, CritChance, LifeMax, ManaMax, Damage, Resistances);

    /// <summary>The player's typed damage (weapon bundle, or a physical bundle from the
    /// provisional Offense when no weapon attributes are present), including the passive/buff bonus.</summary>
    private DamageBundle PlayerDamageBundle()
    {
        var baseBundle = Damage.Total > 0 ? Damage : new DamageBundle(Offense);
        // Weapon_Damage_Percent_Bonus_Final reduction from MonsterCurseReducedWeaponDamage.
        return baseBundle.Scale((1 + PassiveOffense() + (offenseBuffTimer > 0 ? offenseBuffBonus : 0))
            * (1 - curseReduceDamageAmount));
    }

    private static CombatantStats MonsterStats(CombatMonster monster)
        => new(monster.Offense, monster.Defense, 0, monster.Level, Armor: monster.Armor, Resistances: monster.Resistances);

    private double EffectiveMaxHealth() => CombatModel.MaxHealth(PlayerStats())
        * (1 + PassiveHealth());

    /// <summary>Mana pool: the recovered Mana_Max_Total when available, else 40 + 10/level.</summary>
    private double EffectiveMaxMana() => ManaMax > 0 ? ManaMax : 40 + 10 * Math.Max(1, PlayerLevel);

    private double PassiveOffense() => passives.Sum(p => p.OffenseBonus);

    private double PassiveHealth() => passives.Sum(p => p.HealthBonus);

    private double EffectiveOffense() => (Damage.Total > 0 ? Damage.Total : Offense)
        * (1 + PassiveOffense() + (offenseBuffTimer > 0 ? offenseBuffBonus : 0));

    /// <summary>Replace the equipped loadout (e.g. after a mastery point or loadout change).</summary>
    public void UpdatePowers(ClassPowerPool powers)
    {
        skills = powers.Active.Take(MaxActiveSkills).ToArray();
        passives = powers.Passive.Take(MaxPassiveSkills).ToArray();
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
            var angle = (i / (double)monsterCount) * Math.PI * 2;
            var radius = 7 + (i % 3) * 3;
            monsters.Add(CreateMonster(i, Math.Cos(angle) * radius, Math.Sin(angle) * radius, alive: true));
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
        // Client packs spawn inside one of the dungeon's spawn areas; the web picks one of a
        // ring of zones and clusters the pack there.
        var zone = (int)(rng.NextDouble() * SpawnZones.Length) % SpawnZones.Length;
        var (cx, cz) = SpawnZones[zone];
        packOriginX = cx;
        packOriginZ = cz;
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
        monsters.Add(CreateMonster(index, x, z, alive: true));
    }

    private CombatMonster CreateMonster(int index, double x, double z, bool alive, MonsterProfile? overrideProfile = null)
    {
        var level = MonsterLevel;
        var profile = overrideProfile ?? profiles[index % profiles.Length];
        var maxHp = (30 + level * 16) * profile.HpMult;
        var offense = (3 + level * 1.2) * profile.OffenseMult;
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
            Defense = (2 + level * 1.5) * profile.DefenseMult,
            Armor = (10 + level * 6) * profile.DefenseMult,
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
            MaxHp = 200 + level * 80,
            Hp = 200 + level * 80,
            Offense = 8 + level * 3,
            Defense = 20 + level * 8,
            Armor = 60 + level * 20,
            Damage = new DamageBundle(Physical: 0.5, Cold: 0.5).Scale(8 + level * 3),
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
        TickPoison(dt);
        TickCurses(dt);
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
        offenseBuffTimer = Math.Max(0, offenseBuffTimer - dt);
        moveSpeedBuffTimer = Math.Max(0, moveSpeedBuffTimer - dt);
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
                var speed = playerSpeed * (1 + (moveSpeedBuffTimer > 0 ? moveSpeedBuffBonus : 0)) * (1 - curseSlowAmount);
                var step = Math.Min(distance, speed * dt);
                PlayerX += dx / distance * step;
                PlayerZ += dz / distance * step;
                ClampToArena();
            }
        }

        var target = NearestAliveMonster(PlayerAttackRange);
        if (target is not null && attackCooldown <= 0)
        {
            attackCooldown = PlayerAttackInterval;
            var hit = CombatModel.ResolveBundleAttack(
                PlayerStats(), PlayerDamageBundle(), MonsterStats(target), target.Resistances,
                new AttackProfile(1.0, 0.08, 1.6, 0.12),
                rng);
            DamageMonster(target, ApplyOnHit(target, hit, autoAttack: true));
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
                    monster.PoisonTimer = 0;
                    monster.PoisonDps = 0;
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

    /// <summary>Applies a {@link MonsterCurse*} debuff to the player. The strongest active curse
    /// of each kind wins and refreshes the duration.</summary>
    private void ApplyCurse(CombatMonster monster, MonsterPower power)
    {
        switch (power.Curse)
        {
            case CurseEffect.Slow:
            case CurseEffect.SlowProjectiles:
                curseSlowAmount = Math.Max(curseSlowAmount, power.DamageMultiplier);
                curseSlowTimer = Math.Max(curseSlowTimer, power.Duration);
                break;
            case CurseEffect.LowerResistances:
                curseResistAmount = Math.Max(curseResistAmount, power.DamageMultiplier);
                curseResistTimer = Math.Max(curseResistTimer, power.Duration);
                break;
            case CurseEffect.AmplifyDamageTaken:
                curseAmplifyAmount = Math.Max(curseAmplifyAmount, power.DamageMultiplier);
                curseAmplifyTimer = Math.Max(curseAmplifyTimer, power.Duration);
                break;
            case CurseEffect.ReducedWeaponDamage:
                curseReduceDamageAmount = Math.Max(curseReduceDamageAmount, power.DamageMultiplier);
                curseReduceDamageTimer = Math.Max(curseReduceDamageTimer, power.Duration);
                break;
            case CurseEffect.Leech:
                curseLeechAmount = Math.Max(curseLeechAmount, power.DamageMultiplier);
                curseLeechTimer = Math.Max(curseLeechTimer, power.Duration);
                curseLeechSource = monster;
                break;
        }
        monster.SpecialCooldown = power.Cooldown;
    }

    private void TickCurses(double dt)
    {
        curseSlowTimer = Math.Max(0, curseSlowTimer - dt);
        if (curseSlowTimer <= 0) curseSlowAmount = 0;
        curseResistTimer = Math.Max(0, curseResistTimer - dt);
        if (curseResistTimer <= 0) curseResistAmount = 0;
        curseAmplifyTimer = Math.Max(0, curseAmplifyTimer - dt);
        if (curseAmplifyTimer <= 0) curseAmplifyAmount = 0;
        curseReduceDamageTimer = Math.Max(0, curseReduceDamageTimer - dt);
        if (curseReduceDamageTimer <= 0) curseReduceDamageAmount = 0;
        curseLeechTimer = Math.Max(0, curseLeechTimer - dt);
        if (curseLeechTimer <= 0) { curseLeechAmount = 0; curseLeechSource = null; }
    }

    public double CurseSlow => curseSlowTimer > 0 ? curseSlowAmount : 0;
    public double CurseResistancePenalty => curseResistTimer > 0 ? curseResistAmount : 0;
    public double CurseAmplifyDamageTaken => curseAmplifyTimer > 0 ? curseAmplifyAmount : 0;
    public double CurseWeaponDamageReduction => curseReduceDamageTimer > 0 ? curseReduceDamageAmount : 0;

    private ResistanceBundle EffectivePlayerResistances()
        => curseResistTimer > 0
            ? new ResistanceBundle(
                Resistances.Fire - curseResistAmount, Resistances.Cold - curseResistAmount,
                Resistances.Lightning - curseResistAmount, Resistances.Poison - curseResistAmount)
            : Resistances;

    /// <summary>Shield, amplify-damage-taken, leech and death for one hit on the player.</summary>
    private void ApplyPlayerDamage(CombatMonster source, double incoming)
    {
        if (curseAmplifyTimer > 0) incoming *= 1 + curseAmplifyAmount;
        if (PlayerShield > 0)
        {
            var absorbed = Math.Min(PlayerShield, incoming);
            PlayerShield -= absorbed;
            incoming -= absorbed;
        }
        PlayerHp = Math.Max(0, PlayerHp - incoming);
        if (source is not null && curseLeechTimer > 0 && ReferenceEquals(curseLeechSource, source))
            source.Hp = Math.Min(source.MaxHp, source.Hp + incoming * curseLeechAmount);
        if (PlayerHp <= 0)
        {
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
        var step = Math.Min(dist, monster.Speed * 3 * dt);
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
        var step = Math.Min(gdist, monster.Speed * dt);
        monster.X = Math.Clamp(monster.X + gx / gdist * step, -ArenaHalf, ArenaHalf);
        monster.Z = Math.Clamp(monster.Z + gz / gdist * step, -ArenaHalf, ArenaHalf);
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
                monster.AttackCooldown = monster.AttackInterval;
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
                var step = Math.Min(monster.PreferredDistance - distance + 1.0, monster.Speed * dt);
                monster.X = Math.Clamp(monster.X - dx / distance * step, -ArenaHalf, ArenaHalf);
                monster.Z = Math.Clamp(monster.Z - dz / distance * step, -ArenaHalf, ArenaHalf);
            }
            return;
        }

        double goalX, goalZ;
        if (distance <= MonsterAggroRange)
        {
            goalX = PlayerX;
            goalZ = PlayerZ;
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
            var step = Math.Min(gdist, monster.Speed * dt);
            monster.X += gx / gdist * step;
            monster.Z += gz / gdist * step;
            monster.X = Math.Clamp(monster.X, -ArenaHalf, ArenaHalf);
            monster.Z = Math.Clamp(monster.Z, -ArenaHalf, ArenaHalf);
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
        if (hit.Critical && target.PoisonTimer > 0 && CombatModel.RollChance(DoubleDamageOnCritPoisoned, rng))
            damage *= 2;
        if (damage > 0 && (PoisonOnHit || CombatModel.RollChance(PoisonChance, rng)))
        {
            // Native: Buff_Duration=1 and Tick_Damage_Per_Second=TotalDamage*0.2.
            // The web retains one strongest poison; full BuffManager replacement is not ported.
            target.PoisonTimer = Math.Max(target.PoisonTimer, PoisonSeconds);
            target.PoisonDps = Math.Max(target.PoisonDps, damage * PoisonHitDamageFactor);
        }
        if (autoAttack && ProjectileAutoAttack)
        {
            var fork = CombatModel.RollChance(ForkChance, rng);
            var chain = CombatModel.RollChance(ChainChance, rng);
            // Native forks twice at 0.5x and disables recursion. A successful fork takes
            // precedence over chain. Selection of nearby targets is still a web approximation
            // of the two moving projectiles' collision geometry.
            if (fork || chain)
                foreach (var secondary in SecondaryTargets(target).Take(fork ? 2 : 1).ToArray())
                {
                    var next = CombatModel.ResolveBundleAttack(PlayerStats(), PlayerDamageBundle(),
                        MonsterStats(secondary), secondary.Resistances,
                        new AttackProfile(fork ? 0.5 : 1, 0.08, 1.6, 0.12), rng);
                    DamageMonster(secondary, ApplyOnHit(secondary, next, autoAttack: false));
                }
        }
        return damage;
    }

    private IEnumerable<CombatMonster> SecondaryTargets(CombatMonster primary)
    {
        var candidates = boss is { Alive: true } ? monsters.Append(boss) : monsters;
        return candidates.Where(m => m.Alive && m != primary)
            .Select(m => (Monster: m, Distance: Math.Sqrt(Math.Pow(m.X - primary.X, 2) + Math.Pow(m.Z - primary.Z, 2))))
            .Where(x => x.Distance <= 10).OrderBy(x => x.Distance).ThenBy(x => x.Monster.Index)
            .Select(x => x.Monster);
    }

    public const double PoisonSeconds = 1.0;
    public const double PoisonHitDamageFactor = 0.2;

    private void TickPoison(double dt)
    {
        foreach (var monster in monsters) TickMonsterPoison(monster, dt);
        if (boss is { Alive: true }) TickMonsterPoison(boss, dt);
    }

    private void TickMonsterPoison(CombatMonster monster, double dt)
    {
        if (monster is null || !monster.Alive || monster.PoisonTimer <= 0) return;
        var elapsed = Math.Min(dt, monster.PoisonTimer);
        monster.PoisonTimer = Math.Max(0, monster.PoisonTimer - elapsed);
        // DebuffPoisoned.DoWork sends Tick_Damage_Per_Second*dt as poison damage.
        // DoT cannot roll a new hit/crit, poison, fork or chain, or use the one-damage hit floor.
        var damage = CombatModel.EffectiveElementalDamage(monster.PoisonDps * elapsed, monster.Resistances.Poison);
        DamageMonster(monster, damage);
        if (monster.PoisonTimer <= 0) monster.PoisonDps = 0;
    }

    private void DamageMonster(CombatMonster monster, double damage)
    {
        if (!monster.Alive) return;
        monster.Hp -= damage;
        if (monster.Hp > 0) return;
        monster.Alive = false;
        monster.Hp = 0;
        monster.PoisonTimer = 0;
        monster.PoisonDps = 0;
        Kills++;
        Experience += CombatModel.ExperienceReward(monster.Level);
        LevelUpIfNeeded();

        if (monster.IsBoss)
        {
            boss = null;
            DungeonsCleared++;
            Silver += 50 + monster.Level * 25;
            pendingDrops.Add(RollDrop(monster.Level, minRarity: 4));
            pendingDrops.Add(RollDrop(monster.Level + 2, minRarity: 5));
            dungeonKills = 0;
            return;
        }

        if (rng.NextDouble() < TrashDropChance)
            pendingDrops.Add(RollDrop(monster.Level, minRarity: 0));

        if (packMode)
        {
            // The pack is over once its last member dies (and every member has spawned);
            // then spawn the next pack or finish the run.
            if (pendingPackSize == 0 && monsters.All(m => !m.Alive))
            {
                packsCleared++;
                if (packsCleared >= totalPacks)
                {
                    DungeonsCleared++;
                    Silver += 50 + monster.Level * 25;
                    pendingDrops.Add(RollDrop(monster.Level, minRarity: 4));
                    pendingDrops.Add(RollDrop(monster.Level + 2, minRarity: 5));
                    packMode = false;
                    totalPacks = 0;
                    packsCleared = 0;
                }
                else
                {
                    pendingPackSpawn = true;
                }
            }
            return;
        }

        dungeonKills++;
        if (dungeonKills >= BossKillGoal && boss is null)
            boss = CreateBoss();

        monster.RespawnTimer = MonsterRespawnSeconds;
    }

    private LootDrop RollDrop(int level, int minRarity)
    {
        var slot = DropSlots[(int)(rng.NextDouble() * DropSlots.Length) % DropSlots.Length];
        var rarity = RollRarity(minRarity);
        return new LootDrop(slot, rarity, Math.Max(1, level), false, rng.NextUInt64());
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

        switch (skill.Effect)
        {
            case "nova":
            {
                var inRange = AliveMonstersInRadius(skill.Radius);
                if (inRange.Count == 0) return new SkillOutcome(false, 0, -1, "no_target");
                BeginCast(skillId, skill);
                double total = 0;
                var first = inRange[0].Index;
                var stun = StunSecondsOf(skill);
                foreach (var monster in inRange)
                {
                    var hit = ResolveSkill(monster, skill, skill.Multiplier);
                    var dealt = ApplyOnHit(monster, hit, autoAttack: false);
                    total += dealt;
                    DamageMonster(monster, dealt);
                    if (stun > 0) monster.StunTimer = Math.Max(monster.StunTimer, stun);
                }
                Version++;
                return new SkillOutcome(true, total, first, "ok");
            }
            case "chain":
            {
                var candidates = AliveMonstersInRadius(Math.Max(skill.Radius, 8));
                if (candidates.Count == 0) return new SkillOutcome(false, 0, -1, "no_target");
                BeginCast(skillId, skill);
                candidates.Sort((a, b) => Distance(a).CompareTo(Distance(b)));
                var maxTargets = Math.Max(1, ChainsOf(skill));
                var decay = skill.Values.TryGetValue("Power_Chain_Lightning_Damage_Reduction_Percent", out var d)
                    ? Math.Clamp(1 - d, 0.1, 1.0)
                    : 0.85;
                double total = 0;
                var first = candidates[0].Index;
                var currentMultiplier = skill.Multiplier;
                foreach (var monster in candidates.Take(maxTargets))
                {
                    var hit = ResolveSkill(monster, skill, currentMultiplier);
                    var dealt = ApplyOnHit(monster, hit, autoAttack: false);
                    total += dealt;
                    DamageMonster(monster, dealt);
                    currentMultiplier *= decay;
                }
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
                if (skill.HealPercent > 0)
                    PlayerHp = Math.Min(PlayerMaxHp, PlayerHp + PlayerMaxHp * skill.HealPercent);
                if (skill.Effect == "shield" && skill.Values.TryGetValue("Mana_Shield_Life_Factor", out var lifeFactor))
                    PlayerShield += PlayerMaxHp * lifeFactor;
                var bonus = skill.BuffBonus;
                if (bonus <= 0 && skill.Effect == "summon")
                    bonus = skill.Values.TryGetValue("Minion_Inheritance_Weapon_Damage_Bonus_Percent", out var b) ? b : 0.15;
                if (bonus > 0)
                {
                    offenseBuffTimer = Math.Max(offenseBuffTimer, skill.BuffSeconds);
                    offenseBuffBonus = bonus;
                }
                if (skill.Effect == "mobility")
                {
                    var speed = skill.Values.TryGetValue("Movement_Speed_Bonus_Percent", out var s) ? s / 100.0 : 0.25;
                    moveSpeedBuffTimer = Math.Max(moveSpeedBuffTimer, skill.BuffSeconds);
                    moveSpeedBuffBonus = speed;
                }
                Version++;
                return new SkillOutcome(true, 0, -1, "ok");
            }
            case "projectile":
            {
                // Ranged single-target shot; pierce modifiers add nearby targets.
                var target = NearestAliveMonster(14);
                if (target is null) return new SkillOutcome(false, 0, -1, "no_target");
                BeginCast(skillId, skill);
                var hit = ResolveSkill(target, skill, skill.Multiplier);
                var total = ApplyOnHit(target, hit, autoAttack: false);
                DamageMonster(target, total);
                if (skill.Values.ContainsKey("Power_Projectile_Pierce_Chance") ||
                    skill.Values.ContainsKey("Power_Power_Shot_Pierce_Chance_Percent"))
                {
                    foreach (var extra in AliveMonstersInRadius(6).Where(m => m.Index != target.Index).Take(2))
                    {
                        var pierce = ResolveSkill(extra, skill, skill.Multiplier * 0.5);
                        total += pierce.Damage;
                        DamageMonster(extra, pierce.Damage);
                    }
                }
                Version++;
                return new SkillOutcome(true, total, target.Index, "ok");
            }
            default:
            {
                var target = NearestAliveMonster(8);
                if (target is null) return new SkillOutcome(false, 0, -1, "no_target");
                BeginCast(skillId, skill);
                var hit = ResolveSkill(target, skill, skill.Multiplier);
                var dealt = ApplyOnHit(target, hit, autoAttack: false);
                DamageMonster(target, dealt);
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
        return CombatModel.ResolveBundleAttack(
            PlayerStats(), bundle, MonsterStats(target), target.Resistances,
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
            Math.Round(offenseBuffTimer, 2),
            DungeonsCleared, Math.Max(0, BossKillGoal - dungeonKills), boss is { Alive: true }, rows,
            PacksRemaining, TotalPacks);
    }

    private void ClampToArena()
    {
        PlayerX = Math.Clamp(PlayerX, -ArenaHalf, ArenaHalf);
        PlayerZ = Math.Clamp(PlayerZ, -ArenaHalf, ArenaHalf);
    }
}
