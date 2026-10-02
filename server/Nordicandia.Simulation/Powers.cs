namespace Nordicandia.Simulation;

/// <summary>
/// A real Nordicandia active skill as extracted from <c>Powers.json</c>. The name,
/// description, icon and tags are client-verified; the <paramref name="Effect"/> and its
/// numeric parameters are provisional until the client's power execution is recovered.
/// </summary>
public sealed record SkillProfile(
    int Slot,
    string Name,
    string Description,
    string Icon,
    string Effect,      // strike | nova | rally
    double Multiplier,
    double Cooldown,
    double Radius,
    double HealPercent,
    double BuffBonus,
    double BuffSeconds);

/// <summary>A real passive skill; <see cref="Effect"/> is a provisional category.</summary>
public sealed record PassiveProfile(
    string Name,
    string Description,
    string Icon,
    string Effect,      // might | warding | haste | fortune
    double OffenseBonus,
    double HealthBonus);

public sealed record ClassPowers(string ClassName, IReadOnlyList<SkillProfile> Active, PassiveProfile Passive);

/// <summary>Live per-skill state returned in a combat snapshot so the HUD labels the real skills.</summary>
public readonly record struct SkillStatus(int Slot, string Name, string Effect, double Cooldown, double MaxCooldown);
