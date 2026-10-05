namespace Nordicandia.Simulation;

/// <summary>MonsterManager.SpawnMonster, Android 1.9.3 @0x02A810AC.
/// Sequential, short-circuit rolls: Unique=4, Rare=2, Champion=1, Normal=0.
/// Multipliers modify reciprocal denominators 200/100/50; they are not weights.</summary>
public static class MonsterSpawnRules
{
    public static int RollRarity(double level, int tier, Func<double> nextDouble,
        double champSpawnMult = 1, double rareSpawnMult = 1, double uniqueSpawnMult = 1,
        bool normalOnly = false, int? forceRarity = null,
        bool lastLevelAndFirstBossFight = false, bool canRollBoss = false)
    {
        // Native initializes rarity from forceRarity before bypassing automatic rolls.
        if (forceRarity.HasValue) return forceRarity.Value;
        if (normalOnly) return 0;
        if (lastLevelAndFirstBossFight && canRollBoss) return 6;
        bool Chance(double multiplier, double denominator)
        {
            var inverse = multiplier == 0 ? 0 : denominator / multiplier;
            var chance = inverse == 0 ? 0 : 1 / inverse;
            return chance > 0 && nextDouble() <= chance;
        }
        if ((level >= 50 || tier >= 2) && Chance(uniqueSpawnMult, 200)) return 4;
        if ((level >= 25 || tier >= 2) && Chance(rareSpawnMult, 100)) return 2;
        if ((level >= 10 || tier >= 2) && Chance(champSpawnMult, 50)) return 1;
        return 0;
    }

    /// <summary>Roll the world's monster type first, then select a definition supporting the
    /// rolled rarity within that type. No eligible definition means no spawn, as in native.
    /// Profiles carry equal within-type weights until that separate path is fully recovered.</summary>
    public static MonsterProfile? SelectProfile(IReadOnlyList<MonsterProfile> profiles, int rarity, CombatRandom rng)
    {
        var groups = profiles.GroupBy(p => p.SpawnGroup ?? p.Name).ToList();
        var total = groups.Sum(g => g.Sum(p => Math.Max(0, p.SpawnWeight)));
        if (total <= 0) return null;
        var roll = rng.NextDouble() * total;
        var selected = groups[^1];
        foreach (var group in groups)
        {
            roll -= group.Sum(p => Math.Max(0, p.SpawnWeight));
            if (roll < 0) { selected = group; break; }
        }
        var eligible = selected.Where(p => p.AvailableRarities?.Contains(rarity) == true).ToArray();
        if (eligible.Length == 0) return null;
        return eligible[(int)(rng.NextDouble() * eligible.Length)] with
        {
            Rarity = rarity, Champion = rarity == 1,
        };
    }
}
