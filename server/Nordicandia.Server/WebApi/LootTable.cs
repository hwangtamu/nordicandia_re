using System.Reflection;
using System.Text.Json;
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
    /// <summary>Web-only attribute holding the item's set id (SetCatalog key); enables set bonuses.</summary>
    public const int AttrSetId = 99005;
    /// <summary>Web-only attribute holding the level required to equip the item.</summary>
    public const int AttrRequiredLevel = 99006;
    /// <summary>Web-only flag (1) marking a unique/legendary item.</summary>
    public const int AttrUnique = 99007;
    /// <summary>Web-only attribute holding the client's AffixType (0=Prefix, 1=Suffix, 2=Implicit).</summary>
    public const int AttrAffixType = 99008;

    private static readonly Lazy<IReadOnlyList<(int Count, int Weight)>> AffixCountWeights = new(LoadAffixCountWeights);

    /// <summary>Affix-count distribution from Droprates.json.NumAffixesRatio. ClientVerified data:
    /// <c>ItemGenerator.InitializeNumAffixesPool</c> builds a weighted int randomizer from exactly
    /// this table and <c>GetRandomNumAffixes</c> draws from it.</summary>
    public static IReadOnlyList<(int Count, int Weight)> NumAffixesWeights => AffixCountWeights.Value;

    private static IReadOnlyList<(int Count, int Weight)> LoadAffixCountWeights()
    {
        var assembly = Assembly.GetExecutingAssembly();
        var resource = assembly.GetManifestResourceNames()
            .First(n => n.EndsWith("GameData.num_affixes_ratio.json", StringComparison.Ordinal));
        using var stream = assembly.GetManifestResourceStream(resource)!;
        var raw = JsonSerializer.Deserialize<Dictionary<string, int>>(stream)!;
        return raw.OrderBy(kv => int.Parse(kv.Key)).Select(kv => (int.Parse(kv.Key), kv.Value)).ToList();
    }

    /// <summary>Weighted affix-count roll (the client's <c>GetRandomNumAffixes</c>).</summary>
    public static int RollAffixCount(CombatRandom rng)
    {
        var weights = AffixCountWeights.Value;
        var total = weights.Sum(w => w.Weight);
        if (total <= 0) return 1;
        var roll = rng.NextDouble() * total;
        foreach (var (count, weight) in weights)
        {
            roll -= weight;
            if (roll < 0) return count;
        }
        return weights[^1].Count;
    }

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

    /// <summary>Number of real client affixes available to roll.</summary>
    public static int AffixCatalogSize => AffixCatalog.Entries.Count;

    /// <summary>Display name for an affix (the client attribute it grants).</summary>
    public static string AffixName(int attributeId)
    {
        foreach (var affix in AffixCatalog.Entries)
            foreach (var a in affix.Attributes)
                if (a.AttributeId == attributeId) return a.AttributeName;
        return "Affix";
    }

    public static string RarityName(int rarity)
        => rarity >= 0 && rarity < RarityNames.Length ? RarityNames[rarity] : "?";

    public static SerializedItem CreateItem(LootDrop drop, ItemCatalog.RarityType? forceRarityType = null,
        double magicFind = 0)
    {
        // E02: a guaranteed item type (the boss's RegularBoss drop, e.g. HelheimKey) is not a slot
        // pick, so build it directly from the definition.
        if (!string.IsNullOrEmpty(drop.ForceType)) return CreateTypeItem(drop);
        var rng = new CombatRandom(drop.Seed == 0 ? 0x2545F4914F6CDD1DUL : drop.Seed);
        // A separate stream for affix generation, so the count/values don't shift the base item's
        // weapon/armour rolls (the client uses one global Rand; the web keeps its seeded streams).
        var affixRng = new CombatRandom((drop.Seed == 0 ? 0x2545F4914F6CDD1DUL : drop.Seed) ^ 0xA24BAED4963EE407UL);
        // Roll Normal/Unique/Set using the client weights (or use the forced type, e.g. a set
        // merchant offer or a pity-guaranteed unique), then pick a matching definition.
        var rarityType = forceRarityType ?? ItemCatalog.RollRarityType(rng, magicFind);
        var definition = ItemCatalog.Pick(drop.Slot, rarityType, rng, drop.ClassId, drop.Table);
        var fallbackNames = SlotNames.TryGetValue(drop.Slot, out var list) ? list : new[] { "Trinket" };
        var name = definition?.Name ?? fallbackNames[Math.Abs(Hash(drop.Level, drop.Slot)) % fallbackNames.Length];
        var definitionId = definition?.IntegerId ?? 0;
        var baseStats = StatsFor(drop.Slot, drop.Rarity, drop.Level);
        // Rolls a real implicit value for the item's rarity when the definition has one; else the
        // provisional fallback. The result is scaled by level via GameBalance.ScalingFunctions
        // (matching the client's GetMinMaxValues).
        double RollImplicit(string attributeName, double fallback, double scale = 1.0)
        {
            var range = definition?.Find(attributeName)?.For(drop.Rarity);
            return (range is { } r ? r.Roll(rng) : fallback) * scale;
        }
        var weaponScale = ScalingCatalog.WeaponDamageFactor(drop.Level);
        var armorScale = ScalingCatalog.ArmorFactor(drop.Level);
        var evasionScale = ScalingCatalog.EvasionFactor(drop.Level);

        // Affixes are drawn from the real client affix catalog; the count comes from the client's
        // NumAffixesRatio distribution (GetRandomNumAffixes), not a web rarity curve.
        var affixCount = RollAffixCount(affixRng);
        var offense = baseStats.Offense;
        var defense = baseStats.Defense;
        var recovery = baseStats.Recovery;
        // The client passes excludedAffixDefinitions to GenerateRandomAffix and filters
        // candidates before drawing. A duplicate must not consume a rolled slot. E01: the pool is
        // the full client catalog filtered to item-domain (0) random Prefix/Suffix affixes.
        // E01: only affixes whose item-type tags match the chosen definition (ItemTypes TagIds/
        // AffixIds with parent inheritance). A null definition falls back to the whole item pool.
        var typeTags = AffixCatalog.TagsForType(definition?.Type);
        var pool = AffixCatalog.Entries
            .Where(a => a.IsPrefixOrSuffix && a.Domain == AffixCatalog.DomainItem && a.EligibleFor(definition?.Type))
            .DistinctBy(a => a.Name).ToList();
        // E02: weighted by TagData spawn weight; values scaled by TagData ValueMultiplier.
        var weights = pool.Select(a => a.SpawnWeight(typeTags)).ToList();
        // CreateItem initializes once (affixNumber=1, highest=null), then calls
        // GenerateRandomAffix with allowReinitializingRarityPool=false. Do not introduce
        // the helper's optional inter-affix bias into the normal item-generation path.
        var rarityWeights = AffixRarityCatalog.Weights(magicFind);
        var chosen = new List<(AffixCatalog.Affix Affix, int Rarity, List<(AffixCatalog.AffixAttribute Attribute, double Value)> Values)>();
        for (var i = 0; i < affixCount && pool.Count > 0; i++)
        {
            var total = 0.0;
            foreach (var w in weights) total += w;
            var roll = affixRng.NextDouble() * total;
            var index = 0;
            while (index < pool.Count - 1 && roll >= weights[index]) { roll -= weights[index]; index++; }
            var affix = pool[index];
            pool.RemoveAt(index);
            weights.RemoveAt(index);
            var affixRarity = AffixRarityCatalog.Roll(affixRng, rarityWeights);
            var values = AffixCatalog.RollAll(affix, affixRarity, affixRng);
            var mult = affix.ValueMultiplier(typeTags);
            if (mult != 1.0) values = values.Select(v => (v.Attribute, v.Value * mult)).ToList();
            chosen.Add((affix, affixRarity, values));
        }

        var suffix = string.Concat(chosen.Select(a => " " + a.Affix.Name));
        var itemAttributes = new Dictionary<int, GameAttributeValue>
        {
            [AttrOffense] = Value(offense),
            [AttrDefense] = Value(defense),
            [AttrRecovery] = Value(recovery),
            [AttrEquipSlot] = Value(drop.Slot),
            [AttrRequiredLevel] = Value(Math.Max(1, drop.Level)),
        };
        // Items carry the client's attribute ids so the recovered synthesis formulas drive the
        // character's ratings. Base values come from the real item definition's implicit affixes
        // (rolled), with the provisional curve as fallback.
        if (drop.Slot == 12)
        {
            // Main-hand: physical (implicit) + one random element, attack speed, weapon crit, range.
            var physMin = RollImplicit("Local_Implicit_Physical_Base_Damage_Min", offense * 0.8, weaponScale);
            var physDelta = RollImplicit("Local_Implicit_Physical_Base_Damage_Delta", offense * 0.4, weaponScale);
            itemAttributes[2500] = Value(Math.Round(physMin, 4)); // Local_Implicit_Physical_Base_Damage_Min
            itemAttributes[2501] = Value(Math.Round(physDelta, 4)); // ..._Delta
            itemAttributes[507] = Value(Math.Round(physMin, 4));   // Item_Weapon_Physical_Damage_Min_MainHand
            itemAttributes[508] = Value(Math.Round(physDelta, 4)); // ..._Delta_MainHand
            var element = new[] { 1407, 1507, 1607, 1707 }[(int)(rng.NextDouble() * 4) & 3];
            itemAttributes[element] = Value(Math.Round(physMin * 0.5, 4));
            itemAttributes[element + 1] = Value(Math.Round(physDelta * 0.5, 4));
            var speed = RollImplicit("Local_Implicit_Base_Attack_Speed", 1.3 + drop.Rarity * 0.06);
            itemAttributes[462] = Value(Math.Round(speed, 4));      // Local_Implicit_Base_Attack_Speed
            itemAttributes[454] = Value(Math.Round(speed, 4));      // Item_Attack_Speed_MainHand
            var critPct = RollImplicit("Local_Base_Crit_Chance", 4 + drop.Rarity * 0.4);
            itemAttributes[718] = Value(Math.Round(critPct, 4));    // Local_Base_Crit_Chance (percent)
            itemAttributes[701] = Value(Math.Round(critPct / 100.0, 4)); // Item_Crit_Chance_MainHand (fraction)
            itemAttributes[217] = Value(2 + Math.Round(drop.Rarity * 0.2, 1)); // Item_Attack_Range_MainHand
        }
        else if (drop.Slot == 13)
        {
            var offArmor = RollImplicit("Local_Implicit_Base_Armor", defense * 0.5, armorScale);
            itemAttributes[273] = Value(Math.Round(offArmor, 4));   // Local_Implicit_Base_Armor
            itemAttributes[251] = Value(Math.Round(offArmor, 4));   // Armor
            itemAttributes[455] = Value(Math.Round(1.2 + drop.Rarity * 0.04, 3)); // Item_Attack_Speed_OffHand
        }
        else
        {
            // Armour: Armor_Total reads the plain Armor attribute; boots/cloaks/wrists also evade.
            var armor = RollImplicit("Local_Implicit_Base_Armor", defense * 0.6, armorScale);
            itemAttributes[273] = Value(Math.Round(armor, 4));      // Local_Implicit_Base_Armor
            itemAttributes[251] = Value(Math.Round(armor, 4));      // Armor
            if (drop.Slot is 4 or 5 or 9)
            {
                var evasion = RollImplicit("Local_Implicit_Base_Evasion", defense * 0.35, evasionScale);
                itemAttributes[276] = Value(Math.Round(evasion, 4)); // Local_Implicit_Base_Evasion
                itemAttributes[256] = Value(Math.Round(evasion, 4)); // Evasion
            }
            // Accessories and armour carry a small all-resistance (Resistance_All = 1002).
            itemAttributes[1002] = Value(Math.Round(defense * 0.0006, 4));
        }
        // Equipment durability (Durability_Implicit_Base = 27). Durability_Total (30) is
        // Durability_Implicit_Base + Durability_Base (28) + Durability (29), default 0; the
        // client's disassemble chance is min(1.0, Durability_Total). Equipment is undamaged here,
        // so it disassembles; 0 gates non-durable items out. No RNG, so the loot stream is unchanged.
        if ((int)drop.Slot is >= 0 and <= 13)
        {
            var durability = definition?.Find("Durability_Implicit_Base")?.For(drop.Rarity)?.Min ?? 100.0;
            itemAttributes[27] = Value(Math.Round(durability, 2));
            itemAttributes[30] = Value(Math.Round(durability, 2));
        }
        // All of the definition's implicit affixes (unique items carry their special affixes here),
        // except those already mapped explicitly above, so unique/set bonuses are preserved.
        if (definition is { } def)
        {
            var engine = CharacterAttributeEngine.Instance;
            foreach (var implicitValue in def.Implicits)
            {
                if (!engine.TryGetId(implicitValue.Name, out var attributeId)) continue;
                if (itemAttributes.ContainsKey(attributeId)) continue;
                if (implicitValue.For(drop.Rarity) is { } range)
                    itemAttributes[attributeId] = Value(Math.Round(range.Roll(rng), 4));
            }
        }
        if (definition is { IsUnique: true }) itemAttributes[AttrUnique] = Value(1);
        // Set membership belongs to the definition, never to a random rarity roll.
        if (definition is { SetId: { } definitionSetId })
            itemAttributes[AttrSetId] = Value(definitionSetId);
        var item = new SerializedItem
        {
            Id = Guid.NewGuid(),
            Name = $"{name} ({RarityName(drop.Rarity)}){suffix}",
            Slot = ItemSlotTypes.Inventory,
            DefinitionIntegerId = definitionId,
            BaseRarity = (Rarity)Math.Clamp(drop.Rarity, 0, 11),
            Location = new SerializedItemInventoryLocation { Page = 1, Row = 0, Column = 0 },
            Attributes = new SerializedAttributes
            {
                Values = new Dictionary<AttributeOrigin, Dictionary<int, GameAttributeValue>>
                {
                    [AttributeOrigin.Item] = itemAttributes,
                },
                MultiplicativeValues = new(),
            },
            Affixes = chosen.Select(a => new SerializedAffix
            {
                // E01: the client's SerializedAffix references the affix definition, not the
                // granted attribute (several affixes can share a primary attribute).
                DefinitionIntegerId = a.Affix.IntegerId != 0 ? a.Affix.IntegerId : a.Affix.AttributeId,
                Rarity = (Rarity)a.Rarity,
                Attributes = new SerializedAttributes
                {
                    Values = new Dictionary<AttributeOrigin, Dictionary<int, GameAttributeValue>>
                    {
                        [AttributeOrigin.Item] = AffixValues(a.Affix, a.Values),
                    },
                    MultiplicativeValues = new(),
                },
            }).ToList(),
        };
        return item;
    }

    /// <summary>E02: a definition-typed item with no slot implicit rolls (guaranteed drops such as
    /// the boss's HelheimKey / Helheim's Bless+portals).</summary>
    private static SerializedItem CreateTypeItem(LootDrop drop)
    {
        var definition = ItemCatalog.Definitions.FirstOrDefault(d => d.Type == drop.ForceType);
        var resolved = definition.Name is not null;
        return new SerializedItem
        {
            Id = Guid.NewGuid(),
            Name = resolved ? definition.Name : drop.ForceType,
            Slot = ItemSlotTypes.Inventory,
            DefinitionIntegerId = resolved ? definition.IntegerId : 0,
            BaseRarity = (Rarity)Math.Clamp(drop.Rarity, 0, 11),
            Location = new SerializedItemInventoryLocation { Page = 1, Row = 0, Column = 0 },
            Attributes = new SerializedAttributes
            {
                Values = new Dictionary<AttributeOrigin, Dictionary<int, GameAttributeValue>>
                {
                    [AttributeOrigin.Item] = new() { [AttrRequiredLevel] = Value(Math.Max(1, drop.Level)) },
                },
                MultiplicativeValues = new(),
            },
            Affixes = new List<SerializedAffix>(),
        };
    }

    private static GameAttributeValue Value(double v) => new() { Value = (int)Math.Round(v), ValueD = v };

    /// <summary>E01: an affix may grant several attributes; store each rolled value plus the
    /// client AffixType (Prefix/Suffix) on the affix record.</summary>
    private static Dictionary<int, GameAttributeValue> AffixValues(AffixCatalog.Affix affix,
        List<(AffixCatalog.AffixAttribute Attribute, double Value)> values)
    {
        var map = new Dictionary<int, GameAttributeValue>();
        foreach (var (attribute, value) in values)
            map[attribute.AttributeId] = Value(Math.Round(value, 4));
        map[AttrAffixType] = Value(affix.GenerationType);
        return map;
    }

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

    /// <summary>True when the item carries the given attribute, even at value 0 (set id 0 is a
    /// valid set, so presence — not value — is what marks a set item).</summary>
    public static bool HasAttribute(SerializedItem item, int attributeId)
    {
        if (item?.Attributes?.Values == null) return false;
        if (!item.Attributes.Values.TryGetValue(AttributeOrigin.Item, out var map) || map == null) return false;
        return map.ContainsKey(attributeId);
    }

    /// <summary>True for a unique/legendary or set item (used by the drop-pity counter).</summary>
    public static bool IsUniqueOrSet(SerializedItem item)
        => AttributeOf(item, AttrUnique) > 0 || HasAttribute(item, AttrSetId);

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
