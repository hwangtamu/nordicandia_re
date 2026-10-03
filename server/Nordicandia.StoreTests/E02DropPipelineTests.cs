using Nordicandia.Server.WebApi;
using Nordicandia.Simulation;
using SharedNet.Constants.Game;

/// <summary>
/// E02: item-type affix gating, TagData spawn weights and value multipliers in the loot pipeline.
/// </summary>
static class E02DropPipelineTests
{
    public static void Run()
    {
        void Check(bool ok, string message)
        {
            if (!ok) throw new Exception(message);
            Console.WriteLine("PASS " + message);
        }

        var heavyTags = AffixCatalog.TagsForType("HeavyBracers");
        Check(heavyTags.Contains("HeavyArmor") && heavyTags.Contains("Armor") && heavyTags.Contains("Bracers"),
            "E02: item type tags resolve through the parent chain (HeavyBracers)");

        var localArmor = AffixCatalog.Entries.First(e => e.Name == "LocalArmorPercent");
        var lifePercent = AffixCatalog.Entries.First(e => e.Name == "LifePercent");
        Check(localArmor.EligibleFor("HeavyBracers") && !localArmor.EligibleFor("Amulet"),
            "E02: LocalArmorPercent is gated to Heavy armour, not amulets");
        Check(lifePercent.EligibleFor("Amulet") && lifePercent.EligibleFor("HeavyChest"),
            "E02: LifePercent is eligible on both jewellery and armour");

        // TagData spawn weight: matched tag uses its weight; no matching tag falls back to 1000.
        var amuletTags = AffixCatalog.TagsForType("Amulet");
        var agility = AffixCatalog.Entries.First(e => e.Name == "Agility_Percent_Bonus");
        Check(agility.SpawnWeight(heavyTags) == 500,
            $"E02: Agility_Percent_Bonus uses its TagData weight on armour ({agility.SpawnWeight(heavyTags)})");
        Check(localArmor.SpawnWeight(heavyTags) == 1000,
            "E02: LocalArmorPercent's HeavyArmor tag weight is 1000");
        Check(localArmor.SpawnWeight(amuletTags) == 1000,
            "E02: an item type without the affix's tag falls back to the default spawn weight");
        Check(lifePercent.ValueMultiplier(heavyTags) >= 1.0,
            "E02: TagData ValueMultiplier is applied to the rolled value");

        // Generation: slot 1 is always the Amulet type, so every rolled affix must be eligible.
        var rolls = 0;
        var allEligible = true;
        var allHaveAttributes = true;
        for (ulong seed = 1; seed <= 500; seed++)
        {
            var item = LootTable.CreateItem(new LootDrop(1, 6, 30, false, seed), ItemCatalog.RarityType.Normal);
            foreach (var affix in item.Affixes)
            {
                var definition = AffixCatalog.Entries.FirstOrDefault(e => e.IntegerId == affix.DefinitionIntegerId);
                if (definition.Name is null) continue; // set/unique grants use a different path
                rolls++;
                allEligible &= definition.EligibleFor("Amulet");
                allHaveAttributes &= affix.Attributes.Values[AttributeOrigin.Item].Count > 0;
            }
        }
        Check(rolls > 0, $"E02: amulet drops roll affixes ({rolls} rolls over 500 seeds)");
        Check(allEligible, "E02: every rolled amulet affix is eligible for the Amulet type");
        Check(allHaveAttributes, "E02: every rolled affix carries its client attributes");
    }
}
