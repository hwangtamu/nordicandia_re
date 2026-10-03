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
public readonly record struct CombatantStats(double Offense, double Defense, double Recovery, int Level,
    double AttackRating = 0, double Armor = 0, double Evasion = 0, double CritChance = 0,
    double LifeMax = 0, double ManaMax = 0, DamageBundle Damage = default, ResistanceBundle Resistances = default,
    double ForkChance = 0, double ChainChance = 0, double PoisonChance = 0, double DoubleDamageOnCritPoisoned = 0,
    double MoveSpeedMultiplier = 1, bool ProjectileAutoAttack = false, bool PoisonOnHit = false)
{
    public static CombatantStats FromRealtime(double offense, double defense, double recovery, int level)
        => new(Math.Max(0, offense), Math.Max(0, defense), Math.Max(0, recovery), Math.Max(1, level));

    // Recovered ratings take precedence; fall back to the provisional Offense/Defense while a
    // character has no synthesised attribute map yet.
    public double EffectiveAttackRating => AttackRating > 0 ? AttackRating : Offense;
    public double EffectiveArmor => Armor > 0 ? Armor : Defense;
    public double EffectiveEvasion => Evasion > 0 ? Evasion : Defense;
    public double EffectiveLifeMax => LifeMax > 0 ? LifeMax : 150.0 + 50.0 * Math.Max(1, Level);
    public double EffectiveOffense => Damage.Total > 0 ? Damage.Total : Offense;
}

/// <summary>One attack's tunable inputs (weapon/skill multiplier, crit).</summary>
public readonly record struct AttackProfile(
    double SkillMultiplier = 1.0,
    double CritChance = 0.05,
    double CritMultiplier = 1.5,
    double Variance = 0.10);

/// <summary>
/// Confidence of one resolved hit, deliberately split so a verified formula is never
/// presented as if the whole pipeline (input sources, execution order, tuning constants)
/// were verified too. <c>Formula</c> covers the recovered equations, <c>Inputs</c> the
/// attribute sources feeding them, and <c>Execution</c> the composition/ordering.
/// The <c>Assumed*</c> flags name the tuning constants still standing in for client data.
/// </summary>
public readonly record struct DamageConfidence(
    RuleConfidence Formula,
    RuleConfidence Inputs,
    RuleConfidence Execution,
    bool AssumedReductionCap,
    bool AssumedVariance,
    bool AssumedMinDamage)
{
    /// <summary>Weakest link of the three layers (the enum is ordered best-first, so the
    /// weakest link is the largest numeric value).</summary>
    public RuleConfidence Overall => (RuleConfidence)Math.Max((int)Formula, Math.Max((int)Inputs, (int)Execution));
}

/// <summary>Result of one resolved hit.</summary>
public readonly record struct DamageResult(double Damage, bool Critical, DamageConfidence Confidence, bool Hit = true);

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
        => stats.EffectiveLifeMax;

    /// <summary>Constant from <c>Game.Calculator.CalculatePhysicalDamageReduction</c>
    /// (the client computes <c>armor / (armor + 50 * damage)</c>).</summary>
    public const double ReductionConstant = 50.0;

    /// <summary>Assumed cap for <c>Physical_Damage_Reduction_Max</c> when the attribute is unset.
    /// (The client attribute defaults to 0 and is set through difficulty/area data.)</summary>
    public const double DefaultReductionCap = 0.9;

    /// <summary>
    /// ClientVerified port of <c>Game.Calculator.CalculatePhysicalDamageReduction(armor, damage,
    /// damageReductionBonus, cap)</c>: <c>min(armor / (armor + 50*damage) + bonus, cap)</c>.
    /// The client's second argument is the incoming weapon damage, so armour mitigates
    /// proportionally less against large hits.
    /// </summary>
    public static double PhysicalDamageReduction(double armor, double damage, double bonus = 0.0,
        double cap = DefaultReductionCap)
    {
        var a = Math.Max(0.0, armor);
        var d = Math.Max(1e-6, damage);
        return Math.Min(a / (a + ReductionConstant * d) + bonus, cap);
    }

    /// <summary>Backwards-compatible alias used by older callers; now delegates to the
    /// recovered formula with <paramref name="defense"/> as armour and a reference damage.</summary>
    public static double Mitigation(double defense, int attackerLevel)
        => PhysicalDamageReduction(defense, Math.Max(1, attackerLevel));

    /// <summary>ClientVerified port of <c>Game.Calculator.ApplyDamageReduction(damageReduction, rawDamage)</c>
    /// (0x02BAF34C): <c>Max(0, rawDamage * (1 - damageReduction))</c>. Used for the elemental
    /// resistance application.</summary>
    public static double ApplyDamageReduction(double damageReduction, double rawDamage)
    {
        // Faithful to the client: only the *result* is floored at 0, so a negative reduction
        // (an elemental weakness) increases the damage.
        var raw = Math.Max(0.0, rawDamage);
        return Math.Max(0.0, raw * (1.0 - damageReduction));
    }

    /// <summary>Elemental resistance is capped at 1.0 before application
    /// (<c>CalculateEffectiveElementalDamagePerSecond</c> uses <c>Math.Min(1, resistance)</c>).</summary>
    public const double ResistanceCap = 1.0;

    /// <summary>ClientVerified elemental damage after resistance: cap the resistance at 1.0, then
    /// <see cref="ApplyDamageReduction"/>.</summary>
    public static double EffectiveElementalDamage(double rawDamage, double resistance)
        => ApplyDamageReduction(Math.Min(ResistanceCap, resistance), rawDamage);

    /// <summary>ClientVerified full damage pipeline: physical damage goes through armour
    /// (<see cref="PhysicalDamageReduction"/>), each element through its capped resistance
    /// (<see cref="EffectiveElementalDamage"/>), then the parts are summed.</summary>
    public static double MitigateDamage(DamageBundle damage, double armor, ResistanceBundle resistances)
    {
        var physicalReduction = PhysicalDamageReduction(armor, damage.Physical);
        var physical = Math.Max(0.0, damage.Physical * (1.0 - physicalReduction));
        return physical
            + EffectiveElementalDamage(damage.Fire, resistances.Fire)
            + EffectiveElementalDamage(damage.Cold, resistances.Cold)
            + EffectiveElementalDamage(damage.Lightning, resistances.Lightning)
            + EffectiveElementalDamage(damage.Poison, resistances.Poison);
    }

    /// <summary>Constants from <c>Game.Calculator.CalculateChanceToHit</c>.</summary>
    public const double ChanceToHitMultiplier = 1.05;
    public const double ChanceToHitMin = 0.05;

    /// <summary>ClientVerified port of <c>Game.Calculator.CalculateChanceToHit(attackRating,
    /// defenseRating, bonus, cap)</c>:
    /// <c>clamp(1.05*attackRating / (Pow(defenseRating*0.5, 0.75) + attackRating) + bonus, 0.05, cap)</c>.</summary>
    public static double ChanceToHit(double attackRating, double defenseRating, double bonus = 0.0,
        double cap = 1.0)
    {
        var atk = Math.Max(0.0, attackRating);
        var def = Math.Max(0.0, defenseRating);
        var value = ChanceToHitMultiplier * atk / (Math.Pow(def * 0.5, 0.75) + atk) + bonus;
        return Math.Clamp(value, ChanceToHitMin, cap);
    }

    /// <summary>ClientVerified port of <c>Game.Calculator.CalculateChance(chance)</c>:
    /// <c>chance &gt; 0 &amp;&amp; Rand.Value &lt;= chance</c>.</summary>
    public static bool RollChance(double chance, CombatRandom rng)
        => chance > 0.0 && rng.NextDouble() <= chance;

    /// <summary>Expected damage before variance, useful for balancing and tests.</summary>
    public static double ExpectedDamage(CombatantStats attacker, CombatantStats defender, AttackProfile profile)
    {
        var raw = Math.Max(0, attacker.EffectiveOffense) * Math.Max(0, profile.SkillMultiplier);
        var mitigated = raw * (1.0 - PhysicalDamageReduction(defender.EffectiveArmor, raw));
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
        // Formula layer is recovered from the client; the attribute inputs and the
        // variance/min-damage tuning are not, so they are labelled separately.
        var confidence = new DamageConfidence(
            Formula: RuleConfidence.ClientVerified,
            Inputs: RuleConfidence.Provisional,
            Execution: RuleConfidence.Inferred,
            AssumedReductionCap: true,
            AssumedVariance: profile.Variance > 0,
            AssumedMinDamage: true);

        // ClientVerified order: roll to hit, then crit, then mitigate and apply variance.
        var hit = RollChance(ChanceToHit(attacker.EffectiveAttackRating, defender.EffectiveEvasion), rng);
        var critChance = attacker.CritChance > 0 ? attacker.CritChance : profile.CritChance;
        var critical = RollChance(critChance, rng);
        if (!hit) return new DamageResult(0.0, false, confidence, false);
        var raw = Math.Max(0, attacker.EffectiveOffense) * Math.Max(0, profile.SkillMultiplier);
        var mitigated = raw * (1.0 - PhysicalDamageReduction(defender.EffectiveArmor, raw));
        var variance = 1.0 + (rng.NextDouble() * 2.0 - 1.0) * Math.Max(0, profile.Variance);
        var critFactor = critical ? Math.Max(1.0, profile.CritMultiplier) : 1.0;
        var damage = Math.Max(1.0, mitigated * Math.Max(0.05, variance) * critFactor);
        return new DamageResult(damage, critical, confidence, true);
    }

    /// <summary>
    /// ClientVerified typed-damage attack: rolls to hit/crit like <see cref="ResolveHit"/>, then
    /// scales the typed <paramref name="bundle"/> by the skill multiplier and crit factor and
    /// mitigates it with <see cref="MitigateDamage"/> (physical through armour, each element
    /// through the defender's capped resistance).
    /// </summary>
    public static DamageResult ResolveBundleAttack(
        CombatantStats attacker, DamageBundle bundle, CombatantStats defender,
        ResistanceBundle defenderResistances, AttackProfile profile, CombatRandom rng)
    {
        var confidence = new DamageConfidence(
            Formula: RuleConfidence.ClientVerified,
            Inputs: RuleConfidence.Provisional,
            Execution: RuleConfidence.Inferred,
            AssumedReductionCap: true,
            AssumedVariance: profile.Variance > 0,
            AssumedMinDamage: true);

        var hit = RollChance(ChanceToHit(attacker.EffectiveAttackRating, defender.EffectiveEvasion), rng);
        var critChance = attacker.CritChance > 0 ? attacker.CritChance : profile.CritChance;
        var critical = RollChance(critChance, rng);
        if (!hit) return new DamageResult(0.0, false, confidence, false);

        var scale = Math.Max(0, profile.SkillMultiplier) * (critical ? Math.Max(1.0, profile.CritMultiplier) : 1.0);
        var mitigated = MitigateDamage(bundle.Scale(scale), defender.EffectiveArmor, defenderResistances);
        var variance = 1.0 + (rng.NextDouble() * 2.0 - 1.0) * Math.Max(0, profile.Variance);
        var damage = Math.Max(1.0, mitigated * Math.Max(0.05, variance));
        return new DamageResult(damage, critical, confidence, true);
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
