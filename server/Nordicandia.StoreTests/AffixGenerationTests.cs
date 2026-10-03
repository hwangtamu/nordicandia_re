using Nordicandia.Server.WebApi;
using Nordicandia.Simulation;
using SharedNet.Constants.Game;

static class AffixGenerationTests
{
    public static void Run()
    {
        void Check(bool ok, string message)
        {
            if (!ok) throw new Exception(message);
            Console.WriteLine("PASS " + message);
        }
        Check(LootTable.NumAffixesWeights.SequenceEqual(new (int, int)[]
            { (0,7000), (1,1400), (2,1400), (3,600), (4,600), (5,300), (6,300) }),
            "affix count: embedded weights match client Droprates.NumAffixesRatio");
        Check(AffixCatalog.IsRandomAffixType(0) && AffixCatalog.IsRandomAffixType(1) &&
            new[] { -1, 2, 3, 4 }.All(t => !AffixCatalog.IsRandomAffixType(t)),
            "affix types: only client Prefix=0 and Suffix=1 are random affixes");
        var histogram = new int[7];
        for (ulong seed = 1; seed <= 20000; seed++)
        {
            var expected = LootTable.RollAffixCount(new CombatRandom(seed ^ 0xA24BAED4963EE407UL));
            var item = LootTable.CreateItem(new LootDrop(12, 3, 10, false, seed), ItemCatalog.RarityType.Normal);
            if (item.Affixes.Count != expected ||
                item.Affixes.Select(a => (a.DefinitionIntegerId,
                    a.Attributes.Values[AttributeOrigin.Item][LootTable.AttrAffixType].ValueD)).Distinct().Count() != expected)
                throw new Exception($"affix count: seed {seed} requested {expected}, got {item.Affixes.Count} or duplicates");
            if (item.Affixes.Any(a => a.Attributes.Values[AttributeOrigin.Item][LootTable.AttrAffixType].ValueD is not (0 or 1)))
                throw new Exception($"affix types: seed {seed} rolled an implicit, set or unique affix");
            histogram[item.Affixes.Count]++;
        }
        Check(histogram.All(n => n > 0), "affix count: all 0–6 outcomes exercised without lost slots or duplicates (20,000 seeds)");
        // Golden values re-derived after switching item-type selection to the client's loot-table
        // weights (E02, via the ItemTypes parent chain). The affix stream must not perturb
        // definition or base rolls.
        var fixtures = new[] { (1UL,42,12.0909,11.4575), (2UL,300,13.8253,5.9826), (3UL,375,9.6811,1.8589) };
        foreach (var (seed, definition, min, delta) in fixtures)
        {
            var item = LootTable.CreateItem(new LootDrop(12, 3, 10, false, seed), ItemCatalog.RarityType.Normal);
            var attributes = item.Attributes.Values[AttributeOrigin.Item];
            Check(item.DefinitionIntegerId == definition && Math.Abs(attributes[2500].ValueD - min) < 1e-8 &&
                Math.Abs(attributes[2501].ValueD - delta) < 1e-8,
                $"affix count: base item RNG is unchanged for seed {seed}");
        }
    }
}
