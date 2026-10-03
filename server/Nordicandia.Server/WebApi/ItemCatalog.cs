using System.Reflection;
using System.Text.Json;
using Nordicandia.Simulation;

namespace Nordicandia.Server.WebApi;

/// <summary>
/// Real item definitions (from gamedata_decrypted/Items.json), extracted with their implicit
/// base attributes (weapon damage/speed/crit, armour, evasion) and their per-rarity ranges to
/// tools/web-content/generated/item_catalog.json and embedded. Loot picks a definition for the
/// slot and rolls the implicit range matching the item's rarity, so base values are calibrated
/// to the client data.
/// </summary>
public static class ItemCatalog
{
    public readonly record struct Range(double Min, double Max)
    {
        public double Roll(CombatRandom rng) => Min + rng.NextDouble() * (Max - Min);
    }

    public readonly record struct Implicit(string Name, Range? Default, IReadOnlyList<(int Rarity, Range Range)> ByRarity)
    {
        /// <summary>The range for <paramref name="itemRarity"/>: the highest rarity threshold at or
        /// below it, else the lowest available rarity, else the default.</summary>
        public Range? For(int itemRarity)
        {
            Range? best = null;
            var bestRarity = int.MinValue;
            foreach (var (rarity, range) in ByRarity)
                if (rarity <= itemRarity && rarity >= bestRarity) { best = range; bestRarity = rarity; }
            if (best is null && ByRarity.Count > 0)
                best = ByRarity.OrderBy(r => r.Rarity).First().Range;
            return best ?? Default;
        }
    }

    /// <summary>Item rarity type from Droprates.json (Normal / Unique / Set).</summary>
    public enum RarityType { Normal, Unique, Set }

    public readonly record struct Definition(string Name, int IntegerId, string Type, bool IsUnique,
        int? SetId, IReadOnlyList<Implicit> Implicits)
    {
        public Implicit? Find(string attributeName)
        {
            foreach (var implicitValue in Implicits)
                if (implicitValue.Name == attributeName) return implicitValue;
            return null;
        }
    }

    private static readonly Lazy<IReadOnlyDictionary<string, double>> BaseRarityWeights = new(() =>
    {
        using var stream = ReadResource("GameData.droprate_weights.json");
        return JsonSerializer.Deserialize<Dictionary<string, double>>(stream)!;
    });

    /// <summary>Loaded independently of item definitions, including the first rarity roll.</summary>
    public static IReadOnlyDictionary<string, double> RarityTypeWeights => BaseRarityWeights.Value;

    /// <summary>Client InternalInitializeSetOrUniqueItemRarityTypes (0x02CA0534).
    /// With p=100*MF, factor=1: Unique boost=1+.01*p*225/(.5*p+3*225),
    /// Set boost=1+.01*p*150/(.6*p+4.5*150). Normal DIVIDES by 1+.01*p/(p+1).
    /// Round each resulting weight to even. ARM64 FMOV immediates are 3 and 4.5;
    /// the Cpp2IL annotated text decodes them incorrectly as 2 and 4.</summary>
    public static (double Normal, double Unique, double Set) RarityWeightsFor(
        double magicFind = 0, double magicFindFactorMultiplier = 1)
    {
        if (!double.IsFinite(magicFind) || !double.IsFinite(magicFindFactorMultiplier) || magicFindFactorMultiplier < 0)
            throw new ArgumentOutOfRangeException(nameof(magicFind), "Loot luck inputs must be finite and the factor nonnegative.");
        var mf = Math.Max(0, magicFind);
        double Boost(double slope, double coefficient, double divisor)
        {
            // Zero multiplier disables the bonus; avoid the native 0/0 singularity at MF=0.
            if (magicFindFactorMultiplier == 0 || mf == 0) return 1;
            var p = 100 * mf;
            var c = coefficient * magicFindFactorMultiplier;
            return 1 + 0.01 * p * c / (p * slope + divisor * c);
        }
        double Weight(string type) => RarityTypeWeights.GetValueOrDefault(type, 0);
        return (Math.Round(Weight("Normal") / Boost(1, 1, 1), MidpointRounding.ToEven),
                Math.Round(Weight("Unique") * Boost(0.5, 225, 3), MidpointRounding.ToEven),
                Math.Round(Weight("Set") * Boost(0.6, 150, 4.5), MidpointRounding.ToEven));
    }

    public static RarityType RollRarityType(CombatRandom rng, double magicFind = 0)
    {
        var (normal, unique, set) = RarityWeightsFor(magicFind);
        var total = normal + unique + set;
        if (total <= 0) return RarityType.Normal;
        var roll = rng.NextDouble() * total;
        if (roll < set) return RarityType.Set;
        if (roll < set + unique) return RarityType.Unique;
        return RarityType.Normal;
    }

    // Equip slot -> candidate item type names (client ItemTypes).
    private static readonly string[][] SlotTypes =
    {
        new[] { "HeavyHelmet", "MediumHelmet", "LightHelmet" },
        new[] { "Amulet" },
        new[] { "HeavyShoulders", "MediumShoulders", "LightShoulders" },
        new[] { "HeavyChest", "MediumChest", "LightChest" },
        new[] { "Cloak" },
        new[] { "HeavyBracers", "MediumBracers", "LightBracers" },
        new[] { "HeavyGloves", "MediumGloves", "LightGloves" },
        new[] { "Belt" },
        new[] { "HeavyLeggings", "MediumLeggings", "LightLeggings" },
        new[] { "HeavyBoots", "MediumBoots", "LightBoots" },
        new[] { "Ring" },
        new[] { "Ring" },
        new[]
        {
            "Axe1H", "Mace1H", "Sword1H", "Crossbow1H", "ElementalWand1H", "FireWand1H", "ColdWand1H",
            "LightningWand1H", "PoisonWand1H", "Axe2H", "Mace2H", "Sword2H", "Spear2H", "Bow",
            "ElementalStaff2H", "FireStaff2H", "ColdStaff2H", "LightningStaff2H", "PoisonStaff2H",
        },
        new[] { "Shield", "Tome" },
    };

    private static readonly Lazy<IReadOnlyList<Definition>> All = new(Load);
    public static IReadOnlyList<Definition> Definitions => All.Value;

    /// <summary>Picks a definition matching the equip slot and rarity type (Normal excludes
    /// unique/set items); falls back to any matching-slot definition.</summary>
    public static Definition? Pick(int slot, RarityType rarityType, CombatRandom rng)
    {
        var all = All.Value;
        bool Matches(Definition d) => rarityType switch
        {
            RarityType.Unique => d.IsUnique,
            RarityType.Set => d.SetId.HasValue,
            _ => !d.IsUnique && !d.SetId.HasValue,
        };
        var candidates = all.Where(Matches).ToList();
        if (slot >= 0 && slot < SlotTypes.Length)
        {
            var bySlot = candidates.Where(d => SlotTypes[slot].Contains(d.Type)).ToList();
            if (bySlot.Count > 0) return bySlot[(int)(rng.NextDouble() * bySlot.Count) % bySlot.Count];
        }
        return candidates.Count > 0 ? candidates[(int)(rng.NextDouble() * candidates.Count) % candidates.Count] : null;
    }

    private static IReadOnlyList<Definition> Load()
    {
        var assembly = Assembly.GetExecutingAssembly();
        var resource = assembly.GetManifestResourceNames()
            .First(n => n.EndsWith("GameData.item_catalog.json", StringComparison.Ordinal));
        using var stream = assembly.GetManifestResourceStream(resource)!;
        var raw = JsonSerializer.Deserialize<Dictionary<string, RawDefinition>>(stream,
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true })!;
        return raw.Select(kv => new Definition(kv.Key, kv.Value.IntegerId, kv.Value.Type,
            kv.Value.IsUnique, kv.Value.SetId, kv.Value.Implicit.Select(i => new Implicit(i.Key,
                i.Value.Default is { Length: 2 } d ? new Range(d[0], d[1]) : null,
                i.Value.ByRarity.Select(r => (int.Parse(r.Key), new Range(r.Value[0], r.Value[1]))).ToList()
            )).ToList())).ToList();
    }

    private static Stream ReadResource(string suffix)
    {
        var assembly = Assembly.GetExecutingAssembly();
        var resource = assembly.GetManifestResourceNames()
            .First(n => n.EndsWith(suffix, StringComparison.Ordinal));
        return assembly.GetManifestResourceStream(resource)!;
    }

    private sealed record RawImplicit(double[] Default, Dictionary<string, double[]> ByRarity);
    private sealed record RawDefinition(int IntegerId, string Type, bool IsUnique, int? SetId, Dictionary<string, RawImplicit> Implicit);
}
