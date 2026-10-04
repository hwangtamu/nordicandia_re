using Game;
using SharedNet.Constants.Game;

namespace Nordicandia.Server.WebApi;

/// <summary>Android CraftingUtils 0x02C8364C / 0x02C84350. Produces an immutable plan;
/// the caller applies it only when Output > 0. The native Max(1, output) is a UI reagent
/// hint and must never be used as the actual output count.</summary>
public static class SmeltingRules
{
    public sealed record Plan(int DefinitionId, string Name, int Output, IReadOnlyDictionary<Guid, int> Consumed);
    public static double? EssenceValue(int rarity) => rarity switch
    {
        2 => .05, 3 => .1, 4 => .15, 5 => .2, 6 => .25, 7 => .5,
        8 => 1, 9 => 3, 10 => 10, 11 => 50, _ => null,
    };
    public static int Stacks(SerializedItem item)
    {
        if (item.Attributes?.Values?.TryGetValue(AttributeOrigin.Item, out var map) == true
            && map != null && map.TryGetValue(19, out var value))
            return Math.Max(0, (int)value.ValueD);
        return 1;
    }
    public static int HighestRarity(SerializedItem item) => Math.Max((int)item.BaseRarity,
        item.Affixes?.Select(a => (int)a.Rarity).DefaultIfEmpty(0).Max() ?? 0);

    public static Plan Steel(IReadOnlyList<SerializedItem> materials)
    {
        var essences = materials.Where(i => EssenceCatalog.IsEssence(i.DefinitionIntegerId) && Stacks(i) > 0)
            .OrderBy(HighestRarity).ToList();
        var iron = materials.Where(i => i.DefinitionIntegerId == 63).ToList();
        // Native sums one coefficient per essence instance, not its stack count.
        var count = (int)Math.Min(iron.Sum(i => (long)Stacks(i)) / 300,
            Math.Floor(essences.Sum(i => EssenceValue(HighestRarity(i)) ?? 0)));
        var used = new Dictionary<Guid, int>();
        if (count <= 0) return new(589, "Steel", 0, used);
        Take(iron, count * 300, used);
        double remaining = count;
        foreach (var essence in essences)
        {
            if (remaining <= 0) break;
            if (EssenceValue(HighestRarity(essence)) is not { } value) continue;
            used[essence.Id] = 1;
            remaining -= value;
        }
        // 0x02C83FC8: when the last essence overshoots, return smaller essences from
        // the ascending list while their value fits the surplus (native epsilon 1e-5).
        var surplus = -remaining;
        foreach (var essence in essences)
        {
            if (surplus <= 0) break;
            if (EssenceValue(HighestRarity(essence)) is not { } value) continue;
            if (value > surplus + .00001) break;
            used.Remove(essence.Id);
            surplus -= value;
        }
        return new(589, "Steel", count, used);
    }

    public static Plan Titansteel(IReadOnlyList<SerializedItem> materials)
    {
        var ore = materials.Where(i => i.DefinitionIntegerId == 594).ToList();
        var steel = materials.Where(i => i.DefinitionIntegerId == 589).ToList();
        var count = (int)Math.Min(ore.Sum(i => (long)Stacks(i)) / 10, steel.Sum(i => (long)Stacks(i)) / 100);
        var used = new Dictionary<Guid, int>();
        if (count <= 0) return new(592, "TitanSteel", 0, used);
        Take(ore, count * 10, used);
        Take(steel, count * 100, used);
        return new(592, "TitanSteel", count, used);
    }

    private static void Take(IEnumerable<SerializedItem> stacks, int amount, Dictionary<Guid, int> used)
    {
        foreach (var item in stacks)
        {
            var take = Math.Min(amount, Stacks(item));
            if (take > 0) used[item.Id] = take;
            amount -= take;
            if (amount <= 0) break;
        }
    }
}
