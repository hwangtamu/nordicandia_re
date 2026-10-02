namespace Nordicandia.Simulation;

/// <summary>
/// A real Nordicandia active skill as extracted from <c>Powers.json</c>. The name,
/// description, icon and tags are client-verified; <paramref name="Effect"/> is derived
/// from the recovered attribute values, and <paramref name="Values"/> carries every
/// attribute the client sets (cooldown, mana, chains, leech, minion inheritance, ...).
/// </summary>
public sealed record SkillProfile(
    int Slot,
    string Name,
    string Description,
    string Icon,
    string Effect,      // strike | nova | chain | summon | mobility | shield | leech | projectile
    double Multiplier,
    double Cooldown,
    double Radius,
    double ManaCost,
    double HealPercent,
    double BuffBonus,
    double BuffSeconds,
    string Confidence,
    IReadOnlyDictionary<string, double> Values); // client-verified | provisional-behaviour

/// <summary>A real passive skill; <see cref="Effect"/> is a provisional category.</summary>
public sealed record PassiveProfile(
    string Name,
    string Description,
    string Icon,
    string Effect,      // might | warding | haste | fortune
    double OffenseBonus,
    double HealthBonus,
    string Confidence);

public sealed record ClassPowers(string ClassName, IReadOnlyList<SkillProfile> Active, PassiveProfile Passive);

/// <summary>Live per-skill state returned in a combat snapshot so the HUD labels the real skills.</summary>
public readonly record struct SkillStatus(int Slot, string Name, string Effect, double Cooldown, double MaxCooldown, double ManaCost, string Confidence, int Chains, string Special);
