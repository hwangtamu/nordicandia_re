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
    public readonly record struct Affix(string Name, int AttributeId, string AttributeName, IReadOnlyList<Range> Ranges);

    private static readonly Lazy<IReadOnlyList<Affix>> All = new(Load);
    public static IReadOnlyList<Affix> Entries => All.Value;

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
        return raw.Select(kv => new Affix(kv.Key, kv.Value.AttributeId, kv.Value.AttributeName,
            kv.Value.Ranges.Select(r => new Range(r.Rarity, r.Min, r.Max)).ToList())).ToList();
    }

    private sealed record Raw(int AttributeId, string AttributeName, List<RawRange> Ranges);
    private sealed record RawRange(int? Rarity, double Min, double Max);
}
