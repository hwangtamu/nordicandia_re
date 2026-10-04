using System.Reflection;
using System.Text.Json;
using Game;
using SharedNet.Constants.Game;

namespace Nordicandia.Server.WebApi;

/// <summary>
/// The web merchant's catalog (a subset of the client's potion merchant). Products can be bought
/// with silver or opals, and (for the barter UI) traded for offered items.
/// </summary>
public static class MerchantCatalog
{
    public readonly record struct ProductView(Guid ItemId, string Name, int DefinitionIntegerId, int SilverPrice, int OpalPrice);

    private sealed record OfflinePrice(string Name, string Currency, int Price);
    private static readonly Lazy<List<OfflinePrice>> OfflinePrices = new(() =>
    {
        var assembly = Assembly.GetExecutingAssembly();
        using var stream = assembly.GetManifestResourceStream(assembly.GetManifestResourceNames()
            .Single(n => n.EndsWith("GameData.offline_merchant_prices.json", StringComparison.Ordinal)))!;
        return JsonSerializer.Deserialize<List<OfflinePrice>>(stream,
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true })!;
    });
    private static int Price(string name, string currency)
        => OfflinePrices.Value.Single(p => p.Name == name && p.Currency == currency).Price;

    public static IReadOnlyList<ProductView> Products { get; } = new List<ProductView>
    {
        new(Guid.Parse("4bf73588-1dc8-4059-aed2-4e84c7046606"), "GreatElixirOfKnowledge", 606, Price("GreatElixirOfKnowledge", "SL"), Price("GreatElixirOfKnowledge", "OP")),
        new(Guid.Parse("76a881ea-12f3-4105-93f0-c31df5f5e609"), "GreatElixirOfQuantity", 616, Price("GreatElixirOfQuantity", "SL"), Price("GreatElixirOfQuantity", "OP")),
        new(Guid.Parse("e43b59b0-60e9-4030-ab60-5a9c449d9f10"), "GreatElixirOfImmortality", 610, Price("GreatElixirOfImmortality", "SL"), Price("GreatElixirOfImmortality", "OP")),
        new(Guid.Parse("171ff678-ee17-4bbd-abd1-1b9ddf59e72f"), "GreatElixirOfDreams", 609, Price("GreatElixirOfDreams", "SL"), Price("GreatElixirOfDreams", "OP")),
        new(Guid.Parse("e10d6f84-8284-44d6-904a-ae5a8452a858"), "GreatElixirOfInsomnia", 614, Price("GreatElixirOfInsomnia", "SL"), Price("GreatElixirOfInsomnia", "OP")),
        // Pet-growth / revive potions (Items.json 628..634), priced from the same offline
        // merchant catalog. Selling them is faithful; their pet effect is P03/P04.
        new(Guid.Parse("0f1594a3-35b4-4e71-b3dd-406ec8be4458"), "GrowthElixir", 628, Price("GrowthElixir", "SL"), Price("GrowthElixir", "OP")),
        new(Guid.Parse("777b858a-6ed0-408d-9c3c-29434156d21d"), "PotentGrowthElixir", 629, Price("PotentGrowthElixir", "SL"), Price("PotentGrowthElixir", "OP")),
        new(Guid.Parse("ea2b5e81-e19c-4305-bc35-9aea7ad65ade"), "RapidGrowthElixir", 630, Price("RapidGrowthElixir", "SL"), Price("RapidGrowthElixir", "OP")),
        new(Guid.Parse("dc922e58-2b67-476a-87e7-b100ad25b7dc"), "RareGrowthElixir", 631, Price("RareGrowthElixir", "SL"), Price("RareGrowthElixir", "OP")),
        new(Guid.Parse("e3d1bef2-f47c-4c0d-b7db-544100ce5b5b"), "MythicalGrowthElixir", 632, Price("MythicalGrowthElixir", "SL"), Price("MythicalGrowthElixir", "OP")),
        new(Guid.Parse("5ff95c9f-57ba-4099-9baf-b6008cf201eb"), "LegendaryGrowthElixir", 633, Price("LegendaryGrowthElixir", "SL"), Price("LegendaryGrowthElixir", "OP")),
        new(Guid.Parse("500b4ff0-3131-477c-9f1f-eb18bdeadf9c"), "FamiliarReviveElixir", 634, Price("FamiliarReviveElixir", "SL"), Price("FamiliarReviveElixir", "OP")),
        // Provisional web offer: Niflheim is absent from the recovered offline merchant catalog.
        // Consumable Niflheim portal (Items.json IntegerId 159); using it starts a Niflheim run.
        new(Guid.Parse("15900000-0000-0000-0000-000000000159"), "NiflheimPortal", 159, 50_000, 30),
    };

    public static ProductView? Find(Guid itemId)
    {
        foreach (var product in Products)
            if (product.ItemId == itemId) return product;
        return null;
    }

    /// <summary>Builds the purchasable item for a product (name + client definition id). Portal
    /// items carry the recovered <c>NumMonsterPacks</c> attribute (id 133); rarity F uses the
    /// default 11..20 range (gamedata_decrypted/Affixes.json NumMonsterPacks).</summary>
    public static SerializedItem CreateItem(ProductView product)
    {
        var item = new SerializedItem
        {
            Id = Guid.NewGuid(),
            Name = product.Name,
            Slot = ItemSlotTypes.Inventory,
            DefinitionIntegerId = product.DefinitionIntegerId,
            BaseRarity = Rarity.F,
            Location = new SerializedItemInventoryLocation { Page = 1, Row = 0, Column = 0 },
        };
        if (product.DefinitionIntegerId == NiflheimPortalDefinitionIntegerId)
            SetItemAttribute(item, NumMonsterPacksAttributeId, Random.Shared.Next(11, 21));
        return item;
    }

    /// <summary>Items.json: NiflheimPortal. Affixes.json NumMonsterPacks (IntegerId 747)
    /// targets attribute id 133.</summary>
    public const int NiflheimPortalDefinitionIntegerId = 159;
    public const int NumMonsterPacksAttributeId = 133;

    public static void SetItemAttribute(SerializedItem item, int attributeId, double value)
    {
        item.Attributes ??= new SerializedAttributes { Values = new(), MultiplicativeValues = new() };
        item.Attributes.Values ??= new();
        if (!item.Attributes.Values.TryGetValue(AttributeOrigin.Item, out var map) || map == null)
            item.Attributes.Values[AttributeOrigin.Item] = map = new Dictionary<int, GameAttributeValue>();
        map[attributeId] = new GameAttributeValue { Value = (int)value, ValueD = value };
    }
}
