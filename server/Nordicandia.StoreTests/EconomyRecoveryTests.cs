using System.Reflection;
using Game;
using Nordicandia.Server.State;
using Nordicandia.Server.WebApi;
using Nordicandia.Simulation;
using SharedNet.Api;
using SharedNet.Constants.Game;

static class EconomyRecoveryTests
{
    private static void Check(bool ok, string message)
    {
        if (!ok) throw new Exception(message);
        Console.WriteLine("PASS " + message);
    }

    public static void Run()
    {
        // Independent decimal evaluation of the reviewed ARM64 branches (see recovery doc).
        var expected = new[] { 10000d/11, 1100000d/11, 110000d,
            27590.16393442623, 11500, 4788.461538461538, 1450,
            484.6153846153846, 157.6470588235294, 29.148936170212766, 6.529411764705882 };
        var weights = AffixRarityCatalog.Weights(10);
        Check(weights.All(kv => Math.Abs(kv.Value - expected[kv.Key]) < 1e-8),
            "E02 recovery: all F-S MF=10 native weight branches match independent decimal goldens");
        Check(AffixRarityCatalog.Weights().All(kv => kv.Value == AffixRarityCatalog.BaseWeights[kv.Key]),
            "E02 recovery: zero MF preserves every base rarity weight");
        Check(AffixRarityCatalog.Weights(10, 9.999)[2] == 110000 && AffixRarityCatalog.Weights(10, 10)[2] == 20000,
            "E02 recovery: D factor threshold is exactly 10, not applied to F/E");
        Check(Math.Abs(AffixRarityCatalog.Weights(10, 10)[10] - 9.122448979591837) < 1e-12,
            "E02 recovery: S uses divisor 4.5 and slope 0.6");
        var previous = AffixRarityCatalog.Weights(0, 1, 4, 3);
        Check(Math.Abs(previous[0] - 1346.8013468013468) < 1e-9 && previous[3] == 3720 && previous[4] == 1980,
            "E02 recovery: highest-rarity bias includes 2.25 distance penalty and affix ordinal");
        Check(AffixRarityCatalog.Weights(0, 0).Values.All(double.IsFinite)
            && AffixRarityCatalog.Weights(10, 0)[3] == AffixRarityCatalog.BaseWeights[3],
            "E02 recovery: disabled saturated MF factor avoids native zero/zero");
        foreach (var mf in new[] { double.NaN, double.PositiveInfinity, -1d })
        {
            var rejected = false;
            try { AffixRarityCatalog.Weights(mf); } catch (ArgumentOutOfRangeException) { rejected = true; }
            Check(rejected, "E02 recovery: invalid MF rejected " + mf);
        }
        var histogram = new int[11];
        var rng = new CombatRandom(91237);
        const int draws = 300000;
        for (var i = 0; i < draws; i++) histogram[AffixRarityCatalog.Roll(rng, weights)]++;
        var total = weights.Values.Sum();
        foreach (var (rarity, weight) in weights)
        {
            var p = weight / total;
            var mean = draws * p;
            Check(Math.Abs(histogram[rarity] - mean) <= 6 * Math.Sqrt(mean * (1-p)) + 2,
                $"E02 recovery: seeded distribution rarity={rarity} observed={histogram[rarity]} expected={mean:F2}");
        }
        var raritySum = new long[2];
        var normalSets = 0;
        var affixesAboveItem = 0;
        for (ulong seed = 1; seed <= 1000; seed++)
            foreach (var mf in new[] { 0, 10 })
            {
                var item = LootTable.CreateItem(new LootDrop(1, 0, 1, false, seed), ItemCatalog.RarityType.Normal, mf);
                normalSets += LootTable.IsUniqueOrSet(item) ? 1 : 0;
                raritySum[mf == 0 ? 0 : 1] += item.Affixes.Sum(a => (int)a.Rarity);
                affixesAboveItem += item.Affixes.Count(a => (int)a.Rarity > (int)item.BaseRarity);
            }
        Check(raritySum[1] > raritySum[0] && affixesAboveItem > 0,
            "E02 recovery: actual loot uses MF and independent affix rarities, not base item rarity");
        for (ulong seed = 1; seed <= 250; seed++)
            normalSets += LootTable.IsUniqueOrSet(LootTable.CreateItem(new LootDrop(3, 10, 30, false, seed), ItemCatalog.RarityType.Normal)) ? 1 : 0;
        Check(normalSets == 0, "E03 recovery: normal definitions never receive invented random set membership");
        BatchedQuantity();
    }

    private static void BatchedQuantity()
    {
        var dir = Path.Combine(Path.GetTempPath(), "nord-economy-" + Guid.NewGuid());
        try
        {
            using var store = new GameStore(dir);
            var owner = store.GetOrCreateUser("device:economy-recovery").UserId;
            var data = Defaults.Create<SerializedCharacterData.SerializedData>();
            data.Attributes = new SerializedAttributes { Values = new()
            {
                [AttributeOrigin.Character] = new() { [367] = new GameAttributeValue { ValueD = .5 } }
            }, MultiplicativeValues = new() };
            var id = store.CreateCharacter(owner, new CreateCharacterRequest { DisplayName = "e-recovery",
                CharacterGameMode = GameMode.Normal, Data = new SerializedCharacterData { Data = data } }).CharacterId;
            var registry = new CombatRegistry(store);
            var instance = registry.GetOrCreate(owner, id);
            var drops = (List<LootDrop>)typeof(CombatInstance).GetField("pendingDrops", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(instance)!;
            var before = store.GetItems(owner, id).Count;
            for (ulong i = 1; i <= 2; i++) drops.Add(new LootDrop(1, 0, 1, false, i));
            registry.Advance(owner, id);
            Check(store.GetItems(owner, id).Count - before == 3,
                "E02 recovery: two drops drained together at +50% quantity grant 1+2, not 1+1");
            Check(instance.DrainDrops().Count == 0, "E02 recovery: collected drops are consumed once");
            registry.Advance(owner, id);
            Check(store.GetItems(owner, id).Count - before == 3, "E02 recovery: polling does not duplicate batched loot");
        }
        finally { if (Directory.Exists(dir)) Directory.Delete(dir, true); }
    }
}
