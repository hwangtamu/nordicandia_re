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
            // Recovered on-hit mechanics (GameAttributes ids 431/663/428/429).
            ForkChance = eval.Resolve("Projectile_Auto_Attacks_Fork_Chance"),
            ChainChance = eval.Resolve("Projectile_Auto_Attacks_Chain_Chance"),
            PoisonChance = eval.Resolve("Poison_Chance_On_Hit"),
            PoisonOnHit = eval.Resolve("Poison_On_Hit") > 0,
            DoubleDamageOnCritPoisoned = eval.Resolve("Double_Damage_Chance_On_Crit_On_Poisoned_Target"),
            // Movement_Speed_Total = Base_Movement_Speed * (1 + ...) * (1 + Final); base is 1.
            MoveSpeedMultiplier = 1 + eval.Resolve("Movement_Speed_Bonus_Percent_Final"),
            // Element conversion (ids 153-156 + 633-636) and resistance penetration
            // (Fire/Cold/Lightning/Poison_Resistance_Penetration_Total = 98/99/113/114).
            Conversion = new DamageBundle(0,
                eval.Resolve("Physical_Weapon_Damage_Converted_To_Fire_Percent") + eval.Resolve("Weapon_Damage_Converted_To_Fire_Percent"),
                eval.Resolve("Physical_Weapon_Damage_Converted_To_Cold_Percent") + eval.Resolve("Weapon_Damage_Converted_To_Cold_Percent"),
                eval.Resolve("Physical_Weapon_Damage_Converted_To_Lightning_Percent") + eval.Resolve("Weapon_Damage_Converted_To_Lightning_Percent"),
                eval.Resolve("Physical_Weapon_Damage_Converted_To_Poison_Percent") + eval.Resolve("Weapon_Damage_Converted_To_Poison_Percent")),
            Penetration = new ResistanceBundle(
                eval.Resolve("Fire_Resistance_Penetration_Total"),
                eval.Resolve("Cold_Resistance_Penetration_Total"),
                eval.Resolve("Lightning_Resistance_Penetration_Total"),
                eval.Resolve("Poison_Resistance_Penetration_Total")),
            ArmorPenetration = eval.Resolve("Armor_Piercing_Percent_Total"),
        };
    }
}
