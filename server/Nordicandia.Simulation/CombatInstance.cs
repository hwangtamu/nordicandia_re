namespace Nordicandia.Simulation;

/// <summary>Per-archetype monster tuning so an instance can spawn several kinds of enemy.</summary>
public readonly record struct MonsterProfile(
    string Name, double HpMult = 1.0, double OffenseMult = 1.0, double DefenseMult = 1.0, double Speed = 2.4,
    DamageBundle Damage = default, ResistanceBundle Resistances = default);

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
    int Index, string Name, int Level, bool IsBoss, double X, double Z, double Hp, double MaxHp, bool Alive, double StunTimer);

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
    IReadOnlyList<MonsterSnapshot> Monsters);

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
    public const int BossKillGoal = 8;
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
    private readonly double playerSpeed = 6.5;
    private readonly int monsterCount;
    private readonly MonsterProfile[] profiles;
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
        DoubleDamageOnCritPoisoned = stats.DoubleDamageOnCritPoisoned;
    }

    private CombatantStats PlayerStats() => new(EffectiveOffense(), Defense, Recovery, PlayerLevel,
        AttackRating, Armor, Evasion, CritChance, LifeMax, ManaMax, Damage, Resistances);

    /// <summary>The player's typed damage (weapon bundle, or a physical bundle from the
    /// provisional Offense when no weapon attributes are present), including the passive/buff bonus.</summary>
    private DamageBundle PlayerDamageBundle()
    {
        var baseBundle = Damage.Total > 0 ? Damage : new DamageBundle(Offense);
        return baseBundle.Scale(1 + PassiveOffense() + (offenseBuffTimer > 0 ? offenseBuffBonus : 0));
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
        monsters.Clear();
        for (var i = 0; i < monsterCount; i++)
        {
            var angle = (i / (double)monsterCount) * Math.PI * 2;
            var radius = 7 + (i % 3) * 3;
            monsters.Add(CreateMonster(i, Math.Cos(angle) * radius, Math.Sin(angle) * radius, alive: true));
        }
    }

    private CombatMonster CreateMonster(int index, double x, double z, bool alive)
    {
        var level = MonsterLevel;
        var profile = profiles[index % profiles.Length];
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
                var step = Math.Min(distance, (playerSpeed * (1 + (moveSpeedBuffTimer > 0 ? moveSpeedBuffBonus : 0))) * dt);
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
            ChaseAndAttack(monster, dt);
        }
    }

    private void UpdateBoss(double dt) => ChaseAndAttack(boss, dt);

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
        var distance = Math.Sqrt(dx * dx + dz * dz);

        if (distance <= MonsterAttackRange)
        {
            if (monster.AttackCooldown <= 0)
            {
                monster.AttackCooldown = monster.AttackInterval;
                var monsterDamage = monster.Damage.Total > 0 ? monster.Damage : new DamageBundle(monster.Offense);
                var hit = CombatModel.ResolveBundleAttack(
                    MonsterStats(monster), monsterDamage, PlayerStats(), Resistances,
                    new AttackProfile(1.0, 0.03, 1.5, 0.12),
                    rng);
                var incoming = hit.Damage;
                if (PlayerShield > 0)
                {
                    var absorbed = Math.Min(PlayerShield, incoming);
                    PlayerShield -= absorbed;
                    incoming -= absorbed;
                }
                PlayerHp = Math.Max(0, PlayerHp - incoming);
                if (PlayerHp <= 0)
                {
                    playerRespawnTimer = PlayerRespawnSeconds;
                    hasTarget = false;
                }
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

    /// <summary>Nearest alive monster other than <paramref name="exclude"/> (fork/chain target).</summary>
    private CombatMonster NearestAliveMonsterExcept(CombatMonster exclude, double range)
    {
        CombatMonster best = null;
        var bestDistance = range;
        if (boss is { Alive: true } && boss != exclude)
        {
            var bd = Distance(boss);
            if (bd < bestDistance) { bestDistance = bd; best = boss; }
        }
        foreach (var monster in monsters)
        {
            if (!monster.Alive || monster == exclude) continue;
            var distance = Distance(monster);
            if (distance < bestDistance) { bestDistance = distance; best = monster; }
        }
        return best;
    }

    /// <summary>Recovered on-hit mechanics: Poison_Chance_On_Hit (428),
    /// Double_Damage_Chance_On_Crit_On_Poisoned_Target (429) and, for auto-attacks,
    /// Projectile_Auto_Attacks_Fork_Chance (431) via Calculator.CalculateChance (see
    /// ShootRangedProjectile.HandleForkAndChain). Returns the damage actually dealt.</summary>
    private double ApplyOnHit(CombatMonster target, DamageResult hit, bool autoAttack)
    {
        if (!hit.Hit) return 0;
        var damage = hit.Damage;
        if (hit.Critical && target.PoisonTimer > 0 && CombatModel.RollChance(DoubleDamageOnCritPoisoned, rng))
            damage *= 2;
        if (CombatModel.RollChance(PoisonChance, rng))
        {
            target.PoisonTimer = Math.Max(target.PoisonTimer, PoisonSeconds);
            target.PoisonDps = Math.Max(target.PoisonDps, PoisonDamagePerSecond());
        }
        if (autoAttack && CombatModel.RollChance(ForkChance, rng))
        {
            var forked = NearestAliveMonsterExcept(target, PlayerAttackRange * 2);
            if (forked is not null) DamageMonster(forked, damage);
        }
        if (autoAttack && CombatModel.RollChance(ChainChance, rng))
        {
            var chained = NearestAliveMonsterExcept(target, PlayerAttackRange * 2);
            if (chained is not null) DamageMonster(chained, damage * 0.75);
        }
        return damage;
    }

    private const double PoisonSeconds = 3.0;

    /// <summary>Poison DoT rate: half the main-hand poison damage per second (Interim magnitude;
    /// the chance and double-damage rules are the recovered ones).</summary>
    private double PoisonDamagePerSecond() => Math.Max(1.0, Damage.Poison * 0.5);

    private void TickPoison(double dt)
    {
        foreach (var monster in monsters) TickMonsterPoison(monster, dt);
        if (boss is { Alive: true }) TickMonsterPoison(boss, dt);
    }

    private void TickMonsterPoison(CombatMonster monster, double dt)
    {
        if (monster is null || !monster.Alive || monster.PoisonTimer <= 0) return;
        monster.PoisonTimer -= dt;
        DamageMonster(monster, monster.PoisonDps * dt);
        if (monster.PoisonTimer <= 0) monster.PoisonDps = 0;
    }

    private void DamageMonster(CombatMonster monster, double damage)
    {
        if (!monster.Alive) return;
        monster.Hp -= damage;
        if (monster.Hp > 0) return;
        monster.Alive = false;
        monster.Hp = 0;
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
                Math.Round(monster.X, 3), Math.Round(monster.Z, 3), Math.Round(monster.Hp, 2), Math.Round(monster.MaxHp, 2), monster.Alive, Math.Round(monster.StunTimer, 2)));
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
            DungeonsCleared, Math.Max(0, BossKillGoal - dungeonKills), boss is { Alive: true }, rows);
    }

    private void ClampToArena()
    {
        PlayerX = Math.Clamp(PlayerX, -ArenaHalf, ArenaHalf);
        PlayerZ = Math.Clamp(PlayerZ, -ArenaHalf, ArenaHalf);
    }
}
