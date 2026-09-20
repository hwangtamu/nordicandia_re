using Game;
using Grpc.Core;
using Grpc.Net.Client;
using MagicOnion.Client;
using MagicOnion.Serialization;
using MessagePack;
using SharedNet.Api;

namespace Nordicandia.TestClient;

/// <summary>Exercises catalog loading, both currencies, price validation, and inventory persistence.</summary>
public static class MerchantProbe
{
    public static async Task<int> RunAsync(string address)
    {
        var http = new HttpClientHandler();
        if (address.StartsWith("https", StringComparison.OrdinalIgnoreCase))
            http.ServerCertificateCustomValidationCallback = (m, c, ch, e) => true;
        var serializer = MessagePackMagicOnionSerializerProvider.Default
            .WithOptions(MessagePackSerializer.DefaultOptions.WithCompression(MessagePackCompression.Lz4BlockArray));

        using var loginChannel = GrpcChannel.ForAddress(address, new GrpcChannelOptions { HttpHandler = http });
        var login = MagicOnionClient.Create<ILoginServiceApi>(loginChannel, serializer);
        var session = await TestAuth.RegisterAndLoginAsync(login, "merchant");
        using var channel = GrpcChannel.ForAddress(address, new GrpcChannelOptions
        {
            HttpHandler = new HttpHeaderHandler(new HttpClientHandler(), session.Session.AuthToken),
        });
        var characters = MagicOnionClient.Create<ICharacterServiceApi>(channel, serializer);
        var created = await characters.CreateCharacter(new CreateCharacterRequest
        {
            DisplayName = "merchant-test",
            CharacterClass = SharedNet.Constants.Game.CharacterClass.Warrior,
            CharacterRace = SharedNet.Constants.Game.CharacterRace.Human,
            CharacterGameMode = SharedNet.Constants.Game.GameMode.Normal,
            Data = new SerializedCharacterData
            {
                Header = new SerializedCharacterData.SerializedHeader { Name = "merchant-test", Level = 1 },
                Data = new SerializedCharacterData.SerializedData
                {
                    Attributes = new SerializedAttributes { Values = new(), MultiplicativeValues = new() },
                    Items = new SerializedItems { Items = new() },
                    CombatStats = new SerializedCharacterData.SerializedCombatStats { HelheimAttempts = new() },
                },
            },
        });
        var characterId = created.Character.CharacterId;
        var currency = MagicOnionClient.Create<IVirtualCurrencyServiceApi>(channel, serializer);
        await currency.GainSilver(new GainSilverRequest { CharacterId = characterId, Amount = 100_000 });
        await currency.GainVirtualCurrency(new GainVirtualCurrencyRequest { CharacterId = characterId, Amount = 100 });

        var merchant = MagicOnionClient.Create<ICatalogServiceApi>(channel, serializer);
        var silverCatalog = (await merchant.GetCatalog(new GetCatalogRequest
        {
            Name = "merchant_newPotions_8", Category = SharedNet.Constants.CatalogCategory.Merchant,
        })).Catalog;
        var knowledge = silverCatalog.Items.Single(i => i.Name == "GreatElixirOfKnowledge");
        var silverPrice = Convert.ToInt64(knowledge.Prices["SL"]);
        var silverPurchase = await merchant.PurchaseCatalogItemForSilver(new PurchaseCatalogItemRequest
        {
            CharacterId = characterId, CatalogId = silverCatalog.Id, CatalogItemId = knowledge.CatalogItemId,
            Currency = "SL", PriceCents = silverPrice * 100,
        });

        var opalCatalog = (await merchant.GetCatalog(new GetCatalogRequest
        {
            Name = "merchant_OpalPotions", Category = SharedNet.Constants.CatalogCategory.Merchant,
        })).Catalog;
        var quantity = opalCatalog.Items.Single(i => i.Name == "GreatElixirOfQuantity");
        var opalPrice = Convert.ToInt64(quantity.Prices["OP"]);
        var opalPurchase = await merchant.PurchaseCatalogItem(new PurchaseCatalogItemRequest
        {
            CharacterId = characterId, CatalogId = opalCatalog.Id, CatalogItemId = quantity.CatalogItemId,
            Currency = "OP", PriceCents = opalPrice * 100,
        });

        var rejectedTamperedPrice = false;
        try
        {
            await merchant.PurchaseCatalogItemForSilver(new PurchaseCatalogItemRequest
            {
                CharacterId = characterId, CatalogId = silverCatalog.Id, CatalogItemId = knowledge.CatalogItemId,
                Currency = "SL", PriceCents = 1,
            });
        }
        catch (RpcException e) when (e.StatusCode == StatusCode.InvalidArgument)
        {
            rejectedTamperedPrice = true;
        }

        var petCatalog = (await merchant.GetCatalog(new GetCatalogRequest
        {
            Name = "merchant_petPotions2", Category = SharedNet.Constants.CatalogCategory.Merchant,
        })).Catalog;
        var entered = await characters.EnterGameWithCharacter(new EnterGameWithCharacterRequest { CharacterId = characterId });
        var items = entered.Character.Items.Items;
        var knowledgeItem = items.SingleOrDefault(i => i.Name == "GreatElixirOfKnowledge");
        var quantityItem = items.SingleOrDefault(i => i.Name == "GreatElixirOfQuantity");

        var ok = silverCatalog.Items.Count == 5 && petCatalog.Items.Count == 7
            && silverPurchase.NewSilver == 90_000 && opalPurchase.NewOpals == 65
            && knowledgeItem?.DefinitionIntegerId == 606 && quantityItem?.DefinitionIntegerId == 616
            && knowledgeItem.Attributes.Values[SharedNet.Constants.Game.AttributeOrigin.Item][145].ValueD == 28_800
            && rejectedTamperedPrice;
        Console.WriteLine($"CATALOG potions={silverCatalog.Items.Count} petPotions={petCatalog.Items.Count}");
        Console.WriteLine($"PURCHASE silver={silverPurchase.NewSilver} opals={opalPurchase.NewOpals} persistedItems={items.Count} tamperRejected={rejectedTamperedPrice}");
        Console.WriteLine(ok ? "MERCHANT OK" : "MERCHANT FAIL");
        return ok ? 0 : 6;
    }

    private sealed class HttpHeaderHandler : DelegatingHandler
    {
        private readonly string token;
        public HttpHeaderHandler(HttpMessageHandler inner, string token) : base(inner) => this.token = token;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);
            return base.SendAsync(request, cancellationToken);
        }
    }
}
