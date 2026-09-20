using Game;
using Grpc.Core;
using MagicOnion;
using MagicOnion.Server;
using Nordicandia.Server.State;
using SharedNet.Api;
using SharedNet.Constants;
using SharedNet.Constants.Game;
using SharedNet.Dto;

namespace Nordicandia.Server.Services;

/// <summary>Elixir catalogs used by the two merchant windows in the 1.9.3 client.</summary>
public sealed class CatalogServiceApiImpl : ServiceBase<ICatalogServiceApi>, ICatalogServiceApi
{
    private const string Silver = "SL";
    private const string Opals = "OP";

    private sealed record Product(
        Guid ItemId, string Name, int DefinitionIntegerId, Guid DefinitionId,
        int SilverPrice, int OpalPrice, int StackAffixId, params (int AffixId, int AttributeId, double Value)[] Effects);

    private sealed record CatalogDefinition(Guid Id, string Currency, IReadOnlyList<Product> Products);

    private static readonly Product[] Potions =
    {
        new(Guid.Parse("4bf73588-1dc8-4059-aed2-4e84c7046606"), "GreatElixirOfKnowledge", 606,
            Guid.Parse("73525c4a-0f3a-4c1c-a6f3-c27c2e9f57bc"), 10_000, 25, 774,
            (2305, 144, 20), (2305, 145, 28_800)),
        new(Guid.Parse("76a881ea-12f3-4105-93f0-c31df5f5e609"), "GreatElixirOfQuantity", 616,
            Guid.Parse("8f097b14-8b0f-481c-922e-bc2c2cb7fec1"), 15_000, 35, 2335,
            (2337, 966, 28_800), (2337, 967, 50)),
        new(Guid.Parse("e43b59b0-60e9-4030-ab60-5a9c449d9f10"), "GreatElixirOfImmortality", 610,
            Guid.Parse("647c4ad5-f412-4da6-9739-6890423ac08e"), 25_000, 50, 2303,
            (2308, 959, 0), (2308, 960, 14_400)),
        new(Guid.Parse("171ff678-ee17-4bbd-abd1-1b9ddf59e72f"), "GreatElixirOfDreams", 609,
            Guid.Parse("e7c4cdb3-ee8a-481f-a2cb-ae9ce21ee05e"), 20_000, 40, 2301,
            (2307, 962, 100), (2307, 963, 86_400)),
        new(Guid.Parse("e10d6f84-8284-44d6-904a-ae5a8452a858"), "GreatElixirOfInsomnia", 614,
            Guid.Parse("ba74582c-9693-4e50-88bd-6dc07cdee409"), 10_000, 25, 2309,
            (2311, 964, 0), (2311, 965, 28_800)),
    };

    private static readonly Product[] PetPotions =
    {
        new(Guid.Parse("240f1c6d-c8a4-46b3-b5ca-17da00713ad9"), "GrowthElixir", 628,
            Guid.Parse("0f1594a3-35b4-4e71-b3dd-406ec8be4458"), 1_000, 5, 2386, (2387, 969, 100)),
        new(Guid.Parse("00a03dc2-f48a-49c0-8cc1-010d2ecf123f"), "PotentGrowthElixir", 629,
            Guid.Parse("777b858a-6ed0-408d-9c3c-29434156d21d"), 10_000, 20, 2386, (2393, 969, 500)),
        new(Guid.Parse("f2954163-a45f-41cb-b7ed-219bdf2332c0"), "RapidGrowthElixir", 630,
            Guid.Parse("ea2b5e81-e19c-4305-bc35-9aea7ad65ade"), 25_000, 35, 2386, (2394, 969, 1_000)),
        new(Guid.Parse("dcf5754d-a83d-4fbb-b549-9d3148773759"), "RareGrowthElixir", 631,
            Guid.Parse("dc922e58-2b67-476a-87e7-b100ad25b7dc"), 100_000, 75, 2386, (2395, 969, 10_000)),
        new(Guid.Parse("485d940d-6ef5-440e-a628-572828972eae"), "MythicalGrowthElixir", 632,
            Guid.Parse("e3d1bef2-f47c-4c0d-b7db-544100ce5b5b"), 500_000, 150, 2386, (2392, 969, 100_000)),
        new(Guid.Parse("62ba724e-188e-462d-a15d-69ca48ef78fa"), "LegendaryGrowthElixir", 633,
            Guid.Parse("5ff95c9f-57ba-4099-9baf-b6008cf201eb"), 2_000_000, 300, 2386, (2391, 969, 1_000_000)),
        new(Guid.Parse("efb6dc22-4401-4eb5-a628-aeb3729b21b8"), "FamiliarReviveElixir", 634,
            Guid.Parse("500b4ff0-3131-477c-9f1f-eb18bdeadf9c"), 5_000, 10, 2388, (2389, 970, 1)),
    };

    private static readonly Dictionary<string, CatalogDefinition> Catalogs = new(StringComparer.Ordinal)
    {
        ["merchant_newPotions_8"] = new(Guid.Parse("938b5621-ea87-48f0-81eb-8cf6bca2cc38"), Silver, Potions),
        ["merchant_OpalPotions"] = new(Guid.Parse("25210016-4d53-4d30-bb64-08119062477f"), Opals, Potions),
        ["merchant_petPotions2"] = new(Guid.Parse("e3ff41fc-234f-4fd0-9147-3205ead3a55b"), Silver, PetPotions),
        ["merchant_petPotionsOpals2"] = new(Guid.Parse("1df49839-6f6c-46db-b3b5-74fbaf90595c"), Opals, PetPotions),
    };

    // ---- Season reward tracks -------------------------------------------------
    // The client requests these two catalogs with CatalogCategory.SeasonProgress and matches
    // each item's Name against the regex (\d+)_(\d+)_(\d+), extracting the first two groups
    // as combinedMilestoneLevel = season*1000 + level. A two-part name fails the regex and
    // throws while building the tab, so always emit three parts: season, level, reward slot.
    // 30 levels each; the pass track uses the pet potions.
    public const int SeasonRewardLevels = 30;
    private static readonly Guid SeasonRewardCatalogId = Guid.Parse("5e4a3b21-0f6d-4c1a-9b7e-0d2c4f6a8b10");
    private static readonly Guid SeasonPassRewardCatalogId = Guid.Parse("7c9d5e32-1a8b-4d2f-8e60-3b5d7a9c1e20");

    private static Guid SeasonRewardItemId(bool pass, int level, int slot)
    {
        var bytes = new byte[16];
        BitConverter.TryWriteBytes(bytes.AsSpan(0, 4), (pass ? 0x9E3779B9u : 0x51ED2701u) ^ (uint)(level * 131 + slot));
        bytes[0] ^= 0xA5;
        bytes[15] ^= 0x5A;
        return new Guid(bytes);
    }

    private static Product SeasonProduct(int level, bool pass)
    {
        var pool = pass ? PetPotions : Potions;
        return pool[((level - 1) % pool.Length + pool.Length) % pool.Length];
    }

    public static SerializedItem CreateSeasonRewardItem(int level, bool pass) => CreateItem(SeasonProduct(level, pass));

    private static CatalogItemDto SeasonRewardDto(int level, bool pass, int slot)
    {
        var product = SeasonProduct(level, pass);
        return new CatalogItemDto
        {
            CatalogId = pass ? SeasonPassRewardCatalogId : SeasonRewardCatalogId,
            CatalogItemId = SeasonRewardItemId(pass, level, slot),
            Name = $"{GameStore.CurrentSeasonNumber()}_{level}_{slot}",
            StackSize = 1,
            DefinitionId = product.DefinitionId,
            Prices = new Dictionary<string, object>(),
            Metadata = new Dictionary<string, object>
            {
                ["SeasonLevel"] = level,
                ["SeasonPass"] = pass,
                ["DefinitionIntegerId"] = product.DefinitionIntegerId,
            },
        };
    }

    private Guid Owner => GameStore.Instance.RequireUser(Context.CallContext.RequestHeaders.GetValue("authorization"));

    public UnaryResult<GetCatalogResponse> GetCatalog(GetCatalogRequest req)
    {
        if (req != null && req.Category == CatalogCategory.SeasonProgress)
        {
            var pass = req.Name == "season_rewards_season_pass";
            if (pass || req.Name == "season_rewards_regular")
                return UnaryResult.FromResult(new GetCatalogResponse
                {
                    Catalog = new CatalogDto
                    {
                        Id = pass ? SeasonPassRewardCatalogId : SeasonRewardCatalogId,
                        Items = Enumerable.Range(1, SeasonRewardLevels).Select(l => SeasonRewardDto(l, pass, 1)).ToList(),
                    },
                });
        }

        if (req == null || req.Category != CatalogCategory.Merchant || !Catalogs.TryGetValue(req.Name ?? "", out var catalog))
            return UnaryResult.FromResult(new GetCatalogResponse { Catalog = new CatalogDto { Items = new() } });

        return UnaryResult.FromResult(new GetCatalogResponse
        {
            Catalog = new CatalogDto
            {
                Id = catalog.Id,
                Items = catalog.Products.Select(p => ToDto(catalog, p)).ToList(),
            },
        });
    }

    public UnaryResult<PurchaseCatalogItemForSilverResponse> PurchaseCatalogItemForSilver(PurchaseCatalogItemRequest req)
    {
        var (catalog, product) = ValidatePurchase(req, Silver);
        var purchase = GameStore.Instance.BuyMerchantItem(Owner, req.CharacterId!.Value,
            CreateItem(product), product.SilverPrice, useOpals: false);
        return UnaryResult.FromResult(new PurchaseCatalogItemForSilverResponse
        {
            Items = purchase.Items,
            NewSilver = purchase.NewBalance,
        });
    }

    public UnaryResult<PurchaseCatalogItemResponse> PurchaseCatalogItem(PurchaseCatalogItemRequest req)
    {
        var (catalog, product) = ValidatePurchase(req, Opals);
        var purchase = GameStore.Instance.BuyMerchantItem(Owner, req.CharacterId!.Value,
            CreateItem(product), product.OpalPrice, useOpals: true);
        return UnaryResult.FromResult(new PurchaseCatalogItemResponse
        {
            Items = purchase.Items,
            NewOpals = purchase.NewBalance,
        });
    }

    private static CatalogItemDto ToDto(CatalogDefinition catalog, Product product)
    {
        var price = catalog.Currency == Silver ? product.SilverPrice : product.OpalPrice;
        return new CatalogItemDto
        {
            CatalogId = catalog.Id,
            CatalogItemId = product.ItemId,
            Name = product.Name,
            StackSize = 1,
            DefinitionId = product.DefinitionId,
            Prices = new Dictionary<string, object> { [catalog.Currency] = price },
            Metadata = new Dictionary<string, object>(),
        };
    }

    private static (CatalogDefinition Catalog, Product Product) ValidatePurchase(PurchaseCatalogItemRequest req, string currency)
    {
        if (req?.CharacterId is null || req.CharacterId == Guid.Empty || req.Currency != currency)
            throw new RpcException(new Status(StatusCode.InvalidArgument, "Invalid merchant purchase"));
        var catalog = Catalogs.Values.FirstOrDefault(c => c.Id == req.CatalogId && c.Currency == currency)
            ?? throw new RpcException(new Status(StatusCode.NotFound, "Catalog not found"));
        var product = catalog.Products.FirstOrDefault(p => p.ItemId == req.CatalogItemId)
            ?? throw new RpcException(new Status(StatusCode.NotFound, "Catalog item not found"));
        var expectedPrice = currency == Silver ? product.SilverPrice : product.OpalPrice;
        if (req.PriceCents != checked((long)expectedPrice * 100))
            throw new RpcException(new Status(StatusCode.InvalidArgument, "Catalog price changed; refresh the merchant"));
        return (catalog, product);
    }

    private static SerializedItem CreateItem(Product product)
    {
        var attributes = new Dictionary<int, GameAttributeValue>
        {
            [20] = new() { Value = 1, ValueD = 1 }, // CurrentStackAmount
            [18] = new() { Value = 100, ValueD = 100 }, // potion max stack
        };
        var affixes = new List<SerializedAffix> { Affix(product.StackAffixId, 18, product.StackAffixId == 2301 || product.StackAffixId == 2303 || product.StackAffixId == 2309 || product.StackAffixId == 2335 ? 2 : 100) };
        attributes[18] = new GameAttributeValue { Value = (int)affixes[0].AttributeSpecifiers.Values[0].Value, ValueD = affixes[0].AttributeSpecifiers.Values[0].Value };

        foreach (var group in product.Effects.GroupBy(e => e.AffixId))
        {
            var specs = group.Select(e => new SerializedAttributeSpecifier { AttributeId = e.AttributeId, Value = e.Value }).ToList();
            affixes.Add(new SerializedAffix
            {
                DefinitionIntegerId = group.Key,
                Rarity = Rarity.E,
                AffixSource = AffixSources.Generated,
                AttributeSpecifiers = new SerializedAttributeSpecifierList { Values = specs },
            });
            foreach (var effect in group)
                attributes[effect.AttributeId] = new GameAttributeValue { Value = (int)effect.Value, ValueD = effect.Value };
        }

        return new SerializedItem
        {
            Id = Guid.NewGuid(),
            Name = product.Name,
            Slot = ItemSlotTypes.Inventory,
            DefinitionIntegerId = product.DefinitionIntegerId,
            BaseRarity = Rarity.E,
            Attributes = new SerializedAttributes
            {
                Values = new Dictionary<AttributeOrigin, Dictionary<int, GameAttributeValue>> { [AttributeOrigin.Item] = attributes },
                MultiplicativeValues = new(),
            },
            Affixes = affixes,
            Sockets = new(),
        };
    }

    private static SerializedAffix Affix(int definitionId, int attributeId, double value) => new()
    {
        DefinitionIntegerId = definitionId,
        Rarity = Rarity.E,
        AffixSource = AffixSources.Generated,
        AttributeSpecifiers = new SerializedAttributeSpecifierList
        {
            Values = new List<SerializedAttributeSpecifier> { new() { AttributeId = attributeId, Value = value } },
        },
    };

    // The item-trade merchants are separate systems; keep their previous harmless empty responses.
    public UnaryResult<TradeWithMerchantResponse> TradeWithMerchant(TradeWithMerchantRequest req)
        => UnaryResult.FromResult(Defaults.Create<TradeWithMerchantResponse>());
    public UnaryResult<GenerateSetItemMerchantOffersResponse> ViewSetItemMerchantOffers(GenerateSetItemMerchantOffersRequest req)
        => UnaryResult.FromResult(Defaults.Create<GenerateSetItemMerchantOffersResponse>());
    public UnaryResult<TradeWithSetItemMerchantResponse> TradeWithSetItemMerchant(TradeWithSetItemMerchantRequest req)
        => UnaryResult.FromResult(Defaults.Create<TradeWithSetItemMerchantResponse>());
}
