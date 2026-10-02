using Game;
using Nordicandia.Simulation;
using SharedNet.Constants.Game;

namespace Nordicandia.Server.WebApi;

/// <summary>
/// Provisional web loot table. Item power is derived from slot/rarity/level with a formula
/// that is deliberately simple and documented as Provisional (see docs/web/M2_STATUS.md);
/// the recovered client affix maths will replace it during calibration.
///
/// Equipment stats are stored on the item under these high attribute ids so the existing
/// SerializedItem shape is reused without colliding with real client attributes:
///   99001 Offense, 99002 Defense, 99003 Recovery, 99004 eligible equip slot.
/// </summary>
public static class LootTable
{
    public const int AttrOffense = 99001;
    public const int AttrDefense = 99002;
    public const int AttrRecovery = 99003;
    public const int AttrEquipSlot = 99004;

    private static readonly Dictionary<int, string[]> SlotNames = new()
    {
        [12] = new[] { "HandAxe", "Sword", "Warhammer", "BattleAxe" },
        [13] = new[] { "Buckler", "TowerShield", "RuneWard", "Warband" },
        [0] = new[] { "IronHelm", "WoolHood", "HornedHelm" },
        [1] = new[] { "Amulet", "Pendant", "Torc" },
        [2] = new[] { "Pauldrons", "Mantle", "Spaulders" },
        [3] = new[] { "PlateArmor", "Robe", "Hauberk" },
        [4] = new[] { "Cloak", "Cape" },
        [5] = new[] { "Bracers", "Vambraces" },
        [6] = new[] { "Gauntlets", "Gloves" },
        [7] = new[] { "Belt", "Girdle" },
        [8] = new[] { "Greaves", "Leggings" },
        [9] = new[] { "Boots", "Sabatons" },
        [10] = new[] { "Ring", "Signet" },
        [11] = new[] { "Ring", "Signet" },
    };

    private static readonly string[] RarityNames =
        { "F", "E", "D", "C", "B", "A", "AA", "AAA", "AAAA", "AAAAA", "S", "SS" };

    /// <summary>Limited provisional affix set (index, display suffix, stat id, factor of the
    /// item's base power). Names are provisional; the stat ids are the dedicated web ids.</summary>
    private static readonly (string Name, int AttributeId, double Factor)[] AffixTemplates =
    {
        ("of Might", AttrOffense, 0.10),
        ("of Warding", AttrDefense, 0.10),
        ("of Vigor", AttrRecovery, 0.15),
        ("of Fury", AttrOffense, 0.18),
        ("of the Bulwark", AttrDefense, 0.20),
        ("of Focus", AttrRecovery, 0.25),
    };

    public static string AffixName(int index)
        => index >= 0 && index < AffixTemplates.Length ? AffixTemplates[index].Name : "of Power";

    public static string RarityName(int rarity)
        => rarity >= 0 && rarity < RarityNames.Length ? RarityNames[rarity] : "?";

    public static SerializedItem CreateItem(LootDrop drop)
    {
        var names = SlotNames.TryGetValue(drop.Slot, out var list) ? list : new[] { "Trinket" };
        var name = names[Math.Abs(Hash(drop.Level, drop.Slot)) % names.Length];
        var baseStats = StatsFor(drop.Slot, drop.Rarity, drop.Level);
        var rng = new CombatRandom(drop.Seed == 0 ? 0x2545F4914F6CDD1DUL : drop.Seed);

        // 1 affix, +1 at C+, +1 at A+ (capped at 3), matching a limited first-pass affix set.
        var affixCount = 1 + (drop.Rarity >= 3 ? 1 : 0) + (drop.Rarity >= 5 ? 1 : 0);
        var offense = baseStats.Offense;
        var defense = baseStats.Defense;
        var recovery = baseStats.Recovery;
        var power = (4 + drop.Level * 2.2) * (1 + drop.Rarity * 0.55);
        var chosen = new List<(int Index, double Value)>();
        var used = new HashSet<int>();
        for (var i = 0; i < affixCount; i++)
        {
            var index = (int)(rng.NextDouble() * AffixTemplates.Length) % AffixTemplates.Length;
            if (!used.Add(index)) continue;
            var (_, attributeId, factor) = AffixTemplates[index];
            var value = Math.Round(power * factor);
            if (attributeId == AttrOffense) offense += value;
            else if (attributeId == AttrDefense) defense += value;
            else recovery += value;
            chosen.Add((index, value));
        }

        var suffix = string.Concat(chosen.Select(a => " " + AffixName(a.Index)));
        var item = new SerializedItem
        {
            Id = Guid.NewGuid(),
            Name = $"{name} ({RarityName(drop.Rarity)}){suffix}",
            Slot = ItemSlotTypes.Inventory,
            DefinitionIntegerId = 0,
            BaseRarity = (Rarity)Math.Clamp(drop.Rarity, 0, 11),
            Location = new SerializedItemInventoryLocation { Page = 1, Row = 0, Column = 0 },
            Attributes = new SerializedAttributes
            {
                Values = new Dictionary<AttributeOrigin, Dictionary<int, GameAttributeValue>>
                {
                    [AttributeOrigin.Item] = new()
                    {
                        [AttrOffense] = Value(offense),
                        [AttrDefense] = Value(defense),
                        [AttrRecovery] = Value(recovery),
                        [AttrEquipSlot] = Value(drop.Slot),
                    },
                },
                MultiplicativeValues = new(),
            },
            Affixes = chosen.Select(a => new SerializedAffix
            {
                DefinitionIntegerId = a.Index,
                Rarity = (Rarity)Math.Clamp(drop.Rarity, 0, 11),
                Attributes = new SerializedAttributes
                {
                    Values = new Dictionary<AttributeOrigin, Dictionary<int, GameAttributeValue>>
                    {
                        [AttributeOrigin.Item] = new()
                        {
                            [AffixTemplates[a.Index].AttributeId] = Value(a.Value),
                        },
                    },
                    MultiplicativeValues = new(),
                },
            }).ToList(),
        };
        return item;
    }

    private static GameAttributeValue Value(double v) => new() { Value = (int)Math.Round(v), ValueD = v };

    private static int Hash(int level, int slot)
    {
        unchecked
        {
            return (level * 397) ^ slot;
        }
    }

    /// <summary>Provisional per-slot stat weights; weapon offence-heavy, armor defence-heavy.</summary>
    public static (double Offense, double Defense, double Recovery) StatsFor(int slot, int rarity, int level)
    {
        var rarityMult = 1 + rarity * 0.55;
        var power = (4 + level * 2.2) * rarityMult;
        return slot switch
        {
            12 or 13 => (Math.Round(power * 2.2), Math.Round(power * 0.2), Math.Round(power * 0.2)),
            1 => (Math.Round(power * 0.5), Math.Round(power * 0.5), Math.Round(power * 0.8)),
            10 or 11 => (Math.Round(power * 0.9), Math.Round(power * 0.9), Math.Round(power * 0.4)),
            _ => (Math.Round(power * 0.1), Math.Round(power * 1.6), Math.Round(power * 0.25)),
        };
    }

    public static double AttributeOf(SerializedItem item, int attributeId)
    {
        if (item?.Attributes?.Values == null) return 0;
        if (!item.Attributes.Values.TryGetValue(AttributeOrigin.Item, out var map) || map == null) return 0;
        return map.TryGetValue(attributeId, out var value) ? value.ValueD : 0;
    }

    public static int EquipSlotOf(SerializedItem item) => (int)AttributeOf(item, AttrEquipSlot);

    public static bool IsEquipped(SerializedItem item) => (int)item.Slot is >= 0 and <= 13;

    /// <summary>Sum of equipment bonuses over equipped items.</summary>
    public static (double Offense, double Defense, double Recovery) EquipmentBonus(IEnumerable<SerializedItem> items)
    {
        double offense = 0, defense = 0, recovery = 0;
        foreach (var item in items ?? Enumerable.Empty<SerializedItem>())
        {
            if (item == null || !IsEquipped(item)) continue;
            offense += AttributeOf(item, AttrOffense);
            defense += AttributeOf(item, AttrDefense);
            recovery += AttributeOf(item, AttrRecovery);
        }
        return (offense, defense, recovery);
    }
}
