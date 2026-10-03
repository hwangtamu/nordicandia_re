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

    public static IReadOnlyList<ProductView> Products { get; } = new List<ProductView>
    {
        new(Guid.Parse("4bf73588-1dc8-4059-aed2-4e84c7046606"), "GreatElixirOfKnowledge", 606, 10_000, 25),
        new(Guid.Parse("76a881ea-12f3-4105-93f0-c31df5f5e609"), "GreatElixirOfQuantity", 616, 15_000, 35),
        new(Guid.Parse("e43b59b0-60e9-4030-ab60-5a9c449d9f10"), "GreatElixirOfImmortality", 610, 25_000, 50),
        new(Guid.Parse("171ff678-ee17-4bbd-abd1-1b9ddf59e72f"), "GreatElixirOfDreams", 609, 20_000, 40),
        new(Guid.Parse("e10d6f84-8284-44d6-904a-ae5a8452a858"), "GreatElixirOfInsomnia", 614, 10_000, 25),
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
