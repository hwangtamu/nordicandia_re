namespace Nordicandia.Server.WebApi;

/// <summary>
/// The web merchant's barter catalog (a subset of the client's potion merchant). The player
/// offers items (moved into the YourTrade slot) and receives the catalog product.
/// </summary>
public static class MerchantCatalog
{
    public readonly record struct ProductView(Guid ItemId, string Name, int DefinitionIntegerId, int SilverPrice);

    public static IReadOnlyList<ProductView> Products { get; } = new List<ProductView>
    {
        new(Guid.Parse("4bf73588-1dc8-4059-aed2-4e84c7046606"), "GreatElixirOfKnowledge", 606, 10_000),
        new(Guid.Parse("76a881ea-12f3-4105-93f0-c31df5f5e609"), "GreatElixirOfQuantity", 616, 15_000),
        new(Guid.Parse("e43b59b0-60e9-4030-ab60-5a9c449d9f10"), "GreatElixirOfImmortality", 610, 25_000),
        new(Guid.Parse("171ff678-ee17-4bbd-abd1-1b9ddf59e72f"), "GreatElixirOfDreams", 609, 20_000),
        new(Guid.Parse("e10d6f84-8284-44d6-904a-ae5a8452a858"), "GreatElixirOfInsomnia", 614, 10_000),
    };

    public static ProductView? Find(Guid itemId)
    {
        foreach (var product in Products)
            if (product.ItemId == itemId) return product;
        return null;
    }
}
