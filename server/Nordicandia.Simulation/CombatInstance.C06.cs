namespace Nordicandia.Simulation;

/// <summary>
/// C06: second batch of skill mechanism families.
///
/// - Trap (ImpalingTrap, Hunter): persistent trap entity, triggers on proximity.
/// - Teleport (Mage): fixed misclassification (was rally); blink displacement.
/// - Channel (Whirlwind, Warrior): 5s channeled AoE, ticks at recovered frequency.
/// - Mark (MarkOfTheChosen, Hunter): damage-taken amplification debuff on a monster.
/// - Thorns (Hunter): reflect melee damage + physical damage reduction buff.
///
/// Like C05, per-skill dispatch is by name until the classifier gains these
/// families (C06 follow-up).
/// </summary>
public sealed class Trap
{
    public double X, Z;
    public double Radius = 2.0;
    public double Remaining = 5.0;
    public double DamageMult = 7.5;
    public bool Triggered;
    public SkillProfile Skill;
}

public sealed class Channel
{
    public double Remaining = 5.0;
    public double TickInterval = 0.2;
    public double TickTimer;
    public double Radius = 2.0;
    public double DamageMult = 0.4;
    public SkillProfile Skill;
}

public partial class CombatInstance
{
    private readonly List<Trap> traps = new();
    private Channel activeChannel;

    /// <summary>Active traps (for tests/rendering).</summary>
    public IReadOnlyList<Trap> Traps => traps;
    /// <summary>Active channeled skill, if any (for tests/rendering).</summary>
    public Channel ActiveChannel => activeChannel;

    private void TickTraps(double dt)
    {
        foreach (var trap in traps.ToList())
        {
            trap.Remaining -= dt;
            if (trap.Remaining <= 1e-9 || trap.Triggered)
            {
                traps.Remove(trap);
                EmitEvent("trap", -1, 0, trap.Triggered ? "triggered ImpalingTrap" : "expire ImpalingTrap");
                continue;
            }
            foreach (var m in AllCombatMonsters())
            {
                if (!m.Alive) continue;
                var d = Math.Sqrt(Math.Pow(m.X - trap.X, 2) + Math.Pow(m.Z - trap.Z, 2));
                if (d > trap.Radius) continue;
                trap.Triggered = true;
                var dealt = HitTarget(m, trap.Skill, trap.DamageMult);
                EmitEvent("trap", m.Index, dealt, "trigger ImpalingTrap");
                break;
            }
        }
    }

    private void TickChannel(double dt)
    {
        var ch = activeChannel;
        if (ch is null) return;
        ch.Remaining -= dt;
        if (ch.Remaining <= 1e-9)
        {
            activeChannel = null;
            drainTarget = null;
            EmitEvent("channel", -1, 0, $"end {ch.Skill.Name}");
            return;
        }
        ch.TickTimer -= dt;
        if (ch.TickTimer > 0) return;
        ch.TickTimer += ch.TickInterval;
        // C06 batch 3: DrainLife is targeted, not radial.
        if (ch.Skill.Name == "DrainLife" && drainTarget is { Alive: true })
        {
            var dealt = HitTarget(drainTarget, ch.Skill, ch.DamageMult);
            // Leech: 8% + 4% per active minion.
            var leechPct = ch.Skill.Values.TryGetValue("Power_Drain_Life_Percent_Of_Life_Leech_Leeched_As_Life", out var lp) ? lp : 8.0;
            var perMinion = ch.Skill.Values.TryGetValue("Power_Drain_Life_Life_Leech_Bonus_Per_Active_Minion", out var pm) ? pm : 4.0;
            var totalLeech = (leechPct + perMinion * minions.Count(m => m.Alive)) / 100.0;
            var healed = dealt * totalLeech;
            PlayerHp = Math.Min(PlayerMaxHp, PlayerHp + healed);
            EmitEvent("channel", drainTarget.Index, dealt, "tick DrainLife");
            if (healed > 0) EmitEvent("heal", -1, healed, "drainlife leech");
            return;
        }
        double total = 0;
        foreach (var m in AllCombatMonsters())
        {
            if (!m.Alive) continue;
            var d = Math.Sqrt(Math.Pow(m.X - PlayerX, 2) + Math.Pow(m.Z - PlayerZ, 2));
            if (d > ch.Radius) continue;
            total += HitTarget(m, ch.Skill, ch.DamageMult);
        }
        if (total > 0) EmitEvent("channel", -1, total, "tick Whirlwind");
    }

    /// <summary>Places an ImpalingTrap at (x, z). Triggers once on proximity.</summary>
    private void PlaceTrap(SkillProfile skill, double x, double z)
    {
        traps.Add(new Trap
        {
            X = x,
            Z = z,
            Radius = 2.0,
            Remaining = skill.Values.TryGetValue("Power_Duration", out var dur) ? dur : 5.0,
            DamageMult = skill.Multiplier,
            Skill = skill,
        });
        EmitEvent("trap", -1, 0, $"place ImpalingTrap {x:F1},{z:F1}");
    }

    /// <summary>Starts a Whirlwind channel: 5s, ticks every 0.2s, 0.4x in 2y.</summary>
    private void StartWhirlwind(SkillProfile skill)
    {
        activeChannel = new Channel
        {
            Remaining = skill.Values.TryGetValue("Power_Duration", out var dur) ? dur : 5.0,
            TickInterval = skill.Values.TryGetValue("Power_Whirlwind_Damage_Frequency", out var freq) ? freq : 0.2,
            Radius = skill.Radius > 0 ? skill.Radius : 2.0,
            DamageMult = skill.Multiplier,
            Skill = skill,
        };
        activeChannel.TickTimer = activeChannel.TickInterval;
        EmitEvent("channel", -1, 0, "start Whirlwind");
    }

    /// <summary>Blink: teleports the player 10y away from the nearest threat.</summary>
    private void Blink(SkillProfile skill)
    {
        var threat = NearestAliveMonster(30);
        double nx = PlayerX, nz = PlayerZ;
        if (threat is not null)
        {
            var dx = PlayerX - threat.X;
            var dz = PlayerZ - threat.Z;
            var d = Math.Max(1e-6, Math.Sqrt(dx * dx + dz * dz));
            nx = PlayerX + dx / d * 10;
            nz = PlayerZ + dz / d * 10;
        }
        else
        {
            nx = PlayerX + 10;
        }
        PlayerX = Math.Clamp(nx, -ArenaHalf, ArenaHalf);
        PlayerZ = Math.Clamp(nz, -ArenaHalf, ArenaHalf);
        EmitEvent("mobility", -1, 0, $"blink Teleport {PlayerX:F1},{PlayerZ:F1}");
    }

    /// <summary>Applies Mark of the Chosen: target takes amplified damage.</summary>
    private void ApplyMark(CombatMonster target, SkillProfile skill)
    {
        var amplify = skill.Values.TryGetValue("Power_Mark_Of_The_Chosen_Amplify_Damage_Taken_Percent", out var amp) ? amp : 0.1;
        var duration = skill.Values.TryGetValue("Power_Duration", out var dur) ? dur : 15.0;
        target.Buffs.Add(new BuffInstance
        {
            DefinitionId = "mark",
            Source = "MarkOfTheChosen",
            Duration = duration,
            Remaining = duration,
            Magnitude = amplify,
        });
        EmitEvent("debuff", target.Index, amplify, "mark MarkOfTheChosen");
    }

    /// <summary>Applies Thorns: reflects melee damage + physical damage reduction.</summary>
    private void ApplyThorns(SkillProfile skill)
    {
        var reflect = skill.Values.TryGetValue("Reflect_Melee_Damage_Percent", out var r) ? r : 2.0;
        var duration = skill.Values.TryGetValue("Buff_Duration", out var dur) ? dur : 10.0;
        PlayerBuffs.Add(new BuffInstance
        {
            DefinitionId = "thorns",
            Source = "Thorns",
            Duration = duration,
            Remaining = duration,
            Magnitude = reflect,
        });
        var reduction = skill.Values.TryGetValue("Base_Physical_Damage_Reduction_Bonus", out var red) ? red : 0.1;
        PlayerBuffs.Add(new BuffInstance
        {
            DefinitionId = "thorns-reduction",
            Source = "Thorns",
            Duration = duration,
            Remaining = duration,
            Magnitude = reduction,
        });
        EmitEvent("buff", -1, 0, "thorns Thorns");
    }

    /// <summary>
    /// C06: line attack for Slam — damages monsters within <paramref name="halfWidth"/>
    /// of the segment from the player toward <paramref name="target"/>, up to
    /// <paramref name="maxDist"/> yards.
    /// </summary>
    private double HitLine(SkillProfile skill, CombatMonster target, double maxDist, double halfWidth)
    {
        var dx = target.X - PlayerX;
        var dz = target.Z - PlayerZ;
        var len = Math.Max(1e-6, Math.Sqrt(dx * dx + dz * dz));
        dx /= len; dz /= len;
        double total = 0;
        foreach (var m in AllCombatMonsters())
        {
            if (!m.Alive) continue;
            var rx = m.X - PlayerX;
            var rz = m.Z - PlayerZ;
            var proj = rx * dx + rz * dz; // distance along the line
            if (proj < 0 || proj > maxDist) continue;
            var perp = Math.Abs(rx * dz - rz * dx); // perpendicular distance
            if (perp > halfWidth) continue;
            total += HitTarget(m, skill, skill.Multiplier);
        }
        EmitEvent("nova", target.Index, total, $"line Slam {maxDist:F0}y");
        return total;
    }

    /// <summary>
    /// C06: Tornado — a moving vortex: 2y radius collider, 100% pierce, 2.3x per hit.
    /// Flies the standard 14y range with a fat collider (C03 flight). The client's 7s
    /// wandering lifetime is not simulated; the web resolves the flight synchronously.
    /// </summary>
    private double HitTornado(SkillProfile skill, CombatMonster target)
    {
        var projectile = new Projectile
        {
            X = PlayerX,
            Z = PlayerZ,
            Source = skill.Name,
            Radius = skill.Radius > 0 ? skill.Radius : 2.0,
            DamageMult = 1.0,
            CanFork = false,
            CanChain = false,
        };
        return FireProjectiles(projectile, target.X, target.Z,
            (m, mult) => HitTarget(m, skill, skill.Multiplier * mult),
            () => 0, () => 0, () => 1.0);
    }
}
