using Nordicandia.Simulation;

namespace Nordicandia.Server.WebApi;

/// <summary>
/// Bridges a character's stored attribute map to the combat ratings the simulation uses.
/// Runs the recovered <see cref="CharacterAttributeEngine"/> over the map so the web combat
/// consumes the same synthesised AttackRating/Armor/Evasion/CritChance/Life/Mana the client's
/// <c>CalculateCombatAttributes</c> reads.
/// </summary>
internal static class CharacterRatings
{
    public static CombatantStats Apply(CombatantStats provisional, IReadOnlyDictionary<int, double> attributes)
    {
        if (attributes.Count == 0) return provisional;
        var eval = CharacterAttributeEngine.Instance.Evaluate(attributes);
        return provisional with
        {
            AttackRating = eval.AttackRating,
            Armor = eval.Armor,
            Evasion = eval.Evasion,
            CritChance = eval.CritChanceMainHand,
            LifeMax = eval.LifeMax,
            ManaMax = eval.ManaMax,
            Damage = eval.WeaponDamage,
            Resistances = eval.Resistances,
        };
    }
}
