using System.Reflection;
using System.Text.Json;
using Nordicandia.Simulation;
using SharedNet.Constants.Game;

namespace Nordicandia.Server.WebApi;

/// <summary>Android 1.9.3 ItemGenerator.InternalInitializeAffixRarityPool @ 0x02CA2468.
/// Weights are doubles, unlike the rounded Normal/Unique/Set pool. See E_RECOVERY_2026-10-03.md.</summary>
public static class AffixRarityCatalog
{
    private static readonly Lazy<IReadOnlyDictionary<int, double>> Base = new(() =>
    {
        var assembly = Assembly.GetExecutingAssembly();
        using var stream = assembly.GetManifestResourceStream(assembly.GetManifestResourceNames()
            .Single(n => n.EndsWith("GameData.affix_rarity_weights.json", StringComparison.Ordinal)))!;
        var raw = JsonSerializer.Deserialize<Dictionary<string, double>>(stream)!;
        return raw.ToDictionary(kv => (int)Enum.Parse<Rarity>(kv.Key), kv => kv.Value);
    });

    public static IReadOnlyDictionary<int, double> BaseWeights => Base.Value;

    public static IReadOnlyDictionary<int, double> Weights(double magicFind = 0,
        double factor = 1, int affixNumber = 1, int? highestRarity = null)
    {
        if (!double.IsFinite(magicFind) || magicFind < 0 || magicFind > double.MaxValue / 100
            || !double.IsFinite(factor) || factor < 0 || factor > double.MaxValue / 1000
            || affixNumber < 1 || highestRarity is < 0 or > 11)
            throw new ArgumentOutOfRangeException(nameof(magicFind), "Invalid affix rarity inputs.");
        var result = new Dictionary<int, double>();
        foreach (var (rarity, baseline) in BaseWeights.OrderBy(kv => kv.Key))
        {
            double weight;
            if (rarity <= 1) weight = baseline / (1 + magicFind);
            else if (rarity == 2) weight = baseline * (1 + magicFind * (factor >= 10 ? 0.1 : 1));
            else
            {
                var (coefficient, slope, divisor) = rarity switch
                {
                    3 => (1000d, .22, 1d),
                    4 => (800d, .4, 1d),
                    5 => (700d, .6, 1d),
                    6 => (600d, .65, 1d),
                    7 => (500d, .6, 1.4),
                    8 => (400d, .6, 1.9),
                    9 => (225d, .5, 3d),
                    10 => (150d, .6, 4.5),
                    _ => (1d, 1d, 1d),
                };
                var c = coefficient * factor;
                // Continuous zero-factor/zero-MF extension avoids the native 0/0 singularity.
                var bonus = magicFind == 0 || factor == 0 ? 0
                    : .01 * (c / (slope + divisor * (c / (100 * magicFind))));
                weight = baseline * (1 + bonus);
            }
            if (highestRarity is { } highest)
            {
                weight = rarity < highest ? weight / (1 + affixNumber * .025)
                    : weight * (1 + (double)rarity * affixNumber * .02);
                if (highest >= 2 && rarity < highest)
                    weight /= Math.Max(Math.Abs(rarity - highest) * 2.25, 1);
            }
            result.Add(rarity, weight);
        }
        return result;
    }

    public static int Roll(CombatRandom rng, IReadOnlyDictionary<int, double> weights)
    {
        var total = weights.Values.Sum();
        if (!double.IsFinite(total) || total <= 0) throw new ArgumentException("Empty/invalid rarity pool.");
        var roll = rng.NextDouble() * total;
        foreach (var (rarity, weight) in weights)
        {
            roll -= weight;
            if (roll < 0) return rarity;
        }
        return weights.Last().Key;
    }
}
