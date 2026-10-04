using Nordicandia.Server.WebApi;
using Nordicandia.Simulation;
using SharedNet.Constants.Game;

/// <summary>
/// E04: the essence catalog (Items.json EssenceAffixId) and the disassemble filter.
/// </summary>
static class E04EssenceTests
{
    public static void Run()
    {
        void Check(bool ok, string message)
        {
            if (!ok) throw new Exception(message);
            Console.WriteLine("PASS " + message);
        }

        Check(EssenceCatalog.Count == 60, $"E04: the essence catalog loads ({EssenceCatalog.Count}, expected 60)");

        // The item-domain affix ArmorPercent (IntegerId 30) maps to its essence.
        Check(EssenceCatalog.ForAffixDefinition(30) is { Name: "ArmorPercent" },
            "E04: the ArmorPercent affix maps to its essence item");
        // BaseWeaponFireDamage is a prefix that also has an essence.
        var fire = AffixCatalog.Entries.First(e => e.Name == "BaseWeaponFireDamage");
        Check(EssenceCatalog.ForAffixDefinition(fire.IntegerId) is { Name: "BaseWeaponFireDamage" },
            "E04: a weapon damage prefix maps to its essence");
        Check(EssenceCatalog.ForAffixDefinition(-1) is null,
            "E04: an unknown affix has no essence");

        // Equipment durability: Durability_Total (30) >= 1 so min(1.0, total) disassembles.
        var weapon = LootTable.CreateItem(new LootDrop(12, 5, 10, false, 7));
        var itemMap = weapon.Attributes.Values[SharedNet.Constants.Game.AttributeOrigin.Item];
        Check(itemMap.TryGetValue(30, out var durability) && durability.ValueD >= 1
            && Math.Min(1.0, durability.ValueD) == 1.0,
            $"E04: equipment carries durability ({durability.ValueD})");

        // ExtractAffixEssence: a disassembled essence carries the affix at the target rarity.
        var essenceItem = EssenceCatalog.CreateItem(EssenceCatalog.ForAffixDefinition(30)!.Value, 7, 30, 1);
        Check(essenceItem.Affixes.Count == 1 && essenceItem.Affixes[0].DefinitionIntegerId == 30
            && (int)essenceItem.Affixes[0].Rarity == 7 && (int)essenceItem.BaseRarity == 7,
            "E04: a disassembled essence carries its affix at the target rarity");

        // E04: GetCraftingCost (Titansteel) rarity factors and formula.
        Check(CraftingCostCatalog.RarityFactor(10) == 92.2 && CraftingCostCatalog.RarityFactor(11) == 300.0
            && CraftingCostCatalog.RarityFactor(4) == 1.55,
            "E04: GetCraftingCost rarity factors match the recovered switch");
        var expected = (int)(1 * CraftingCostCatalog.BaseMultiplier * 92.2
            * (1 + CraftingCostCatalog.ProbabilityScale / Math.Max(CraftingCostCatalog.ProbabilityFloor, 0.05)));
        Check(CraftingCostCatalog.Evaluate(1, 10, 0.05, uniqueOrSet: false) == expected,
            $"E04: GetCraftingCost formula (rarity 10, prob 0.05 -> {expected})");
        // rarity 4 (1.55) x base 1.24 x 25 (prob 0) x 1.55 (unique/set) = 74.4775 -> 74.
        Check(CraftingCostCatalog.Evaluate(1, 4, 0, uniqueOrSet: true) == 74,
            "E04: GetCraftingCost handles probability 0 and unique/set targets");
    }
}
