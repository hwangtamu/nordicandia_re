namespace Nordicandia.Simulation;

/// <summary>
/// Level/experience curve recovered from the shipped client's <c>Game.Calculator</c>
/// (constants <c>ExpNeededToLevel_Add=350</c>, <c>ExpNeededToLevel_Mult=20</c>,
/// <c>ExpNeededToLevel_Power=1.7</c>). Experience is cumulative: level L is reached
/// once total experience is at least <c>350 + 20 * L^1.7</c>. The client inverts it
/// in closed form with <c>floor(round(((exp - 350) / 20)^(1 / 1.7) - level, 6))</c>.
/// </summary>
public static class Progression
{
    public const double Add = 350.0;
    public const double Mult = 20.0;
    public const double Power = 1.7;

    public static double ExperienceForLevel(int level)
        => level <= 1 ? 0.0 : Add + Mult * Math.Pow(level, Power);

    public static int LevelForExperience(double experience)
    {
        if (double.IsNaN(experience) || double.IsInfinity(experience) || experience <= Add) return 1;
        var level = Math.Floor(Math.Round(Math.Pow((experience - Add) / Mult, 1.0 / Power), 6));
        return level < 1 ? 1 : level >= int.MaxValue ? int.MaxValue : (int)level;
    }
}

/// <summary>Confidence of a rule, so unrecovered numbers are never presented as verified.</summary>
public enum RuleConfidence
{
    /// <summary>Observed verbatim in the shipped client and reproduced here.</summary>
    ClientVerified,
    /// <summary>Deduced from shipped data or a documented relationship; plausible but uncalibrated.</summary>
    Inferred,
    /// <summary>Placeholder chosen to make the loop playable until client samples are captured.</summary>
    Provisional,
}

/// <summary>
/// Minimum combat stats needed by the M0 slice. Values map directly onto the client's
/// <c>SerializedCombatStats.Offense/Defense/Recovery</c> plus level.
/// </summary>
public readonly record struct CombatantStats(double Offense, double Defense, double Recovery, int Level)
{
    public static CombatantStats FromRealtime(double offense, double defense, double recovery, int level)
        => new(Math.Max(0, offense), Math.Max(0, defense), Math.Max(0, recovery), Math.Max(1, level));
}

/// <summary>One attack's tunable inputs (weapon/skill multiplier, crit).</summary>
public readonly record struct AttackProfile(
    double SkillMultiplier = 1.0,
    double CritChance = 0.05,
    double CritMultiplier = 1.5,
    double Variance = 0.10);

/// <summary>Result of one resolved hit.</summary>
public readonly record struct DamageResult(double Damage, bool Critical, RuleConfidence Confidence);

/// <summary>
/// M0 combat sample. The real client's damage pipeline has not been recovered yet, so
/// every formula here is tagged with its <see cref="RuleConfidence"/>. The point of M0
/// is to freeze a reproducible sample with fixed seeds; the numbers will be recalibrated
/// against captured client observations before they are treated as authoritative.
/// </summary>
public static class CombatModel
{
    /// <summary>Provisional health curve: 100 at level 1, growing 40/level.</summary>
    public static double MaxHealth(CombatantStats stats, RuleConfidence confidence = RuleConfidence.Provisional)
        => 150.0 + 50.0 * Math.Max(1, stats.Level);

    /// <summary>
    /// Provisional mitigation: armor scales against an attacker-level baseline so low
    /// level attackers are not fully walled by end-game defense.
    /// </summary>
    public static double Mitigation(double defense, int attackerLevel)
    {
        var scale = 50.0 + 10.0 * Math.Max(1, attackerLevel);
        var d = Math.Max(0, defense);
        return d / (d + scale);
    }

    /// <summary>Expected damage before variance, useful for balancing and tests.</summary>
    public static double ExpectedDamage(CombatantStats attacker, CombatantStats defender, AttackProfile profile)
    {
        var raw = Math.Max(0, attacker.Offense) * Math.Max(0, profile.SkillMultiplier);
        var mitigated = raw * (1.0 - Mitigation(defender.Defense, attacker.Level));
        var critFactor = 1.0 + profile.CritChance * (Math.Max(1.0, profile.CritMultiplier) - 1.0);
        return Math.Max(1.0, mitigated) * critFactor;
    }

    /// <summary>
    /// Resolves one hit deterministically for the supplied RNG. Damage never drops below
    /// 1 so an under-geared attacker still makes progress.
    /// </summary>
    public static DamageResult ResolveHit(
        CombatantStats attacker,
        CombatantStats defender,
        AttackProfile profile,
        CombatRandom rng)
    {
        var raw = Math.Max(0, attacker.Offense) * Math.Max(0, profile.SkillMultiplier);
        var mitigated = raw * (1.0 - Mitigation(defender.Defense, attacker.Level));
        var variance = 1.0 + (rng.NextDouble() * 2.0 - 1.0) * Math.Max(0, profile.Variance);
        var critical = rng.NextDouble() < Math.Clamp(profile.CritChance, 0.0, 1.0);
        var critFactor = critical ? Math.Max(1.0, profile.CritMultiplier) : 1.0;
        var damage = Math.Max(1.0, mitigated * Math.Max(0.05, variance) * critFactor);
        return new DamageResult(damage, critical, RuleConfidence.Provisional);
    }

    /// <summary>Experience granted for a kill at <paramref name="monsterLevel"/>.
    /// Provisional until the client's <c>Monster.CalculateAttributes</c> XP path is sampled.</summary>
    public static double ExperienceReward(int monsterLevel, double multiplier = 1.0)
    {
        var l = Math.Max(1, monsterLevel);
        var baseXp = 8.0 * Math.Pow(l, 1.35) + 5.0;
        return baseXp * Math.Max(0, multiplier);
    }
}
