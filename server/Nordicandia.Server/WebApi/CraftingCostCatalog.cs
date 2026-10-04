using System.Reflection;
using System.Text.Json;

namespace Nordicandia.Server.WebApi;

/// <summary>
/// E04: the client's crafting (Titansteel) cost for adding/rerolling an affix, recovered from
/// <c>CraftingUtils.GetCraftingCost</c> (0x02C85244) by
/// tools/web-content/recover_essence_disassemble.py.
///
/// <c>cost = affixIndex * 1.24 * rarityFactor</c>, then
/// <c>*25</c> when the spawn probability is 0 else <c>*(1 + 0.043 / max(0.01, probability))</c>,
/// then <c>*1.55</c> when the target item is unique/set; truncated to an int.
/// </summary>
public static class CraftingCostCatalog
{
    private sealed record Raw(double BaseMultiplier, double ProbabilityFloor, double ProbabilityScale,
        double UniqueSetFactor, double ProbabilityZeroFactor, Dictionary<string, double> RarityFactors);

    private static readonly Lazy<Raw> Data = new(Load);

    public static double BaseMultiplier => Data.Value.BaseMultiplier;
    public static double ProbabilityFloor => Data.Value.ProbabilityFloor;
    public static double ProbabilityScale => Data.Value.ProbabilityScale;
    public static double UniqueSetFactor => Data.Value.UniqueSetFactor;
    public static double ProbabilityZeroFactor => Data.Value.ProbabilityZeroFactor;

    public static double RarityFactor(int rarity)
        => Data.Value.RarityFactors.TryGetValue(rarity.ToString(), out var factor) ? factor : 0;

    /// <summary>The recovered GetCraftingCost, truncated to the client's int return.</summary>
    public static int Evaluate(int affixIndex, int rarity, double probability, bool uniqueOrSet)
    {
        var cost = affixIndex * BaseMultiplier * RarityFactor(rarity);
        if (probability == 0)
            cost *= ProbabilityZeroFactor;
        else
            cost *= 1 + ProbabilityScale / Math.Max(ProbabilityFloor, probability);
        if (uniqueOrSet) cost *= UniqueSetFactor;
        return (int)cost;
    }

    private static Raw Load()
    {
        var assembly = Assembly.GetExecutingAssembly();
        using var stream = assembly.GetManifestResourceStream(assembly.GetManifestResourceNames()
            .First(n => n.EndsWith("GameData.crafting_cost.json", StringComparison.Ordinal)))!;
        return JsonSerializer.Deserialize<Raw>(stream,
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true })!;
    }
}
