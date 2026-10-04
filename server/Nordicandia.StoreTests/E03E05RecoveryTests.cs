using Game;
using Nordicandia.Server.State;
using Nordicandia.Server.WebApi;
using SharedNet.Constants.Game;
using SharedNet.Api;

static class E03E05RecoveryTests
{
    private static void Check(bool ok, string message)
    {
        if (!ok) throw new Exception(message);
        Console.WriteLine("PASS " + message);
    }
    private static SerializedItem Material(int id, int stacks) => new()
    {
        Id = Guid.NewGuid(), DefinitionIntegerId = id, Slot = ItemSlotTypes.Blacksmith_SourceItem,
        Attributes = new SerializedAttributes { Values = new() { [AttributeOrigin.Item] = new()
        { [19] = new() { Value = stacks, ValueD = stacks } } }, MultiplicativeValues = new() }
    };
    public static void Run()
    {
        var prefix = AffixCatalog.Entries.First(a => a.GenerationType == 0 && a.Domain == 0);
        var suffix = AffixCatalog.Entries.First(a => a.GenerationType == 1 && a.Domain == 0);
        SerializedAffix Affix(int id, int rarity) => new() { DefinitionIntegerId = id, Rarity = (Rarity)rarity };
        var target = new List<SerializedAffix> { OpenAffixSlots.Create(false) };
        Check(!OpenAffixSlots.Merge(target, Affix(prefix.IntegerId, 10)) && target.Count == 1,
            "E03: prefix cannot consume a suffix slot even at rarity S");
        Check(OpenAffixSlots.Merge(target, Affix(suffix.IntegerId, 3)) && !OpenAffixSlots.IsOpen(target[0]),
            "E03: filling a matching slot replaces its marker");
        Check(!OpenAffixSlots.Merge(target, Affix(suffix.IntegerId, 2)) && (int)target[0].Rarity == 3,
            "E03: merging never downgrades an existing affix");
        Check(OpenAffixSlots.Merge(target, Affix(suffix.IntegerId, 4)) && target.Count == 1,
            "E03: existing affix upgrade needs no additional slot");
        Check(!OpenAffixSlots.Merge(target, OpenAffixSlots.Create(true)), "E03: an empty marker is not a merge payload");
        Check(!OpenAffixSlots.Merge(target, Affix(-543, 10)), "E03: unknown definition cannot invent capacity");

        var essence = EssenceCatalog.CreateItem(EssenceCatalog.ForAffixDefinition(30)!.Value, 1);
        essence.BaseRarity = (Rarity)8;
        var steel = SmeltingRules.Steel(new[] { Material(63, 299), essence });
        Check(steel.Output == 0 && steel.Consumed.Count == 0, "E04: 299 Iron cannot smelt; no preview hints consumed");
        steel = SmeltingRules.Steel(new[] { Material(63, 350), essence, Material(999, 50) });
        Check(steel.Output == 1 && steel.Consumed.Values.Sum() == 301,
            "E04: one steel costs 300 Iron plus one AA essence; unrelated items preserved");
        var high = EssenceCatalog.CreateItem(EssenceCatalog.ForAffixDefinition(30)!.Value, 1);
        high.BaseRarity = (Rarity)9;
        steel = SmeltingRules.Steel(new[] { Material(63, 600), essence, high });
        Check(steel.Output == 2 && !steel.Consumed.ContainsKey(essence.Id) && steel.Consumed[high.Id] == 1,
            "E04: overshoot returns the smaller essence (1 + 3 -> need 2, only consume 3)");
        essence.BaseRarity = Rarity.E;
        essence.Affixes = new() { Affix(prefix.IntegerId, 8) };
        Check(SmeltingRules.Steel(new[] { Material(63, 300), essence }).Output == 1,
            "E04: essence value reads highest affix rarity, not only base rarity");
        Check(SmeltingRules.EssenceValue(11) == 50, "E04: SS essence coefficient 50 recovered from cctor");
        var titanium = Material(594, 23); var steelInput = Material(589, 250);
        var titan = SmeltingRules.Titansteel(new[] { titanium, steelInput });
        Check(titan.Output == 2 && titan.Consumed[titanium.Id] == 20 && titan.Consumed[steelInput.Id] == 200,
            "E04: two Titansteel consume 20 TitaniumOre and 200 Steel");
        Check(SmeltingRules.Titansteel(new[] { Material(594, 9), Material(589, 100) }).Output == 0,
            "E04: TitaniumOre shortage cannot create free Titansteel");
        StoreSmelting(titanium, steelInput);
        var expected = new Dictionary<string, (int Silver, int Opal)>
        {
            ["GreatElixirOfKnowledge"] = (1500000,150), ["GreatElixirOfQuantity"] = (1500000,150),
            ["GreatElixirOfImmortality"] = (800000,80), ["GreatElixirOfDreams"] = (2500000,250),
            ["GreatElixirOfInsomnia"] = (1500000,150),
        };
        foreach (var (name, price) in expected)
        {
            var actual = MerchantCatalog.Products.Single(p => p.Name == name);
            Check(actual.SilverPrice == price.Silver && actual.OpalPrice == price.Opal,
                "E05: runtime merchant matches native offline price " + name);
        }
    }
    private static void StoreSmelting(SerializedItem ore, SerializedItem steel)
    {
        var dir = Path.Combine(Path.GetTempPath(), "nord-smelting-" + Guid.NewGuid());
        try
        {
            Guid owner, id;
            using (var store = new GameStore(dir))
            {
                owner = store.GetOrCreateUser("device:smelting-recovery").UserId;
                id = store.CreateCharacter(owner, new CreateCharacterRequest { DisplayName = "smelt",
                    CharacterGameMode = GameMode.Normal,
                    Data = new SerializedCharacterData { Data = Defaults.Create<SerializedCharacterData.SerializedData>() } }).CharacterId;
                var unrelated = Material(999, 77);
                store.GrantItems(owner, id, new List<SerializedItem> { ore, steel, unrelated });
                var result = store.SmeltItems(owner, id);
                var items = store.GetItems(owner, id);
                Check(result.Successful && result.Result.Items.Single().DefinitionIntegerId == 592,
                    "E04: store smelt endpoint produces Titansteel");
                Check(SmeltingRules.Stacks(items.Single(i => i.Id == ore.Id)) == 3
                    && SmeltingRules.Stacks(items.Single(i => i.Id == steel.Id)) == 50
                    && SmeltingRules.Stacks(items.Single(i => i.Id == unrelated.Id)) == 77,
                    "E04: partial stacks and unrelated source survive atomic smelting");
                Check(!store.SmeltItems(owner, id).Successful, "E04: retry with insufficient remainder grants nothing");
            }
            using var reopened = new GameStore(dir);
            var saved = reopened.GetItems(owner, id);
            Check(saved.Where(i => i.DefinitionIntegerId == 592).Sum(SmeltingRules.Stacks) == 2
                && SmeltingRules.Stacks(saved.Single(i => i.Id == ore.Id)) == 3,
                "E04: smelting output and exact remainder persist across reopen");
            var target = Material(123, 1); target.Slot = ItemSlotTypes.Blacksmith_TargetItem;
            target.BaseRarity = (Rarity)10;
            target.Affixes = new() { new SerializedAffix { DefinitionIntegerId = 30, Rarity = Rarity.C } };
            var source = Material(124, 1); source.BaseRarity = (Rarity)10;
            source.Affixes = new() { new SerializedAffix { DefinitionIntegerId = 30, Rarity = Rarity.C } };
            var iron = Material(63, 100); iron.Slot = ItemSlotTypes.Inventory;
            reopened.GrantItems(owner, id, new List<SerializedItem> { target, source, iron });
            var noOp = reopened.CraftEssenceItem(owner, id, 10);
            Check(!noOp.OperationSuccessful && noOp.IronConsumed == 0
                && SmeltingRules.Stacks(reopened.GetItems(owner, id).Single(i => i.Id == iron.Id)) == 100
                && reopened.GetItems(owner, id).Any(i => i.Id == source.Id),
                "E03: an inapplicable merge leaves Iron and sources untouched");
        }
        finally { if (Directory.Exists(dir)) Directory.Delete(dir, true); }
    }
}
