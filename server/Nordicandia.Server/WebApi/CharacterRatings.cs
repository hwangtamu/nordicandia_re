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
            // AttackPayload.Resolve order: dodge then block. Defaults come from the baseline
            // (Base_Dodge_Chance 0.05, Base_Blocked_Damage_Taken_Multiplier 0.5).
            DodgeChance = Math.Max(eval.Resolve("Dodge_Chance_Total"), eval.Resolve("Dodge_Chance_Spell_Total")),
            BlockChance = Math.Max(eval.Resolve("Block_Chance_Total"), eval.Resolve("Block_Chance_Spell_Total")),
            BlockedDamageMultiplier = eval.Resolve("Blocked_Damage_Taken_Multiplier_Total"),
            // IsEvaded inputs and crit immunity.
            HitChanceBonus = eval.Resolve("Hit_Chance_Bonus_Percent"),
            HitChanceCap = eval.Resolve("Hit_Chance_Cap") is var cap && cap > 0 ? cap : 1.0,
            AlwaysHits = eval.Resolve("Always_Hits") > 0 || eval.Resolve("Always_Hits_Global") > 0,
            IgnoresCrits = eval.Resolve("Ignores_Critical_Hits") > 0,
            // Deadly strike (axes): chance from Deadly_Strike_Chance_Total, x2 on a crit.
            DeadlyStrikeChance = eval.Resolve("Deadly_Strike_Chance_Total"),
            ChampionMonsterFindBonus = eval.Resolve("ChampionMonster_Find_Bonus_Percent"),
            UniqueMonsterFindBonus = eval.Resolve("UniqueMonster_Find_Bonus_Percent"),
        };
    }
}
