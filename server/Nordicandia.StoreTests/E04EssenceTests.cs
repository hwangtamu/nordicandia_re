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
        Check(itemMap.TryGetValue(20, out var requiredLevel) && requiredLevel.ValueD == 10,
            "E04: generated equipment persists the client RequiredLevel attribute alongside the web gate");

        // ExtractAffixEssence constructs a new Affix from the source definition at the source rarity.
        var essence = EssenceCatalog.ForAffixDefinition(30)!.Value;
        var essenceItem = EssenceCatalog.CreateItem(essence, 7, 30, 37, seed: 12345);
        var affixDefinition = AffixCatalog.Entries.Single(a => a.IntegerId == 30);
        var rolledAffix = essenceItem.Affixes.Single();
        var rolledValues = rolledAffix.Attributes.Values[AttributeOrigin.Item];
        var expectedAttributeIds = affixDefinition.Attributes.Select(a => a.AttributeId).ToHashSet();
        Check(rolledAffix.DefinitionIntegerId == 30 && (int)rolledAffix.Rarity == 7
            && rolledAffix.AffixSource == AffixSources.EssenceAdd
            && rolledValues.Keys.Where(id => id != LootTable.AttrAffixType).ToHashSet().SetEquals(expectedAttributeIds)
            && (int)essenceItem.Attributes.Values[AttributeOrigin.Item][LootTable.AttrRequiredLevel].ValueD == 37,
            "E04: disassembled essence carries a newly rolled source affix, source rarity, and required level");
        var sameSeed = EssenceCatalog.CreateItem(essence, 7, 30, 37, seed: 12345);
        Check(rolledValues.Where(kv => kv.Key != LootTable.AttrAffixType)
                .All(kv => sameSeed.Affixes[0].Attributes.Values[AttributeOrigin.Item][kv.Key].ValueD == kv.Value.ValueD),
            "E04: essence affix value rolls are deterministic for the same source seed");

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
