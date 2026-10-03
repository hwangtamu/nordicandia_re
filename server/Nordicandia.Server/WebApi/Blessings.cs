namespace Nordicandia.Server.WebApi;

/// <summary>
/// Web-side model of the four Aesir blessings.
///
/// ClientVerified (Android): the four types and the duration per offering size
/// (Small 10m, Medium 30m, Large 1h, ExtraLarge 4h) come from <c>GameStore.MakeOffering</c>.
/// The attribute targets and magnitude are recovered from
/// <c>WindowAesirOffering.CreateAesirBuffOffline</c> (0x0255FAE8) and the matching
/// <c>Aesir*Buff.Apply</c>: the buff is built with a single constant magnitude
/// <c>0.4</c> (the .so double at 0x1387368) written to the target <c>*_Final</c> attribute:
///   * Odin  -> Strength/Dexterity/Intelligence/Vitality_Bonus_Percent_Final (ids 244/245/246/247)
///   * Tyr   -> Movement_Speed_Bonus_Percent_Final (id 115)
///   * Frigg -> Item_Quantity_Bonus_Percent (id 367)
///   * Thor  -> Weapon_Damage_Percent_Bonus_Final (id 89)
/// Offering size changes only the duration, not the magnitude.
/// </summary>
public static class Blessings
{
    public const int Odin = 1, Tyr = 2, Frigg = 3, Thor = 4;
    public static readonly int[] Types = { Odin, Tyr, Frigg, Thor };
    public static readonly int[] Sizes = { 1, 2, 3, 4 };

    /// <summary>Recovered blessing magnitude (0.4 = +40%), independent of offering size.</summary>
    public const double Magnitude = 0.4;

    // Recovered *_Bonus_Percent_Final / bonus attribute ids (attribute_formulas.json).
    private const int StrengthBonusPercentFinal = 244;
    private const int DexterityBonusPercentFinal = 245;
    private const int IntelligenceBonusPercentFinal = 246;
    private const int VitalityBonusPercentFinal = 247;
    private const int MovementSpeedBonusPercentFinal = 115;
    private const int ItemQuantityBonusPercent = 367;
    private const int WeaponDamagePercentBonusFinal = 89;

    public static string TypeName(int type) => type switch
    {
        Odin => "Odin",
        Tyr => "Tyr",
        Frigg => "Frigg",
        Thor => "Thor",
        _ => "Unknown",
    };

    public static string SizeName(int size) => size switch
    {
        1 => "Small",
        2 => "Medium",
        3 => "Large",
        4 => "ExtraLarge",
        _ => "Small",
    };

    /// <summary>Provisional opal cost for an offering size (Small/Medium/Large/ExtraLarge).</summary>
    public static int OpalCost(int size) => size switch
    {
        1 => 15,
        2 => 40,
        3 => 90,
        4 => 250,
        _ => 0,
    };

    /// <summary>ClientVerified duration for an offering size.</summary>
    public static TimeSpan Duration(int size) => size switch
    {
        1 => TimeSpan.FromMinutes(10),
        2 => TimeSpan.FromMinutes(30),
        3 => TimeSpan.FromHours(1),
        4 => TimeSpan.FromHours(4),
        _ => TimeSpan.FromMinutes(10),
    };

    /// <summary>Recovered per-type attribute bonuses, keyed by client attribute id (see type doc).</summary>
    public static Dictionary<int, double> AttributeBonuses(IEnumerable<int> activeTypes)
    {
        var map = new Dictionary<int, double>();
        foreach (var type in activeTypes)
        {
            void Add(int id, double value) => map[id] = map.GetValueOrDefault(id) + value;
            switch (type)
            {
                case Odin:
                    Add(StrengthBonusPercentFinal, Magnitude);
                    Add(DexterityBonusPercentFinal, Magnitude);
                    Add(IntelligenceBonusPercentFinal, Magnitude);
                    Add(VitalityBonusPercentFinal, Magnitude);
                    break;
                case Tyr: Add(MovementSpeedBonusPercentFinal, Magnitude); break;
                case Frigg: Add(ItemQuantityBonusPercent, Magnitude); break;
                case Thor: Add(WeaponDamagePercentBonusFinal, Magnitude); break;
            }
        }
        return map;
    }

    /// <summary>Human-readable recovered effect, for the web tooltip.</summary>
    public static string Effect(int type) => type switch
    {
        Odin => "+40% Strength/Dexterity/Intelligence/Vitality",
        Tyr => "+40% Movement Speed",
        Frigg => "+40% Item Quantity",
        Thor => "+40% Weapon Damage",
        _ => string.Empty,
    };
}
