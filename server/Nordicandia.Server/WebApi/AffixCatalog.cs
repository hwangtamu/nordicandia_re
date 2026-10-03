using System.Reflection;
using System.Text.Json;
using Nordicandia.Simulation;

namespace Nordicandia.Server.WebApi;

/// <summary>
/// Full client item-affix catalog (E01) from <c>gamedata_decrypted/ItemAffixes.json</c>, extracted
/// by <c>tools/web-content/export_item_affix_catalog.py</c> and embedded as
/// <c>GameData/affix_catalog.json</c>. Every random Prefix/Suffix item affix is present with its
/// generated attributes (client attribute id/name and value range per item rarity) and its
/// per-tag weights, so loot generation uses the real catalog rather than a curated subset.
/// </summary>
public static class AffixCatalog
{
    public readonly record struct Range(int? Rarity, double Min, double Max, int? ValueType);
    /// <summary>One attribute an affix grants (some affixes grant several, e.g. fire min + delta).</summary>
    public readonly record struct AffixAttribute(int AttributeId, string AttributeName, IReadOnlyList<Range> Ranges);
    /// <summary>Per-item-tag weighting from ItemAffixDefinition.TagData.</summary>
    public readonly record struct AffixTag(string? Tag, string Guid, double? Weight, double ValueMultiplier);
    public readonly record struct Affix(string Name, int GenerationType, int Domain, string? Group, string? Guid,
        int IntegerId, IReadOnlyList<AffixAttribute> Attributes, IReadOnlyList<AffixTag> Tags,
        IReadOnlyList<string> EligibleTypes)
    {
        /// <summary>E01: whether this affix can roll on an item of the given type (ItemTypes.json
        /// TagIds/AffixIds with parent inheritance). An empty list means no restriction is known.</summary>
        public bool EligibleFor(string? itemType)
            => EligibleTypes.Count == 0 || itemType is null || EligibleTypes.Contains(itemType);
        /// <summary>Client AffixType: Prefix=0, Suffix=1, Implicit=2, Set=3, Unique=4.</summary>
        public bool IsPrefixOrSuffix => IsRandomAffixType(GenerationType);
        /// <summary>Primary attribute (the first), kept for single-attribute callers.</summary>
        public int AttributeId => Attributes.Count > 0 ? Attributes[0].AttributeId : 0;
        public string AttributeName => Attributes.Count > 0 ? Attributes[0].AttributeName : "Affix";
        public IReadOnlyList<Range> Ranges => Attributes.Count > 0 ? Attributes[0].Ranges : Array.Empty<Range>();
    }

    /// <summary>Client AffixDomain: Item=0. Area (2) and Monster (4) affixes are not equipment.</summary>
    public const int DomainItem = 0;

    private static readonly Lazy<IReadOnlyList<Affix>> All = new(Load);
    public static IReadOnlyList<Affix> Entries => All.Value;

    /// <summary>ItemAffixDefinition.IsPrefixOrSuffix uses an unsigned comparison against 2
    /// (ARM64 0x02CECFA8), rejecting Undefined=-1 as well as implicit/set/unique.</summary>
    public static bool IsRandomAffixType(int generationType) => generationType is 0 or 1;

    /// <summary>Rolls a value for one attribute at the item's rarity: uses the highest range whose
    /// rarity threshold does not exceed the item rarity.</summary>
    public static double Roll(AffixAttribute attribute, int itemRarity, CombatRandom rng)
    {
        var ranges = attribute.Ranges;
        if (ranges.Count == 0) return 0.0;
        var range = ranges[0];
        foreach (var r in ranges)
            if ((r.Rarity ?? 0) <= itemRarity) range = r;
        return range.Min + rng.NextDouble() * (range.Max - range.Min);
    }

    /// <summary>Rolls every attribute the affix grants.</summary>
    public static List<(AffixAttribute Attribute, double Value)> RollAll(Affix affix, int itemRarity, CombatRandom rng)
    {
        var result = new List<(AffixAttribute, double)>(affix.Attributes.Count);
        foreach (var attribute in affix.Attributes)
            result.Add((attribute, Roll(attribute, itemRarity, rng)));
        return result;
    }

    private static IReadOnlyList<Affix> Load()
    {
        var assembly = Assembly.GetExecutingAssembly();
        var resource = assembly.GetManifestResourceNames()
            .First(n => n.EndsWith("GameData.affix_catalog.json", StringComparison.Ordinal));
        using var stream = assembly.GetManifestResourceStream(resource)!;
        var raw = JsonSerializer.Deserialize<Dictionary<string, Raw>>(stream,
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true })!;
        return raw.Select(kv => new Affix(
            kv.Key,
            kv.Value.GenerationType,
            kv.Value.Domain,
            kv.Value.Group,
            kv.Value.Guid,
            kv.Value.IntegerId,
            (kv.Value.Attributes ?? new()).Select(a => new AffixAttribute(a.AttributeId, a.AttributeName,
                (a.Ranges ?? new()).Select(r => new Range(r.Rarity, r.Min, r.Max, r.ValueType)).ToList())).ToList(),
            (kv.Value.Tags ?? new()).Select(t => new AffixTag(t.Tag, t.Guid, t.Weight, t.ValueMultiplier)).ToList(),
            kv.Value.EligibleTypes ?? new()
        )).ToList();
    }

    /// <summary>The catalog affix that grants <paramref name="attributeId"/>, or null.</summary>
    public static Affix? ByAttribute(int attributeId)
    {
        foreach (var affix in Entries)
            foreach (var a in affix.Attributes)
                if (a.AttributeId == attributeId) return affix;
        return null;
    }

    private sealed record Raw(int GenerationType, int Domain, string? Group, string? Guid, int IntegerId, List<RawAttribute>? Attributes, List<RawTag>? Tags, List<string>? EligibleTypes);
    private sealed record RawAttribute(int AttributeId, string AttributeName, List<RawRange>? Ranges);
    private sealed record RawRange(int? Rarity, double Min, double Max, int? ValueType);
    private sealed record RawTag(string? Tag, string Guid, double? Weight, double ValueMultiplier);
}
