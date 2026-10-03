using System.Reflection;
using System.Text.Json;
using Nordicandia.Simulation;

namespace Nordicandia.Server.WebApi;

/// <summary>
/// Curated real client affixes (from gamedata_decrypted/Affixes.json), extracted to
/// tools/web-content/generated/affix_catalog.json and embedded. Each entry maps an affix to
/// the client attribute it grants and the value range per item rarity, so generated items carry
/// the client's attribute ids and feed the recovered attribute engine.
/// </summary>
public static class AffixCatalog
{
    public readonly record struct Range(int? Rarity, double Min, double Max);
    public readonly record struct Affix(string Name, int AttributeId, string AttributeName, int GenerationType, IReadOnlyList<Range> Ranges)
    {
        /// <summary>Client AffixType: Prefix=0, Suffix=1, Implicit=2, Set=3, Unique=4.</summary>
        public bool IsPrefixOrSuffix => IsRandomAffixType(GenerationType);
    }

    private static readonly Lazy<IReadOnlyList<Affix>> All = new(Load);
    public static IReadOnlyList<Affix> Entries => All.Value;

    /// <summary>ItemAffixDefinition.IsPrefixOrSuffix uses an unsigned comparison against 2
    /// (ARM64 0x02CECFA8), rejecting Undefined=-1 as well as implicit/set/unique.</summary>
    public static bool IsRandomAffixType(int generationType) => generationType is 0 or 1;

    /// <summary>Rolls a value for the affix at the item's rarity: uses the highest range whose
    /// rarity threshold does not exceed the item rarity.</summary>
    public static double Roll(Affix affix, int itemRarity, CombatRandom rng)
    {
        var range = affix.Ranges[0];
        foreach (var r in affix.Ranges)
            if ((r.Rarity ?? 0) <= itemRarity) range = r;
        return range.Min + rng.NextDouble() * (range.Max - range.Min);
    }

    private static IReadOnlyList<Affix> Load()
    {
        var assembly = Assembly.GetExecutingAssembly();
        var resource = assembly.GetManifestResourceNames()
            .First(n => n.EndsWith("GameData.affix_catalog.json", StringComparison.Ordinal));
        using var stream = assembly.GetManifestResourceStream(resource)!;
        var raw = JsonSerializer.Deserialize<Dictionary<string, Raw>>(stream,
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true })!;
        return raw.Select(kv => new Affix(kv.Key, kv.Value.AttributeId, kv.Value.AttributeName, kv.Value.GenerationType,
            kv.Value.Ranges.Select(r => new Range(r.Rarity, r.Min, r.Max)).ToList())).ToList();
    }

    /// <summary>The catalog affix that grants <paramref name="attributeId"/>, or null.</summary>
    public static Affix? ByAttribute(int attributeId)
    {
        foreach (var affix in Entries)
            if (affix.AttributeId == attributeId) return affix;
        return null;
    }

    private sealed record Raw(int AttributeId, string AttributeName, int GenerationType, List<RawRange> Ranges);
    private sealed record RawRange(int? Rarity, double Min, double Max);
}
