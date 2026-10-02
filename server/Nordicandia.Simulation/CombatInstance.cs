namespace Nordicandia.Simulation;

/// <summary>One monster inside an authoritative combat instance.</summary>
public sealed class CombatMonster
{
    public int Index { get; init; }
    public string Name { get; init; } = "Monster";
    public int Level { get; init; }
    public double X { get; set; }
    public double Z { get; set; }
    public double Hp { get; set; }
    public double MaxHp { get; set; }
    public double Offense { get; set; }
    public double Defense { get; set; }
    public double Speed { get; set; } = 2.4;
    public double AttackInterval { get; set; } = 1.6;
    public double AttackCooldown { get; set; }
    public bool Alive { get; set; } = true;
    public double RespawnTimer { get; set; }
    public double WanderTimer { get; set; }
    public double TargetX { get; set; }
    public double TargetZ { get; set; }
}

/// <summary>Read-only projection of an <see cref="CombatInstance"/> handed to callers/tests.</summary>
public readonly record struct MonsterSnapshot(
    int Index, string Name, int Level, double X, double Z, double Hp, double MaxHp, bool Alive);

public readonly record struct CombatSnapshot(
    long Version,
    double PlayerX,
    double PlayerZ,
    double PlayerHp,
    double PlayerMaxHp,
    int PlayerLevel,
    double Experience,
    int Silver,
    int Opals,
    int Kills,
    double SkillCooldown,
    IReadOnlyList<MonsterSnapshot> Monsters);

/// <summary>Result of a skill command.</summary>
public readonly record struct SkillOutcome(bool Cast, double Damage, int TargetIndex, string Reason);

/// <summary>
/// Server-authoritative combat for one character. Deterministic in fixed steps: any
/// sequence of <see cref="Advance"/> calls is split into a fixed timestep, so the same
/// inputs and seed always produce the same outcome regardless of caller frame rate.
///
/// Progression is written back through the host (see the server's CombatRegistry); this
/// class only tracks in-memory combat + the totals that need persisting.
/// </summary>
public sealed class CombatInstance
{
    public const double StepSeconds = 0.05;
    public const double PlayerAttackRange = 3.6;
    public const double PlayerAttackInterval = 0.75;
    public const double SkillCooldownSeconds = 4.0;
    public const double SkillMultiplier = 2.4;
    public const double SkillCritChance = 0.15;
    public const double SkillCritMultiplier = 2.0;
    public const double MonsterAggroRange = 16;
    public const double MonsterAttackRange = 2.1;
    public const double MonsterRespawnSeconds = 6.0;
    public const double PlayerRespawnSeconds = 3.0;
    public const double ArenaHalf = 20.0;

    private readonly CombatRandom rng;
    private readonly double playerSpeed = 6.5;
    private readonly int monsterCount;

    private double targetX;
    private double targetZ;
    private bool hasTarget;
    private double attackCooldown;
    private double skillCooldown;
    private double playerRespawnTimer;
    private double simulationTime;

    public double PlayerX { get; private set; }
    public double PlayerZ { get; private set; }
    public double PlayerHp { get; private set; }
    public double PlayerMaxHp { get; }
    public double Offense { get; private set; }
    public double Defense { get; }
    public double Recovery { get; }
    public int PlayerLevel { get; private set; }
    public double Experience { get; private set; }
    public int Silver { get; private set; }
    public int Opals { get; private set; }
    public int Kills { get; private set; }
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
        int monsterCount = 5)
    {
        PlayerLevel = stats.Level;
        Offense = stats.Offense;
        Defense = stats.Defense;
        Recovery = stats.Recovery;
        PlayerMaxHp = CombatModel.MaxHealth(stats);
        PlayerHp = PlayerMaxHp;
        Experience = experience;
        Silver = silver;
        Opals = opals;
        Kills = kills;
        this.monsterCount = Math.Clamp(monsterCount, 1, 24);
        rng = new CombatRandom(seed == 0 ? 0x9E3779B97F4A7C15UL : seed);
        SpawnMonsters();
        Version = 1;
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
        var maxHp = 40 + level * 22;
        return new CombatMonster
        {
            Index = index,
            Name = "Draugr",
            Level = level,
            X = x,
            Z = z,
            Hp = alive ? maxHp : 0,
            MaxHp = maxHp,
            Offense = 4 + level * 1.5,
            Defense = 2 + level * 1.5,
            Alive = alive,
            AttackCooldown = rng.NextDouble() * 1.4,
            WanderTimer = rng.NextDouble() * 2,
        };
    }

    /// <summary>Advance by arbitrary wall-clock time, split into fixed steps for determinism.</summary>
    public void Advance(double deltaSeconds)
    {
        if (deltaSeconds <= 0 || double.IsNaN(deltaSeconds)) return;
        // Cap a single advance so a long idle pause cannot fast-forward thousands of hits.
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
        simulationTime += dt;
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
        Version++;
    }

    private void UpdatePlayer(double dt)
    {
        attackCooldown -= dt;
        skillCooldown = Math.Max(0, skillCooldown - dt);
        if (Recovery > 0 && PlayerHp > 0)
            PlayerHp = Math.Min(PlayerMaxHp, PlayerHp + Recovery * dt);

        if (hasTarget)
        {
            var dx = targetX - PlayerX;
            var dz = targetZ - PlayerZ;
            var distance = Math.Sqrt(dx * dx + dz * dz);
            if (distance < 0.15)
            {
                hasTarget = false;
            }
            else
            {
                var step = Math.Min(distance, playerSpeed * dt);
                PlayerX += dx / distance * step;
                PlayerZ += dz / distance * step;
                ClampToArena();
            }
        }

        var target = NearestAliveMonster(PlayerAttackRange);
        if (target is not null && attackCooldown <= 0)
        {
            attackCooldown = PlayerAttackInterval;
            var hit = CombatModel.ResolveHit(
                new CombatantStats(Offense, Defense, Recovery, PlayerLevel),
                new CombatantStats(target.Offense, target.Defense, 0, target.Level),
                new AttackProfile(1.0, 0.08, 1.6, 0.12),
                rng);
            DamageMonster(target, hit.Damage);
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
                }
                continue;
            }

            monster.AttackCooldown -= dt;
            var dx = PlayerX - monster.X;
            var dz = PlayerZ - monster.Z;
            var distance = Math.Sqrt(dx * dx + dz * dz);

            if (PlayerHp > 0 && distance <= MonsterAttackRange)
            {
                if (monster.AttackCooldown <= 0)
                {
                    monster.AttackCooldown = monster.AttackInterval;
                    var hit = CombatModel.ResolveHit(
                        new CombatantStats(monster.Offense, monster.Defense, 0, monster.Level),
                        new CombatantStats(Offense, Defense, Recovery, PlayerLevel),
                        new AttackProfile(1.0, 0.03, 1.5, 0.12),
                        rng);
                    PlayerHp = Math.Max(0, PlayerHp - hit.Damage);
                    if (PlayerHp <= 0)
                    {
                        playerRespawnTimer = PlayerRespawnSeconds;
                        hasTarget = false;
                        break;
                    }
                }
                continue;
            }

            // Chase the player when aggroed, otherwise wander.
            double goalX, goalZ;
            if (PlayerHp > 0 && distance <= MonsterAggroRange)
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
    }

    private CombatMonster NearestAliveMonster(double range)
    {
        CombatMonster best = null;
        var bestDistance = range;
        foreach (var monster in monsters)
        {
            if (!monster.Alive) continue;
            var dx = monster.X - PlayerX;
            var dz = monster.Z - PlayerZ;
            var distance = Math.Sqrt(dx * dx + dz * dz);
            if (distance < bestDistance)
            {
                bestDistance = distance;
                best = monster;
            }
        }
        return best;
    }

    private void DamageMonster(CombatMonster monster, double damage)
    {
        monster.Hp -= damage;
        if (monster.Hp > 0) return;
        monster.Alive = false;
        monster.Hp = 0;
        monster.RespawnTimer = MonsterRespawnSeconds;
        Kills++;
        var gained = CombatModel.ExperienceReward(monster.Level);
        Experience += gained;
        var newLevel = Progression.LevelForExperience(Experience);
        if (newLevel > PlayerLevel)
        {
            var gainedLevels = newLevel - PlayerLevel;
            PlayerLevel = newLevel;
            Offense += 1.5 * gainedLevels;
            PlayerHp = PlayerMaxHp;
        }
    }

    // ------------------------------------------------------------------ commands

    /// <summary>Move the player toward a point. Clamped to the arena.</summary>
    public void MoveTo(double x, double z)
    {
        targetX = Math.Clamp(x, -ArenaHalf, ArenaHalf);
        targetZ = Math.Clamp(z, -ArenaHalf, ArenaHalf);
        hasTarget = true;
        Version++;
    }

    /// <summary>Instant skill on the nearest monster in range.</summary>
    public SkillOutcome UseSkill()
    {
        if (PlayerHp <= 0) return new SkillOutcome(false, 0, -1, "dead");
        if (skillCooldown > 0) return new SkillOutcome(false, 0, -1, "cooldown");
        var target = NearestAliveMonster(8);
        if (target is null) return new SkillOutcome(false, 0, -1, "no_target");
        skillCooldown = SkillCooldownSeconds;
        var hit = CombatModel.ResolveHit(
            new CombatantStats(Offense, Defense, Recovery, PlayerLevel),
            new CombatantStats(target.Offense, target.Defense, 0, target.Level),
            new AttackProfile(SkillMultiplier, SkillCritChance, SkillCritMultiplier, 0.08),
            rng);
        DamageMonster(target, hit.Damage);
        Version++;
        return new SkillOutcome(true, hit.Damage, target.Index, "ok");
    }

    public CombatSnapshot Snapshot()
    {
        var rows = new List<MonsterSnapshot>(monsters.Count);
        foreach (var monster in monsters)
        {
            rows.Add(new MonsterSnapshot(monster.Index, monster.Name, monster.Level,
                monster.X, monster.Z, Math.Round(monster.Hp, 2), Math.Round(monster.MaxHp, 2), monster.Alive));
        }
        return new CombatSnapshot(Version, Math.Round(PlayerX, 3), Math.Round(PlayerZ, 3),
            Math.Round(PlayerHp, 2), Math.Round(PlayerMaxHp, 2), PlayerLevel, Experience, Silver, Opals,
            Kills, Math.Round(skillCooldown, 2), rows);
    }

    private void ClampToArena()
    {
        PlayerX = Math.Clamp(PlayerX, -ArenaHalf, ArenaHalf);
        PlayerZ = Math.Clamp(PlayerZ, -ArenaHalf, ArenaHalf);
    }
}
