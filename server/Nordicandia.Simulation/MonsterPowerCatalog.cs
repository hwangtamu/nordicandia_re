namespace Nordicandia.Simulation;

/// <summary>How a boss/monster power resolves against the player.</summary>
public enum MonsterPowerKind
{
    /// <summary>Normal melee/ranged attack.</summary>
    Attack,
    /// <summary>Instant area hit around the caster.</summary>
    Nova,
    /// <summary>Repeated expanding area hits over <c>Duration</c>.</summary>
    NovaSequence,
    /// <summary>Dash to the player then hit.</summary>
    Charge,
    /// <summary>Line/cone hit toward the player.</summary>
    Beam,
    /// <summary>Summon <c>Count</c> minions.</summary>
    Summon,
    /// <summary>Three zones (left/centre/right) around the player.</summary>
    TripleStrike,
}

/// <summary>Recovered effect descriptor for a boss power.</summary>
public sealed record MonsterPower(
    string Name, MonsterPowerKind Kind, double DamageMultiplier, double Radius,
    double Duration, string Element, int Count, double Cooldown);

/// <summary>
/// Ports the boss/monster powers referenced by <c>Brains.json</c>. The effect shape and the
/// parameter field names come from the client implementation classes
/// (<c>Game.WolfKingRoar</c>, <c>Game.Skills.VileDragonNova</c>, ...). Constants set directly in
/// the client constructors/parameter initialisers are used verbatim; the rest are Provisional
/// because they are supplied by the (unrecovered) monster-skill definitions.
///
/// Recovered values:
///   * WolfKingRoar: radius 3, damage buff x3, movement speed +0.2, cooldown/duration 8s
///   * WolfKingSummonPack: 3 minions; BoneDragonSummonSkeleton: 1; HelSummonPack: 8
///   * VileDragonNova: radius 6
/// </summary>
public static class MonsterPowerCatalog
{
    public static readonly IReadOnlyDictionary<string, MonsterPower> All = new Dictionary<string, MonsterPower>
    {
        ["WolfKingRoar"] = new("WolfKingRoar", MonsterPowerKind.Nova, 1.0, 3.0, 8.0, "Physical", 0, 8.0),
        ["WolfKingCharge"] = new("WolfKingCharge", MonsterPowerKind.Charge, 1.5, 2.0, 0.0, "Physical", 0, 6.0),
        ["WolfKingSummonPack"] = new("WolfKingSummonPack", MonsterPowerKind.Summon, 0.0, 0.0, 0.0, "Physical", 3, 12.0),
        ["DragonFireBreath"] = new("DragonFireBreath", MonsterPowerKind.Beam, 1.0, 3.0, 2.0, "Fire", 0, 8.0),
        ["BolomahlTripleStrike"] = new("BolomahlTripleStrike", MonsterPowerKind.TripleStrike, 1.5, 3.5, 0.0, "Physical", 0, 6.0),
        ["WarchiefWhirlwind"] = new("WarchiefWhirlwind", MonsterPowerKind.NovaSequence, 1.0, 4.0, 3.0, "Physical", 0, 7.0),
        ["FallenAngelRay"] = new("FallenAngelRay", MonsterPowerKind.Beam, 2.0, 3.0, 2.0, "Cold", 0, 8.0),
        ["BoneDragonSummonSkeleton"] = new("BoneDragonSummonSkeleton", MonsterPowerKind.Summon, 0.0, 0.0, 0.0, "Fire", 1, 14.0),
        ["VileDragonNova"] = new("VileDragonNova", MonsterPowerKind.Nova, 1.5, 6.0, 2.0, "Poison", 0, 9.0),
        ["HelSummonPack"] = new("HelSummonPack", MonsterPowerKind.Summon, 0.0, 0.0, 0.0, "Fire", 8, 14.0),
        ["HelHomingFire"] = new("HelHomingFire", MonsterPowerKind.Beam, 1.5, 2.5, 1.0, "Fire", 0, 7.0),
        ["HelFireNovas"] = new("HelFireNovas", MonsterPowerKind.NovaSequence, 1.5, 5.0, 4.0, "Fire", 0, 10.0),
        ["HelBigFireNova"] = new("HelBigFireNova", MonsterPowerKind.Nova, 2.0, 8.0, 0.0, "Fire", 0, 10.0),
        ["SkeletonCharge"] = new("SkeletonCharge", MonsterPowerKind.Charge, 1.5, 2.0, 0.0, "Physical", 0, 6.0),
        ["DeathInstantKill"] = new("DeathInstantKill", MonsterPowerKind.Nova, 5.0, 3.0, 0.0, "Physical", 0, 15.0),
        ["DemonShadowbolt"] = new("DemonShadowbolt", MonsterPowerKind.Beam, 1.5, 3.0, 1.0, "Physical", 0, 6.0),
        ["KibuLightningExplodeSequence"] = new("KibuLightningExplodeSequence", MonsterPowerKind.NovaSequence, 1.5, 5.0, 3.0, "Lightning", 0, 10.0),
        ["KibuShock"] = new("KibuShock", MonsterPowerKind.Nova, 1.5, 5.0, 0.0, "Lightning", 0, 8.0),
        ["KibuShockwave"] = new("KibuShockwave", MonsterPowerKind.Nova, 1.5, 6.0, 0.0, "Lightning", 0, 8.0),
        ["KibuSummonElemental"] = new("KibuSummonElemental", MonsterPowerKind.Summon, 0.0, 0.0, 0.0, "Lightning", 1, 14.0),
        ["OdrChargeTotemFire"] = new("OdrChargeTotemFire", MonsterPowerKind.Nova, 1.5, 4.0, 0.0, "Fire", 0, 7.0),
        ["OdrChargeTotemCold"] = new("OdrChargeTotemCold", MonsterPowerKind.Nova, 1.5, 4.0, 0.0, "Cold", 0, 7.0),
        ["OdrChargeTotemLightning"] = new("OdrChargeTotemLightning", MonsterPowerKind.Nova, 1.5, 4.0, 0.0, "Lightning", 0, 7.0),
        ["OdrEnrage"] = new("OdrEnrage", MonsterPowerKind.Nova, 0.5, 3.0, 0.0, "Physical", 0, 12.0),
        ["OdrSlam"] = new("OdrSlam", MonsterPowerKind.Nova, 2.0, 4.0, 0.0, "Physical", 0, 7.0),
        ["OdrSummonTotem"] = new("OdrSummonTotem", MonsterPowerKind.Summon, 0.0, 0.0, 0.0, "Fire", 1, 14.0),
        ["OdrTotemFireAttack"] = new("OdrTotemFireAttack", MonsterPowerKind.Beam, 1.5, 3.0, 1.0, "Fire", 0, 5.0),
        ["OdrTotemColdAttack"] = new("OdrTotemColdAttack", MonsterPowerKind.Beam, 1.5, 3.0, 1.0, "Cold", 0, 5.0),
        ["OdrTotemLightningAttack"] = new("OdrTotemLightningAttack", MonsterPowerKind.Beam, 1.5, 3.0, 1.0, "Lightning", 0, 5.0),
    };

    public static MonsterPower For(string name)
        => name is not null && All.TryGetValue(name, out var power) ? power : null;
}
