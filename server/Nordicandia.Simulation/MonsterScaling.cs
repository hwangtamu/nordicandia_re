namespace Nordicandia.Simulation;

/// <summary>
/// Client monster base-stat curves. Recovered from <c>Game.Monster.Get*</c> in the Android
/// 1.9.3 <c>libil2cpp.so</c> (method addresses in <c>docs/web/D04_MONSTER_SCALING.md</c>); the
/// numeric constants are read straight from the module's constant pool, not guessed.
///
/// Shape (client): every stat is <c>(a*level + b*level^(e*expMult) + c) * finalMult [* k]</c>,
/// where <c>expMult</c> is the world/global "experience/difficulty" multiplier (a config field
/// that could not be resolved statically, exposed here as a parameter, default 1) and
/// <c>finalMult</c> is <c>Monster.FinalStatsMult</c> (rarity-selected; the property itself
/// returns 0.8 for the default case).
///
/// Status: formulas and constants are ClientVerified; <c>expMult</c> default and the rarity
/// selection of <c>finalMult</c> are Provisional (see the doc / B03 D04).
/// </summary>
public static class MonsterScaling
{
    /// <summary>Monster.get_FinalStatsMult() — default stat multiplier.</summary>
    public const double FinalStatsMult = 0.8;

    /// <summary>Monster.GetResistance() — flat monster resistance.</summary>
    public const double Resistance = 0.05;

    /// <summary>Monster.GetMaxPhysicalDamageReduction() — physical damage-reduction cap.</summary>
    public const double MaxPhysicalDamageReduction = 0.9;

    /// <summary>
    /// Rarity -> finalMult. Read from the client's rarity table (CalculateAttributes
    /// 0x02A6D0FC-0x02A6D128): non-Champion table[0] @0x1385BA0 = 0.8, Champion table[1]
    /// @0x1385BA8 = 0.88, Boss @0x13880D8 = 1.2. ClientVerified.
    /// </summary>
    public static double RarityFinalMult(int rarity) => rarity switch
    {
        6 => 1.2,  // Boss
        4 => 0.88, // Champion
        _ => FinalStatsMult, // Normal / Magic / Rare (= get_FinalStatsMult 0.8)
    };

    /// <summary>GetBaseLife. varianceMult is the client's Rand(0.99, 1.01) roll (1 = average).</summary>
    public static double Life(double level, double expMult = 1, double finalMult = FinalStatsMult, double varianceMult = 1)
        => varianceMult * (level * 0.41 + Math.Pow(level, 1.315 * expMult) * 0.12 + 8) * 2 * finalMult;

    /// <summary>GetArmor.</summary>
    public static double Armor(double level, double expMult = 1, double finalMult = FinalStatsMult)
        => (level * 150 + Math.Pow(level, 1.40 * expMult) * 0.01 + 80) * finalMult;

    /// <summary>GetEvasion.</summary>
    public static double Evasion(double level, double expMult = 1, double finalMult = FinalStatsMult)
        => (level * 8 + Math.Pow(level, 1.28 * expMult) * 0.006 + 4) * finalMult * 0.25;

    /// <summary>GetMinAttackRating.</summary>
    public static double MinAttackRating(double level, double expMult = 1, double finalMult = FinalStatsMult)
        => (level * 1.2 + Math.Pow(level, 1.20 * expMult) * 0.005 + 100) * finalMult * 0.5;

    /// <summary>GetMaxAttackRating.</summary>
    public static double MaxAttackRating(double level, double expMult = 1, double finalMult = FinalStatsMult)
        => (level * 1.2 + Math.Pow(level, 1.20 * expMult) * 0.005 + 100) * finalMult * 0.8;

    /// <summary>GetMinWeaponDamage / GetMaxWeaponDamage (the client computes the same base for
    /// both; the spread is applied by the caller).</summary>
    public static double WeaponDamage(double level, double expMult = 1, double finalMult = FinalStatsMult)
        => (level * 0.35 + Math.Pow(level, 1.24 * expMult) * 0.01) * finalMult * 2;

    /// <summary>GetExperience — note it takes no finalMult.</summary>
    public static double Experience(double level, double expMult = 1)
        => (Math.Pow(level, 1.33 * expMult) * 0.1 + 16) * 0.88 * 0.6;

    /// <summary>GetForceField. varianceMult is the client's Rand(0.99, 1.01) roll (1 = average).</summary>
    public static double ForceField(double level, double expMult = 1, double finalMult = FinalStatsMult, double varianceMult = 1)
        => varianceMult * (level * 0.4 + Math.Pow(level, 1.30 * expMult) * 0.2 + 50) * 2 * finalMult;

    /// <summary>The full client base-stat bundle for a monster at a level.</summary>
    public static MonsterBaseStats Stats(double level, double expMult = 1,
        double finalMult = FinalStatsMult, double varianceMult = 1)
        => new(
            Life: Life(level, expMult, finalMult, varianceMult),
            Armor: Armor(level, expMult, finalMult),
            Evasion: Evasion(level, expMult, finalMult),
            MinAttackRating: MinAttackRating(level, expMult, finalMult),
            MaxAttackRating: MaxAttackRating(level, expMult, finalMult),
            WeaponDamage: WeaponDamage(level, expMult, finalMult),
            Experience: Experience(level, expMult),
            ForceField: ForceField(level, expMult, finalMult, varianceMult),
            Resistance: Resistance,
            MaxPhysicalDamageReduction: MaxPhysicalDamageReduction);
}

/// <summary>Client monster base stats (see <see cref="MonsterScaling"/>).</summary>
public readonly record struct MonsterBaseStats(
    double Life,
    double Armor,
    double Evasion,
    double MinAttackRating,
    double MaxAttackRating,
    double WeaponDamage,
    double Experience,
    double ForceField,
    double Resistance,
    double MaxPhysicalDamageReduction);
