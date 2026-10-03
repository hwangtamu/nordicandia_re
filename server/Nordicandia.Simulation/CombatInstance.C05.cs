namespace Nordicandia.Simulation;

/// <summary>
/// C05: PoisonCloud (Mage) and player minions (SummonSkeleton, Necromancer).
///
/// PoisonCloud: the client runs a persistent 6s cloud (radius 3.0) that ticks once per
/// second for 0.9x weapon damage as poison and applies a 20% slow
/// (Power_Slow_Effect_Percent). Previously the web folded it into the instant nova
/// prototype (C04 gaps: Power_Duration, Power_Slow_Effect_Percent).
///
/// C06: generalized to GroundEffect — Blizzard and ElementalSeal are the same
/// persistent-AoE family with different radius/duration/damage (no slow).
///
/// PlayerMinion: friendly skeleton entities. The old summon prototype only applied an
/// offense buff; C05 spawns real minions that seek and attack monsters. Minion stats
/// use the recovered Minion_Inheritance_* attributes (Provisional formulas).
/// Monsters do not target minions yet (documented simplification).
/// </summary>
public sealed class PoisonCloud
{
    public double X, Z;
    public double Radius = 3.0;
    public double Remaining = 6.0;
    public double TickInterval = 1.0;
    public double TickTimer;
    public double DamageMult = 0.9;
    public double SlowAmount = 0.2;
    public double SlowDuration = 2.0;
    public SkillProfile Skill;
    /// <summary>C06: "PoisonCloud", "Blizzard", "ElementalSeal" — same tick logic.</summary>
    public string Kind = "PoisonCloud";
}

/// <summary>Friendly player minion (skeleton). Attacks monsters in melee.</summary>
public sealed class PlayerMinion
{
    public string Name = "Skeleton";
    public double X, Z;
    public double Hp, MaxHp;
    public double Damage;
    public double AttackInterval = 2.0;
    public double AttackCooldown;
    public double AttackRange = 2.0;
    public double Speed = 6.0;
    public bool Alive = true;
}

public partial class CombatInstance
{
    private readonly List<PoisonCloud> clouds = new();
    private readonly List<PlayerMinion> minions = new();

    /// <summary>Active poison clouds (for tests/rendering).</summary>
    public IReadOnlyList<PoisonCloud> Clouds => clouds;
    /// <summary>Active player minions (for tests/rendering).</summary>
    public IReadOnlyList<PlayerMinion> PlayerMinions => minions;

    private void TickClouds(double dt)
    {
        foreach (var cloud in clouds.ToList())
        {
            cloud.Remaining -= dt;
            if (cloud.Remaining <= 1e-9)
            {
                clouds.Remove(cloud);
                EmitEvent("cloud", -1, 0, $"expire {cloud.Kind}");
                continue;
            }
            cloud.TickTimer -= dt;
            if (cloud.TickTimer > 0) continue;
            cloud.TickTimer += cloud.TickInterval;
            foreach (var m in AllCombatMonsters())
            {
                if (!m.Alive) continue;
                var d = Math.Sqrt(Math.Pow(m.X - cloud.X, 2) + Math.Pow(m.Z - cloud.Z, 2));
                if (d > cloud.Radius) continue;
                // Tick damage (element from the skill's PowerTags).
                var dealt = HitTarget(m, cloud.Skill, cloud.DamageMult);
                // PoisonCloud's 20% slow; Blizzard/ElementalSeal have no slow.
                if (cloud.SlowAmount > 0)
                    m.Buffs.Add(new BuffInstance
                    {
                        DefinitionId = "slow",
                        Source = cloud.Kind,
                        Duration = cloud.SlowDuration,
                        Remaining = cloud.SlowDuration,
                        Magnitude = cloud.SlowAmount,
                    });
                // C06 batch 3: Decay reduces attack speed by 20%.
                if (cloud.Kind == "Decay")
                    m.Buffs.Add(new BuffInstance
                    {
                        DefinitionId = "decay-slow",
                        Source = "Decay",
                        Duration = 2.0,
                        Remaining = 2.0,
                        Magnitude = 0.2,
                    });
                EmitEvent("cloud", m.Index, dealt, $"tick {cloud.Kind}");
            }
        }
    }

    private void TickMinions(double dt)
    {
        foreach (var minion in minions)
        {
            if (!minion.Alive) continue;
            minion.AttackCooldown -= dt;
            var target = NearestMonsterTo(minion.X, minion.Z, 30);
            if (target is null) continue;
            var dx = target.X - minion.X;
            var dz = target.Z - minion.Z;
            var d = Math.Sqrt(dx * dx + dz * dz);
            if (d > minion.AttackRange)
            {
                var step = Math.Min(d - minion.AttackRange, minion.Speed * dt);
                if (d > 1e-9)
                {
                    minion.X = Math.Clamp(minion.X + dx / d * step, -ArenaHalf, ArenaHalf);
                    minion.Z = Math.Clamp(minion.Z + dz / d * step, -ArenaHalf, ArenaHalf);
                }
            }
            else if (minion.AttackCooldown <= 0)
            {
                minion.AttackCooldown = minion.AttackInterval;
                DamageMonster(target, minion.Damage);
                EmitEvent("minion", target.Index, minion.Damage, $"{minion.Name} attack");
            }
        }
        minions.RemoveAll(m => !m.Alive);
    }

    private IEnumerable<CombatMonster> AllCombatMonsters()
    {
        foreach (var m in monsters) yield return m;
        if (boss is { Alive: true }) yield return boss;
    }

    private CombatMonster NearestMonsterTo(double x, double z, double range)
    {
        CombatMonster best = null;
        var bestDist = range;
        foreach (var m in AllCombatMonsters())
        {
            if (!m.Alive) continue;
            var d = Math.Sqrt(Math.Pow(m.X - x, 2) + Math.Pow(m.Z - z, 2));
            if (d < bestDist) { bestDist = d; best = m; }
        }
        return best;
    }

    /// <summary>Monster movement speed with slow debuffs applied.</summary>
    private double EffectiveMonsterSpeed(CombatMonster monster)
        => monster.Speed * (1 - monster.Buffs.MagnitudeOf("slow", "PoisonCloud"));

    /// <summary>
    /// Spawns a persistent ground effect at (x, z) for <paramref name="skill"/>.
    /// Duration/radius/damage/slow come from the skill's recovered attributes.
    /// C06: generalized — PoisonCloud, Blizzard, ElementalSeal share this.
    /// </summary>
    private void SpawnGroundEffect(SkillProfile skill, double x, double z, string kind)
    {
        var cloud = new PoisonCloud
        {
            X = x,
            Z = z,
            Kind = kind,
            Radius = skill.Radius > 0 ? skill.Radius : 3.0,
            Remaining = skill.Values.TryGetValue("Power_Duration", out var dur) ? dur : 6.0,
            DamageMult = skill.Multiplier,
            SlowAmount = skill.Values.TryGetValue("Power_Slow_Effect_Percent", out var slow) ? slow : 0,
            Skill = skill,
        };
        cloud.TickTimer = cloud.TickInterval;
        clouds.Add(cloud);
        EmitEvent("cloud", -1, 0, $"spawn {kind} {x:F1},{z:F1}");
    }

    private void SpawnPoisonCloud(SkillProfile skill, double x, double z)
        => SpawnGroundEffect(skill, x, z, "PoisonCloud");

    /// <summary>
    /// Spawns player skeleton minions for <paramref name="skill"/>. Stats use the
    /// recovered Minion_Inheritance_* attributes (Provisional formulas):
    /// damage = weapon total * (1 + weapon damage bonus), life = max HP * (1 + life bonus),
    /// attack interval = 2s / (1 + attack speed bonus).
    /// </summary>
    private void SpawnSkeletons(SkillProfile skill)
    {
        var v = skill.Values;
        var dmgBonus = v.TryGetValue("Minion_Inheritance_Weapon_Damage_Bonus_Percent", out var db) ? db : 0.15;
        var lifeBonus = v.TryGetValue("Minion_Inheritance_Life_Bonus_Percent", out var lb) ? lb : 0.5;
        var asBonus = v.TryGetValue("Minion_Inheritance_Attack_Speed_Bonus_Percent", out var ab) ? ab : 0.8;
        var weaponTotal = PlayerDamageBundle().Total;
        const int count = 3; // Provisional: no count attribute recovered
        for (var i = 0; i < count; i++)
        {
            var angle = rng.NextDouble() * Math.PI * 2;
            var minion = new PlayerMinion
            {
                X = Math.Clamp(PlayerX + Math.Cos(angle) * 2, -ArenaHalf, ArenaHalf),
                Z = Math.Clamp(PlayerZ + Math.Sin(angle) * 2, -ArenaHalf, ArenaHalf),
                Damage = Math.Max(1, weaponTotal * (1 + dmgBonus)),
                AttackInterval = 2.0 / (1 + asBonus),
            };
            minion.MaxHp = minion.Hp = Math.Max(1, PlayerMaxHp * (1 + lifeBonus));
            minions.Add(minion);
        }
        EmitEvent("summon", -1, count, skill.Name);
    }
}
