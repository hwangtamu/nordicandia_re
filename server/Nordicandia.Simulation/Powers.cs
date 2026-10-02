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

/// <summary>One attribute modifier a mastery rank grants.
/// <paramref name="Operator"/>: 0 Add, 1 Subtract, 2 Multiply (client enum).
/// <paramref name="ModifierType"/>: 0 PerLevel, 1 SpecificLevel.</summary>
public readonly record struct MasterySpec(
    int AttributeId, string AttributeName, double Value, double StartValue,
    int Operator, int ModifierType, int ModifierForSpecificLevel)
{
    /// <summary>Faithful port of PowerMasteryDefinition.GetAttributeSpecifierValue:
    /// PerLevel = StartValue + Value*rank; SpecificLevel = rank >= level ? StartValue + Value : 0;
    /// Subtract negates the result (the client's operators 0 and 2 both return the value).</summary>
    public double ContributionForRank(int rank)
    {
        double contribution = ModifierType == 1
            ? (rank >= ModifierForSpecificLevel ? StartValue + Value : 0)
            : StartValue + Value * rank;
        if (Operator == 1) contribution = -contribution;
        return contribution;
    }
}

/// <summary>A real skill-tree mastery (PowerMasteries.json) with its modifiers.</summary>
public sealed record MasteryProfile(string Name, int IntegerId, int MaxPoints, IReadOnlyList<MasterySpec> Specs);

/// <summary>Live per-skill state returned in a combat snapshot so the HUD labels the real skills.</summary>
public readonly record struct SkillStatus(int Slot, string Name, string Effect, double Cooldown, double MaxCooldown, double ManaCost, string Confidence, int Chains, string Special);

/// <summary>Per-skill mastery view for the web client.</summary>
public sealed record SkillMasteryView(string SkillName, int Slot, int Rank, int MaxPoints, IReadOnlyList<MasteryView> Masteries);

public sealed record MasteryView(string Name, int IntegerId, int Rank, int MaxPoints, IReadOnlyList<MasterySpec> Specs);
