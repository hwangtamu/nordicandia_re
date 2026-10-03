using Game;
using Nordicandia.Server.State;
using Nordicandia.Server.WebApi;
using Nordicandia.Simulation;
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
                store.SaveRealtimeProgress(owner, id, 0, 0, 5000);

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
                // Recovered rate: truncate(efficiency * clamp(1.5*tier, 10, 45)); efficiency 1.5.
                Check(OfflineRewards.KillsPerMinute(1.5, 1) == 15, "m3 offline: tier 1 clamps the rate to 10*1.5");
                Check(OfflineRewards.KillsPerMinute(1.5, 10) == 22, "m3 offline: tier 10 truncates 22.5");
                Check(OfflineRewards.KillsPerMinute(1.5, 30) == 67, "m3 offline: tier 30 clamps at 45*1.5 and truncates");

                // ----- blessings -----
                var before = registry.ActiveBlessings(owner, id);
                Check(before.Active.Count(b => b.Type == Blessings.Odin) == 1
                    && before.Active.Single(b => b.Type == Blessings.Odin).Active == false,
                    "m3 blessings: Odin starts inactive");
                var strengthBefore = registry.Attributes(owner, id).Strength;
                var open = registry.Offer(owner, id, Blessings.Odin, 1);
                Check(open.Applied && open.Reason == "ok", "m3 blessings: a small Odin offering is bought");
                var purchased = registry.ActiveBlessings(owner, id).Active.Single(b => b.Type == Blessings.Odin);
                Check(purchased.Active && purchased.SecondsRemaining > 0,
                    $"m3 blessings: Odin is active ({purchased.SecondsRemaining:F0}s)");
                var strengthAfter = registry.Attributes(owner, id).Strength;
                Check(strengthAfter > strengthBefore * 1.3,
                    $"m3 blessings: Odin applies +40% Strength to the engine ({strengthBefore:F1} -> {strengthAfter:F1})");
                var engine = Nordicandia.Simulation.CharacterAttributeEngine.Instance;
                var tyrEval = engine.Evaluate(Blessings.AttributeBonuses(new[] { Blessings.Tyr }));
                Check(tyrEval.Resolve("Movement_Speed_Bonus_Percent_Final") == Blessings.Magnitude,
                    "m3 blessings: Tyr targets Movement_Speed_Bonus_Percent_Final (+40%)");
                var friggEval = engine.Evaluate(Blessings.AttributeBonuses(new[] { Blessings.Frigg }));
                Check(friggEval.Resolve("Item_Quantity_Bonus_Percent_Total") == Blessings.Magnitude,
                    "m3 blessings: Frigg targets Item_Quantity_Bonus_Percent_Total (+40%)");
                var thorEval = engine.Evaluate(Blessings.AttributeBonuses(new[] { Blessings.Thor }));
                Check(thorEval.Resolve("Weapon_Damage_Percent_Bonus_Final") == Blessings.Magnitude,
                    "m3 blessings: Thor targets Weapon_Damage_Percent_Bonus_Final (+40%)");
                var expensive = registry.Offer(owner, id, Blessings.Thor, 4);
                Check(expensive.Applied, "m3 blessings: an ExtraLarge offering is affordable at 5000 opals");
                clock.Current = clock.Current.AddMinutes(11);
                var expired = registry.ActiveBlessings(owner, id).Active.Single(b => b.Type == Blessings.Odin);
                Check(!expired.Active, "m3 blessings: the short blessing expires");

                // ----- Niflheim portal -----
                var portalProduct = MerchantCatalog.Find(Guid.Parse("15900000-0000-0000-0000-000000000159"))!.Value;
                var portalItem = MerchantCatalog.CreateItem(portalProduct);
                store.GrantItems(owner, id, new List<SerializedItem> { portalItem });
                var portalState = registry.Portal(owner, id);
                Check(portalState.HasPortal && portalState.PortalItemId == portalItem.Id, "m3 portal: the portal is in the bag");
                var packs = (int)LootTable.AttributeOf(portalItem, MerchantCatalog.NumMonsterPacksAttributeId);
                Check(packs is >= 11 and <= 20, $"m3 portal: the portal carries a NumMonsterPacks count ({packs})");

                var entered = registry.EnterPortal(owner, id, portalItem.Id);
                Check(entered.Applied && entered.Reason == "ok", "m3 portal: entering consumes the portal");
                var retry = registry.EnterPortal(owner, id, portalItem.Id);
                Check(!retry.Applied && retry.Reason == "already_in_niflheim", "m3 portal: a retry does not consume again");
                var inside = registry.Portal(owner, id);
                Check(inside.InNiflheim && !inside.HasPortal, "m3 portal: the run is active and the item is gone");
                Check(inside.Packs == packs, $"m3 portal: the run uses the portal's pack count ({inside.Packs})");
                Check(entered.State.Combat.TotalPacks == Math.Max(2, packs)
                    && entered.State.Combat.PacksRemaining == Math.Max(2, packs),
                    $"m3 portal: the run is {entered.State.Combat.PacksRemaining}/{entered.State.Combat.TotalPacks} packs");
                Check(entered.State.Combat.Monsters.Count is >= 1 and <= 4,
                    $"m3 portal: the first pack starts with 2..4 monsters ({entered.State.Combat.Monsters.Count} so far)");
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

            // Niflheim pack layout: TotalPacks packs, 2..4 monsters each, cleared sequentially.
            var strong = CombatantStats.FromRealtime(3000, 1000, 100, 30);
            var packRun = new CombatInstance(strong, 0, 0, 0, 0, seed: 123);
            packRun.SetWorld(new[] { new MonsterProfile("Bat") }, packs: 4);
            var configured = packRun.TotalPacks;
            var seen = new HashSet<int>();
            var maxAlive = 0;
            for (var i = 0; i < 6000 && packRun.DungeonsCleared == 0; i++)
            {
                packRun.Advance(0.05);
                seen.Add(packRun.PacksRemaining);
                maxAlive = Math.Max(maxAlive, packRun.Monsters.Count(m => m.Alive));
            }
            Check(configured == 4, "m3 pack: the run is configured with four packs");
            Check(seen.Contains(4) && seen.Contains(3) && seen.Contains(2) && seen.Contains(1) && seen.Contains(0),
                "m3 pack: packs clear one at a time and the run reaches zero");
            Check(packRun.DungeonsCleared >= 1, "m3 pack: clearing every pack completes the run");
            Check(maxAlive <= 4, $"m3 pack: a pack never exceeds four monsters ({maxAlive})");
        }
        finally { Directory.Delete(dir, true); }
    }
}
