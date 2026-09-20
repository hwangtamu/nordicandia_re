using MagicOnion;
using MagicOnion.Server;
using Nordicandia.Server.State;
using SharedNet.Api;
using SharedNet.Constants;
using SharedNet.Dto;

namespace Nordicandia.Server.Services;

/// <summary>
/// Remote IAP store. The desktop client's custom purchasing module asks the server for its
/// product list (<see cref="GetStoreItems"/>) and builds the Unity IAP catalog from it; the
/// season window's <c>Init</c> blocks on that initialization. Real payment providers can't
/// run on a private server, so purchases are granted locally.
/// </summary>
public sealed partial class StoreServiceApiImpl
{
    private static readonly object OrderGate = new();
    private static readonly Dictionary<ulong, string> Orders = new();
    private static ulong nextOrderId = 1;

    private static readonly Guid SeasonPassId = Guid.Parse("a1000000-0000-4000-8000-000000000001");
    private static readonly List<StoreItemDto> Products = new()
    {
        new StoreItemDto
        {
            StoreItemId = SeasonPassId, Sku = "season_pass", ItemClass = "SeasonPass",
            ProductType = StoreItemProductType.NonConsumable,
            LocalizedName = "Season Pass", LocalizedDescription = "Unlock the season-pass reward track.",
            LocalizedPrice = 4.99m, PriceUSD = 4.99m, LocalizedPriceString = "$4.99", Currency = Currency.USD,
            Tags = new List<string> { "season_pass" },
        },
        new StoreItemDto
        {
            StoreItemId = Guid.Parse("a1000000-0000-4000-8000-000000000002"), Sku = "opals_100", ItemClass = "Opals",
            ProductType = StoreItemProductType.Consumable,
            LocalizedName = "100 Opals", LocalizedDescription = "A small pouch of opals.",
            LocalizedPrice = 0.99m, PriceUSD = 0.99m, LocalizedPriceString = "$0.99", Currency = Currency.USD,
            ContainedOpals = 100, Tags = new List<string> { "opals" },
        },
        new StoreItemDto
        {
            StoreItemId = Guid.Parse("a1000000-0000-4000-8000-000000000003"), Sku = "opals_500", ItemClass = "Opals",
            ProductType = StoreItemProductType.Consumable,
            LocalizedName = "500 Opals", LocalizedDescription = "A chest of opals.",
            LocalizedPrice = 4.99m, PriceUSD = 4.99m, LocalizedPriceString = "$4.99", Currency = Currency.USD,
            ContainedOpals = 500, Tags = new List<string> { "opals" },
        },
        new StoreItemDto
        {
            StoreItemId = Guid.Parse("a1000000-0000-4000-8000-000000000004"), Sku = "opals_1200", ItemClass = "Opals",
            ProductType = StoreItemProductType.Consumable,
            LocalizedName = "1200 Opals", LocalizedDescription = "A vault of opals.",
            LocalizedPrice = 9.99m, PriceUSD = 9.99m, LocalizedPriceString = "$9.99", Currency = Currency.USD,
            ContainedOpals = 1200, Tags = new List<string> { "opals" },
        },
    };

    private Guid Owner => GameStore.Instance.RequireUser(Context.CallContext.RequestHeaders.GetValue("authorization"));

    public UnaryResult<GetStoreItemsResponse> GetStoreItems(GetStoreItemsRequest req)
    {
        Console.WriteLine($"[STORE] GetStoreItems store={req?.StoreName} provider={req?.StoreProvider} -> {Products.Count} products");
        return UnaryResult.FromResult(new GetStoreItemsResponse { StoreItems = Products });
    }

    public UnaryResult<StartPurchaseResponse> StartPurchase(StartPurchaseRequest req)
    {
        ulong orderId;
        lock (OrderGate)
        {
            orderId = nextOrderId++;
            Orders[orderId] = req?.Sku ?? "";
        }
        Console.WriteLine($"[STORE] StartPurchase sku={req?.Sku} order={orderId}");
        return UnaryResult.FromResult(new StartPurchaseResponse { OrderId = orderId, Sku = req?.Sku });
    }

    public UnaryResult<PayForPurchaseResponse> PayForPurchase(PayForPurchaseRequest req)
        => UnaryResult.FromResult(new PayForPurchaseResponse
        {
            OrderId = req?.OrderId ?? 0,
            StoreProvider = req?.StoreProvider ?? StoreProvider.Unknown,
            ProviderToken = "private-server",
            PurchaseCurrency = Currency.USD,
        });

    public UnaryResult<ConfirmPurchaseResponse> ConfirmPurchase(ConfirmPurchaseRequest req)
    {
        var owner = Owner;
        var product = Products.FirstOrDefault(p => p.Sku == SkuForOrder(req?.OrderId ?? 0));
        if (product?.Tags?.Contains("season_pass") == true)
            GameStore.Instance.SetSeasonPass(owner, true);
        Console.WriteLine($"[STORE] ConfirmPurchase order={req?.OrderId} sku={product?.Sku} opals={product?.ContainedOpals ?? 0}");
        return UnaryResult.FromResult(new ConfirmPurchaseResponse
        {
            OrderId = req?.OrderId ?? 0,
            Sku = product?.Sku,
            LocalizedItemName = product?.LocalizedName,
            ContainedOpals = product?.ContainedOpals ?? 0,
            PurchaseTimestamp = DateTime.UtcNow,
        });
    }

    private static string SkuForOrder(ulong orderId)
    {
        lock (OrderGate) return Orders.TryGetValue(orderId, out var sku) ? sku : null;
    }
}