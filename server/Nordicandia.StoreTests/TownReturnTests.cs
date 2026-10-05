using Game;
using Nordicandia.Server.State;
using Nordicandia.Server.WebApi;
using Nordicandia.Simulation;
using SharedNet.Api;
using SharedNet.Constants.Game;

static class TownReturnTests
{
    static void Check(bool ok, string name)
    {
        if (!ok) throw new Exception(name);
        Console.WriteLine("PASS " + name);
    }
    public static void Run()
    {
        var directory = Path.Combine(Path.GetTempPath(), "nord-town-return-" + Guid.NewGuid());
        try
        {
            var clock = new Clock();
            using var store = new GameStore(directory, clock);
            var owner = store.GetOrCreateUser("device:town-return").UserId;
            var id = store.CreateCharacter(owner, new CreateCharacterRequest {
                DisplayName = "return", CharacterGameMode = GameMode.Normal,
                Data = new SerializedCharacterData { Data = Defaults.Create<SerializedCharacterData.SerializedData>() }
            }).CharacterId;
            store.SaveRealtimeProgress(owner, id, 0, 7000000, 1000);
            var registry = new CombatRegistry(store, clock);
            registry.EnterTown(owner, id);
            store.UnlockPet(owner, id, 98, 600);
            store.UnlockCombatPet(owner, id, false, 186, 6000000);
            clock.Utc = clock.Utc.AddSeconds(1);
            var afterPurchase = registry.Advance(owner, id);
            Check(afterPurchase.Combat.Opals == 400 && afterPurchase.Combat.Silver == 1000000
                && store.GetRealtimeProgress(owner, id).Opals == 400
                && store.GetRealtimeProgress(owner, id).Silver == 1000000,
                "town purchase: combat polling preserves pet purchase debits in runtime and storage");
            var product = MerchantCatalog.Products.First(p => p.SilverPrice > 0 && p.SilverPrice < 1000000);
            store.BuyMerchantItem(owner, id, MerchantCatalog.CreateItem(product), product.SilverPrice, false);
            clock.Utc = clock.Utc.AddSeconds(1);
            var merchant = registry.Advance(owner, id);
            Check(merchant.Combat.Silver == 1000000 - product.SilverPrice,
                "town purchase: merchant debit also survives the next combat flush");
            clock.Utc = clock.Utc.AddMinutes(10);
            var returned = registry.LeaveTown(owner, id);
            var immediate = registry.Advance(owner, id);
            Check(!immediate.Combat.InTown && immediate.Combat.Version == returned.Combat.Version
                && immediate.Combat.PlayerHp == returned.Combat.PlayerHp,
                "town return: time spent in town is not simulated in the destination fight");
            var layoutBefore = registry.GetOrCreate(owner, id).Layout;
            registry.Reset();
            var restoredLayout = registry.GetOrCreate(owner, id).Layout;
            Check(Enumerable.Range(0, layoutBefore.Height).All(z => Enumerable.Range(0, layoutBefore.Width)
                    .All(x => layoutBefore.IsFloor(x,z) == restoredLayout.IsFloor(x,z))),
                "town return: selected world layout stays identical across registry restart");
            Check(store.GetPetRoster(owner, id).PetDefinitionIntegerIds.Contains(98),
                "town purchase: acquired companion remains owned after world return and registry restart");
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
    }
    sealed class Clock : TimeProvider
    {
        public DateTimeOffset Utc = new(2026, 10, 4, 12, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => Utc;
    }
}
