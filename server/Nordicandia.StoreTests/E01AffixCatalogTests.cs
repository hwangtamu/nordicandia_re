using Nordicandia.Server.WebApi;
using Nordicandia.Simulation;
using SharedNet.Constants.Game;

/// <summary>
/// E01: the embedded item-affix catalog is the full client random Prefix/Suffix set, every
/// referenced attribute resolves, and loot generation only draws item-domain affixes.
/// </summary>
static class E01AffixCatalogTests
{
    public static void Run()
    {
        void Check(bool ok, string message)
        {
            if (!ok) throw new Exception(message);
            Console.WriteLine("PASS " + message);
        }

        // 60 Prefix + 37 Suffix random item affixes from gamedata ItemAffixes.json.
        Check(LootTable.AffixCatalogSize == 97,
            $"E01: full client random item-affix catalog loaded ({LootTable.AffixCatalogSize}, expected 97)");
        Check(AffixCatalog.Entries.All(e => e.IsPrefixOrSuffix),
            "E01: every catalog entry is a random Prefix/Suffix affix");
        // Item-domain (0) affixes always grant attributes; 14 Monster* affixes (domain 2) are
        // code-driven monster modifiers with no attribute list, so only the item pool is checked.
        var itemAffixes = AffixCatalog.Entries.Where(e => e.Domain == AffixCatalog.DomainItem).ToList();
        Check(itemAffixes.Count == 56, $"E01: item-domain random affixes loaded ({itemAffixes.Count}, expected 56)");
        Check(itemAffixes.All(e => e.Attributes.Count > 0 && e.Attributes.All(a => !string.IsNullOrEmpty(a.AttributeName))),
            "E01: every item-domain affix grants at least one resolved attribute");
        Check(AffixCatalog.Entries.Any(e => e.Attributes.Count > 1),
            "E01: multi-attribute affixes are present (e.g. LocalBaseFireDamage min+delta)");
        Check(AffixCatalog.Entries.Any(e => e.Domain == AffixCatalog.DomainItem),
            "E01: item-domain (0) affixes are present");
        Check(AffixCatalog.Entries.Select(e => e.Name).Distinct().Count() == AffixCatalog.Entries.Count,
            "E01: affix names are unique");

        // Every attribute id resolves to a name in the embedded attribute table, and back.
        var engine = CharacterAttributeEngine.Instance;
        var unresolved = new List<int>();
        foreach (var affix in AffixCatalog.Entries)
            foreach (var attribute in affix.Attributes)
                if (!engine.TryGetName(attribute.AttributeId, out var name) || name != attribute.AttributeName)
                    unresolved.Add(attribute.AttributeId);
        Check(unresolved.Count == 0,
            $"E01: all affix attribute ids resolve to their client names ({unresolved.Count} unresolved)");

        // E01/E02: item-type eligibility from ItemTypes TagIds/AffixIds (parent-inherited).
        var localArmor = AffixCatalog.Entries.First(e => e.Name == "LocalArmorPercent");
        Check(localArmor.EligibleTypes.Count > 0 && localArmor.EligibleTypes.All(t => t.StartsWith("Heavy")),
            $"E01/E02: LocalArmorPercent only rolls on Heavy armour ({string.Join("/", localArmor.EligibleTypes)})");
        var lifePercent = AffixCatalog.Entries.First(e => e.Name == "LifePercent");
        Check(lifePercent.EligibleTypes.Contains("Amulet") && lifePercent.EligibleTypes.Contains("Chest"),
            "E01/E02: LifePercent rolls on amulets and chests");
        Check(!localArmor.EligibleFor("Amulet") && lifePercent.EligibleFor("Amulet"),
            "E01/E02: EligibleFor rejects affixes outside the item type");

        // A shared primary attribute must not be treated as a duplicate affix (E01 definition ids).
        var item = LootTable.CreateItem(new LootDrop(12, 3, 10, false, 1010UL), ItemCatalog.RarityType.Normal);
        Check(item.Affixes.Select(a => a.DefinitionIntegerId).Distinct().Count() == item.Affixes.Count,
            "E01: rolled affixes carry distinct affix-definition ids");
        Check(item.Affixes.All(a => a.Attributes.Values[AttributeOrigin.Item][LootTable.AttrAffixType].ValueD is 0 or 1),
            "E01: loot rolls only Prefix/Suffix affixes");
    }
}
