namespace Nordicandia.Server.WebApi;

/// <summary>
/// Web-side model of the four Aesir blessings.
///
/// ClientVerified: the four types (Odin/Tyr/Frigg/Thor) and the duration per offering size
/// (Small 10m, Medium 30m, Large 1h, ExtraLarge 4h) come from <c>GameStore.MakeOffering</c>;
/// the buff definition ids (276/278/280/282) from <c>BlessingBuffIds</c>.
///
/// Provisional: the opal costs (the client reads <c>OfflineCatalog.GetOpalPrice</c> for
/// product ids 24..27) and the attribute effects (the client applies them inside
/// <c>Aesir*Buff.Apply</c>; the exact <c>GameAttributeDA</c> fields were not decoded).
/// The effects are applied to the attribute map so they flow through the recovered engine.
/// </summary>
public static class Blessings
{
    public const int Odin = 1, Tyr = 2, Frigg = 3, Thor = 4;
    public static readonly int[] Types = { Odin, Tyr, Frigg, Thor };
    public static readonly int[] Sizes = { 1, 2, 3, 4 };

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

    /// <summary>Provisional per-type attribute bonuses, keyed by client attribute id
    /// (Odin magic find, Tyr physical damage, Frigg armour, Thor all resistance).</summary>
    public static Dictionary<int, double> AttributeBonuses(IEnumerable<int> activeTypes)
    {
        var map = new Dictionary<int, double>();
        foreach (var type in activeTypes)
        {
            void Add(int id, double value) => map[id] = map.GetValueOrDefault(id) + value;
            switch (type)
            {
                case Odin: Add(356, 0.25); break;   // Magic_Find_Bonus_Percent
                case Tyr: Add(504, 0.25); break;    // Weapon_Physical_Damage_Bonus_Percent
                case Frigg: Add(252, 0.50); break;  // Armor_Bonus_Percent
                case Thor: Add(1002, 0.15); break;  // Resistance_All
            }
        }
        return map;
    }

    /// <summary>Provisional human-readable effect, for the web tooltip.</summary>
    public static string Effect(int type) => type switch
    {
        Odin => "+25% Magic Find",
        Tyr => "+25% Physical Damage",
        Frigg => "+50% Armor",
        Thor => "+15% All Resistance",
        _ => string.Empty,
    };
}
