using Game;
using Nordicandia.Server.State;
using Nordicandia.Server.WebApi;
using SharedNet.Api;
using SharedNet.Constants.Game;

/// <summary>M3 acceptance: offline rewards, Aesir blessings and the Niflheim portal flow.</summary>
static class M3Tests
{
    sealed class TestClock : TimeProvider
    {
        public DateTimeOffset Current = new(2026, 10, 2, 12, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => Current;
    }

    public static void Run()
    {
        var dir = Path.Combine(Path.GetTempPath(), "nord-m3-" + Guid.NewGuid());
        var clock = new TestClock();
        void Check(bool ok, string name) { if (!ok) throw new Exception(name); Console.WriteLine("PASS " + name); }
        try
        {
            Guid owner, id;
            using (var store = new GameStore(dir, clock))
            {
                owner = store.GetOrCreateUser("device:m3-test").UserId;
                id = store.CreateCharacter(owner, new CreateCharacterRequest
                {
                    DisplayName = "m3",
                    CharacterGameMode = GameMode.Normal,
                    Data = new SerializedCharacterData { Data = Defaults.Create<SerializedCharacterData.SerializedData>() },
                }).CharacterId;
                // Give the character some opals for offerings.
                store.SaveRealtimeProgress(owner, id, 0, 0, 1000);

                var registry = new CombatRegistry(store, clock);

                // ----- offline rewards -----
                clock.Current = clock.Current.AddHours(1);
                var offline = registry.Offline(owner, id);
                Check(offline.AwaySeconds >= 3599 && offline.EligibleSeconds == 3600,
                    $"m3 offline: away window reported ({offline.AwaySeconds}s)");
                Check(offline.Experience > 0 && offline.Claimable, "m3 offline: a claim is available");
                var claimed = registry.ClaimOffline(owner, id);
                Check(claimed.Experience == offline.Experience, "m3 offline: claim grants the previewed experience");
                var second = registry.ClaimOffline(owner, id);
                Check(second.Experience == 0 && !second.Claimable, "m3 offline: a second claim grants nothing");

                // ----- blessings -----
                var before = registry.ActiveBlessings(owner, id);
                Check(before.Active.Count(b => b.Type == Blessings.Odin) == 1
                    && before.Active.Single(b => b.Type == Blessings.Odin).Active == false,
                    "m3 blessings: Odin starts inactive");
                var open = registry.Offer(owner, id, Blessings.Odin, 1);
                Check(open.Applied && open.Reason == "ok", "m3 blessings: a small Odin offering is bought");
                var purchased = registry.ActiveBlessings(owner, id).Active.Single(b => b.Type == Blessings.Odin);
                Check(purchased.Active && purchased.SecondsRemaining > 0,
                    $"m3 blessings: Odin is active ({purchased.SecondsRemaining:F0}s)");
                var expensive = registry.Offer(owner, id, Blessings.Thor, 4);
                Check(expensive.Applied, "m3 blessings: an ExtraLarge offering is affordable at 1000 opals");
                clock.Current = clock.Current.AddMinutes(11);
                var expired = registry.ActiveBlessings(owner, id).Active.Single(b => b.Type == Blessings.Odin);
                Check(!expired.Active, "m3 blessings: the short blessing expires");

                // ----- Niflheim portal -----
                var portalProduct = MerchantCatalog.Find(Guid.Parse("15900000-0000-0000-0000-000000000159"))!.Value;
                var portalItem = MerchantCatalog.CreateItem(portalProduct);
                store.GrantItems(owner, id, new List<SerializedItem> { portalItem });
                var portalState = registry.Portal(owner, id);
                Check(portalState.HasPortal && portalState.PortalItemId == portalItem.Id, "m3 portal: the portal is in the bag");

                var entered = registry.EnterPortal(owner, id, portalItem.Id);
                Check(entered.Applied && entered.Reason == "ok", "m3 portal: entering consumes the portal");
                var retry = registry.EnterPortal(owner, id, portalItem.Id);
                Check(!retry.Applied && retry.Reason == "already_in_niflheim", "m3 portal: a retry does not consume again");
                var inside = registry.Portal(owner, id);
                Check(inside.InNiflheim && !inside.HasPortal, "m3 portal: the run is active and the item is gone");
                Check(store.GetItems(owner, id).All(i => i.Id != portalItem.Id), "m3 portal: the portal item is not duplicated");

                var returned = registry.ReturnPortal(owner, id);
                Check(!registry.Portal(owner, id).InNiflheim, "m3 portal: returning leaves Niflheim");
                var noItem = registry.EnterPortal(owner, id, Guid.NewGuid());
                Check(!noItem.Applied && noItem.Reason == "no_portal_item", "m3 portal: entering without a portal is rejected");
            }

            // Reload to prove the Niflheim flag survives a restart.
            using (var store = new GameStore(dir, clock))
            {
                var registry = new CombatRegistry(store, clock);
                var portalProduct = MerchantCatalog.Find(Guid.Parse("15900000-0000-0000-0000-000000000159"))!.Value;
                var item = MerchantCatalog.CreateItem(portalProduct);
                store.GrantItems(owner, id, new List<SerializedItem> { item });
                registry.EnterPortal(owner, id, item.Id);
            }
            using (var store = new GameStore(dir, clock))
            {
                Check(store.IsNiflheimActive(owner, id), "m3 portal: the active run survives a server restart");
            }
        }
        finally { Directory.Delete(dir, true); }
    }
}
