using System.Reflection;
using System.Text.Json;

namespace Nordicandia.Server.WebApi;

/// <summary>
/// Item attribute scaling (from gamedata_decrypted/GameBalance.json ScalingFunctions), extracted
/// to tools/web-content/generated/scaling_functions.json and embedded. Matches the client's
/// ItemAttributeSpecifierDefinition.GetMinMaxValues scaling:
/// <code>factor = 1 + FinalMult * (TierMult * max(0, Pow(worldLevel/8 + worldLevel, TierExp) - 1)
///                               + Pow(LevelMult * level, LevelExp))</code>
/// </summary>
public static class ScalingCatalog
{
    public readonly record struct Scaling(double LevelMultiplier, double LevelExponent,
        double LevelThreshold, double TierMultiplier, double TierExponent, double FinalMult)
    {
        public double Factor(double level)
        {
            if (level <= 0) return 1.0;
            var levelPart = Math.Pow(Math.Max(0, LevelMultiplier * (level - LevelThreshold)), LevelExponent);
            var tierInput = level / 8.0 + level;
            var tierPart = TierExponent == 0 ? 0 : Math.Max(0, Math.Pow(tierInput, TierExponent) - 1);
            return 1.0 + FinalMult * (tierPart * TierMultiplier + levelPart);
        }
    }

    private static readonly Lazy<Dictionary<string, Scaling>> All = new(Load);

    public static Scaling? Get(string name) => All.Value.TryGetValue(name, out var s) ? s : null;

    public static double WeaponDamageFactor(double level) => Factor("WeaponFlatDamageScaling", level);
    public static double ArmorFactor(double level) => Factor("ArmorFlatArmorScaling", level);
    public static double EvasionFactor(double level) => Factor("ArmorFlatEvasionScaling", level);

    private static double Factor(string name, double level) => Get(name)?.Factor(level) ?? 1.0;

    private static Dictionary<string, Scaling> Load()
    {
        var assembly = Assembly.GetExecutingAssembly();
        var resource = assembly.GetManifestResourceNames()
            .First(n => n.EndsWith("GameData.scaling_functions.json", StringComparison.Ordinal));
        using var stream = assembly.GetManifestResourceStream(resource)!;
        var raw = JsonSerializer.Deserialize<Dictionary<string, Scaling>>(stream,
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true })!;
        return raw;
    }
}
