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

    private static bool PlayerOnFloor(WebCombatState state)
    {
        if (state.Map is not { } map) return false;
        var tile = 2 * CombatInstance.ArenaHalf / Math.Max(map.Width, map.Height);
        var x = (int)Math.Floor(state.Combat.PlayerX / tile + map.Width / 2.0);
        var z = (int)Math.Floor(state.Combat.PlayerZ / tile + map.Height / 2.0);
        return x >= 0 && x < map.Width && z >= 0 && z < map.Height
            && map.Rows[z][x] == '#';
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

                var safeTown = registry.EnterTown(owner, id);
                var worldInstance = registry.GetOrCreate(owner, id);
                worldInstance.MoveTo(18, 18); // leave a stale movement target in the old scene
                Check(safeTown.Combat.InTown && safeTown.Combat.Monsters.Count == 0,
                    "m3 portal: town is a safe zero-hostile scene");
                var entered = registry.EnterPortal(owner, id, portalItem.Id);
                Check(entered.Applied && entered.Reason == "ok", "m3 portal: entering consumes the portal from town");
                Check(entered.State.Map?.Width == 21 && PlayerOnFloor(entered.State),
                    "m3 portal: town-to-portal uses the destination map and spawns on its floor");
                clock.Current = clock.Current.AddSeconds(1);
                var afterTravel = registry.Advance(owner, id);
                Check(Math.Abs(afterTravel.Combat.PlayerX - entered.State.Combat.PlayerX) < 1e-9
                    && Math.Abs(afterTravel.Combat.PlayerZ - entered.State.Combat.PlayerZ) < 1e-9,
                    "m3 portal: entering clears the previous scene movement target");
                var retry = registry.EnterPortal(owner, id, portalItem.Id);
                Check(!retry.Applied && retry.Reason == "already_in_niflheim", "m3 portal: a retry does not consume again");
                var inside = registry.Portal(owner, id);
                Check(inside.InNiflheim && !inside.HasPortal, "m3 portal: the run is active and the item is gone");
                Check(inside.Packs == packs, $"m3 portal: the run uses the portal's pack count ({inside.Packs})");
                Check(entered.State.Combat.TotalPacks == Math.Max(2, packs) - 1
                    && entered.State.Combat.PacksRemaining == Math.Max(2, packs) - 1,
                    $"m3 portal: the run is {entered.State.Combat.PacksRemaining}/{entered.State.Combat.TotalPacks} packs");
                Check(entered.State.Combat.Monsters.Count is >= 1 and <= 5,
                    $"m3 portal: the first pack starts incrementally ({entered.State.Combat.Monsters.Count} so far)");
                Check(store.GetItems(owner, id).All(i => i.Id != portalItem.Id), "m3 portal: the portal item is not duplicated");

                var returned = registry.ReturnPortal(owner, id);
                Check(!registry.Portal(owner, id).InNiflheim && returned.Combat.InTown,
                    "m3 portal: returning leaves Niflheim for the safe town hub");
                var returnedAgain = registry.ReturnPortal(owner, id);
                Check(returnedAgain.Combat.InTown && returnedAgain.Combat.Version == returned.Combat.Version,
                    "m3 portal: repeated return is idempotent");
                var noItem = registry.EnterPortal(owner, id, Guid.NewGuid());
                Check(!noItem.Applied && noItem.Reason == "no_portal_item", "m3 portal: entering without a portal is rejected");

                // Drive the native tail state: the final fight opens an exit, the optional
                // chest is looted once, then using the exit grants the completion reward.
                var finalPortalItem = MerchantCatalog.CreateItem(portalProduct);
                store.GrantItems(owner, id, new List<SerializedItem> { finalPortalItem });
                var finalEntry = registry.EnterPortal(owner, id, finalPortalItem.Id);
                Check(finalEntry.Applied, "m3 portal: second run starts for final-pack reward regression");
                var finalRun = registry.GetOrCreate(owner, id);
                var finishPack = typeof(CombatInstance).GetMethod("FinishPack",
                    System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)
                    ?? throw new MissingMethodException(nameof(CombatInstance), "FinishPack");
                var silverBeforeCompletion = finalRun.Silver;
                var rngField = typeof(CombatInstance).GetField("rng",
                    System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
                var combatRng = (CombatRandom)rngField.GetValue(finalRun)!;
                var chestSeed = Enumerable.Range(1, 10000).Select(i => (ulong)i).First(seed =>
                {
                    var probe = new CombatRandom(seed);
                    return probe.NextDouble() < 0.2 && (int)(probe.NextDouble() * 101) >= 5;
                });
                for (var i = 0; i < finalRun.CombatPacks - 1; i++)
                    finishPack.Invoke(finalRun, new object[] { finalRun.PlayerLevel });
                combatRng.RestoreState(chestSeed);
                finishPack.Invoke(finalRun, new object[] { finalRun.PlayerLevel });
                Check(finalRun.NiflheimExitReady && finalRun.NiflheimChestSize == 1
                    && finalRun.Silver == silverBeforeCompletion,
                    "m3 portal: final fight opens the exit and a medium chest without early silver");
                var beforeFinalRewards = store.GetItems(owner, id).Count;
                var exitState = registry.Advance(owner, id);
                Check(!exitState.Combat.InTown && exitState.Combat.NiflheimExitReady
                    && registry.Portal(owner, id).ChestSize == 1,
                    "m3 portal: final fight leaves a persistent interactive exit and chest");
                using (var disk = System.Text.Json.JsonDocument.Parse(File.ReadAllText(Path.Combine(dir, "world.json"))))
                {
                    var run = disk.RootElement.GetProperty("Characters").GetProperty(id.ToString()).GetProperty("NiflheimRun");
                    Check(run.GetProperty("ExitReady").GetBoolean() && run.GetProperty("ChestSize").GetInt32() == 1
                        && !run.GetProperty("ChestOpened").GetBoolean(),
                        "m3 portal: exit and unopened chest are written to disk");
                }
                registry.Reset();
                Check(registry.Portal(owner, id).ExitReady && registry.Portal(owner, id).ChestSize == 1,
                    "m3 portal: exit and chest survive registry recreation");
                var opened = registry.OpenNiflheimChest(owner, id);
                Check(opened.Applied && opened.State.Loot.Count is >= 60 and <= 70,
                    $"m3 portal: medium chest grants its native 60–70 base rolls ({opened.State.Loot.Count})");
                var itemsAfterChest = store.GetItems(owner, id).Count;
                Check(itemsAfterChest > beforeFinalRewards && !registry.OpenNiflheimChest(owner, id).Applied,
                    "m3 portal: chest loot persists and reopening cannot duplicate items");
                using (var disk = System.Text.Json.JsonDocument.Parse(File.ReadAllText(Path.Combine(dir, "world.json"))))
                {
                    var run = disk.RootElement.GetProperty("Characters").GetProperty(id.ToString()).GetProperty("NiflheimRun");
                    Check(run.GetProperty("ExitReady").GetBoolean() && run.GetProperty("ChestOpened").GetBoolean(),
                        "m3 portal: opened chest flag is written to disk with its claim");
                }
                registry.Reset();
                Check(registry.Portal(owner, id).ChestOpened && store.GetItems(owner, id).Count == itemsAfterChest,
                    "m3 portal: opened chest stays opened after registry recreation");
                var finishedRun = registry.ReturnPortal(owner, id);
                Check(finishedRun.Combat.InTown && finishedRun.Combat.Silver - silverBeforeCompletion == 5000,
                    "m3 portal: using the exit grants the native 5000 silver exactly once");
                Check(store.GetNiflheimRunsCleared(owner, id) == 1,
                    "m3 portal: completed-run count is durable and advances exactly once");
                Check(!store.IsNiflheimActive(owner, id) && store.IsTownActive(owner, id)
                    && store.GetNiflheimRun(owner, id) is null,
                    "m3 portal: reward, completion counter and town transition commit together");
            }

            // Reload the exact current run, not merely its active flag.
            NiflheimRunState? savedRun;
            using (var store = new GameStore(dir, clock))
            {
                var registry = new CombatRegistry(store, clock);
                var portalProduct = MerchantCatalog.Find(Guid.Parse("15900000-0000-0000-0000-000000000159"))!.Value;
                var item = MerchantCatalog.CreateItem(portalProduct);
                store.GrantItems(owner, id, new List<SerializedItem> { item });
                var entered = registry.EnterPortal(owner, id, item.Id);
                Check(entered.Applied && store.GetNiflheimRun(owner, id)?.TotalPacks == entered.State.Combat.TotalPacks + 1,
                    "m3 portal: item consumption and initial pack checkpoint persist together");
                clock.Current = clock.Current.AddSeconds(0.2);
                registry.Advance(owner, id);
                savedRun = store.GetNiflheimRun(owner, id);
                Check(savedRun is { PendingPackSize: >= 0, Monsters.Count: >= 1 }
                    && savedRun.Monsters.Any(m => m.Alive),
                    "m3 portal: current pack and monster state are checkpointed after advancing");
            }
            using (var store = new GameStore(dir, clock))
            {
                Check(store.IsNiflheimActive(owner, id), "m3 portal: the active run survives a server restart");
                var registry = new CombatRegistry(store, clock);
                var restored = registry.GetOrCreate(owner, id).CaptureNiflheimRun();
                Check(restored is not null && savedRun is not null
                    && restored.TotalPacks == savedRun.TotalPacks && restored.PacksCleared == savedRun.PacksCleared
                    && restored.PendingPackSize == savedRun.PendingPackSize
                    && restored.RandomState == savedRun.RandomState
                    && restored.Monsters.Select(m => (m.Name, m.Hp, m.Rarity, m.Alive))
                        .SequenceEqual(savedRun.Monsters.Select(m => (m.Name, m.Hp, m.Rarity, m.Alive))),
                    "m3 portal: restart restores pack progress, RNG and current monster HP/rarity");
                Check(registry.Portal(owner, id).RunsCleared == 1,
                    "m3 portal: completed-run count also survives restart");
                var finishOnePack = typeof(CombatInstance).GetMethod("FinishPack",
                    System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
                finishOnePack.Invoke(registry.GetOrCreate(owner, id), new object[] { registry.GetOrCreate(owner, id).PlayerLevel });
                clock.Current = clock.Current.AddSeconds(0.2);
                var progressed = registry.Advance(owner, id);
                Check(progressed.Combat.PacksRemaining == restored.TotalPacks - 2,
                    "m3 portal: cleared-pack progress is persisted after the next spawn");
                registry.Reset();
                Check(registry.GetOrCreate(owner, id).Snapshot().PacksRemaining == progressed.Combat.PacksRemaining,
                    "m3 portal: registry recreation keeps the cleared-pack count");
                var returned = registry.ReturnPortal(owner, id);
                Check(returned.Combat.InTown && store.GetNiflheimRun(owner, id) is null,
                    "m3 portal: explicit return clears the checkpoint without consuming another item");
            }

            using (var store = new GameStore(dir, clock))
            {
                var registry = new CombatRegistry(store, clock);
                var portalProduct = MerchantCatalog.Find(Guid.Parse("15900000-0000-0000-0000-000000000159"))!.Value;
                var item = MerchantCatalog.CreateItem(portalProduct);
                store.GrantItems(owner, id, new List<SerializedItem> { item });
                registry.EnterPortal(owner, id, item.Id);
                var instance = registry.GetOrCreate(owner, id);
                var finish = typeof(CombatInstance).GetMethod("FinishPack",
                    System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
                for (var i = 0; i < instance.CombatPacks; i++) finish.Invoke(instance, new object[] { instance.PlayerLevel });
                registry.Advance(owner, id);
                Check(store.GetNiflheimRun(owner, id) is { NativeTail: true, ExitReady: true },
                    "m3 portal: exit-ready state is saved before a real process restart");
            }
            using (var store = new GameStore(dir, clock))
            {
                var registry = new CombatRegistry(store, clock);
                Check(registry.Portal(owner, id).ExitReady && registry.GetOrCreate(owner, id).NiflheimExitReady,
                    "m3 portal: real GameStore restart restores the interactive exit");
                var priorClears = store.GetNiflheimRunsCleared(owner, id);
                var exit = registry.ReturnPortal(owner, id);
                Check(exit.Combat.InTown && store.GetNiflheimRunsCleared(owner, id) == priorClears + 1,
                    "m3 portal: restored exit can complete exactly once after restart");
            }

            // The portal's Area_Contains_More_Bosses affix changes the number of
            // ordinary monsters in the final combat pack, and must survive a restart.
            using (var store = new GameStore(dir, clock))
            {
                var registry = new CombatRegistry(store, clock);
                var portalProduct = MerchantCatalog.Find(Guid.Parse("15900000-0000-0000-0000-000000000159"))!.Value;
                var item = MerchantCatalog.CreateItem(portalProduct);
                MerchantCatalog.SetItemAttribute(item, MerchantCatalog.NumMonsterPacksAttributeId, 2);
                MerchantCatalog.SetItemAttribute(item, 2717, 2);
                store.GrantItems(owner, id, new List<SerializedItem> { item });
                var entered = registry.EnterPortal(owner, id, item.Id);
                Check(entered.Applied && store.GetNiflheimRun(owner, id) is
                    { TotalPacks: 2, FinalPackExtraMonsters: 2, PendingPackSize: 2 },
                    "m3 portal: affixed portal persists two extra final-pack monsters");
            }
            using (var store = new GameStore(dir, clock))
            {
                var registry = new CombatRegistry(store, clock);
                var restored = registry.GetOrCreate(owner, id);
                Check(restored.CaptureNiflheimRun() is { FinalPackExtraMonsters: 2, PendingPackSize: 2 },
                    "m3 portal: final-pack modifier survives a real GameStore restart");
                clock.Current = clock.Current.AddSeconds(0.21);
                var expanded = registry.Advance(owner, id);
                Check(expanded.Combat.Monsters.Count == 3
                    && expanded.Combat.Monsters.All(m => !m.IsBoss),
                    "m3 portal: final pack spawns 1 + modifier ordinary monsters");
                registry.ReturnPortal(owner, id);
            }

            // Niflheim pack layout: TotalPacks packs, 2..5 monsters each, cleared sequentially.
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
            Check(maxAlive <= 5, $"m3 pack: a pack never exceeds five monsters ({maxAlive})");
            var nextPackSize = typeof(CombatInstance).GetMethod("NextPackSize",
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
            var packSizes = Enumerable.Range(0, 100).Select(_ => (int)nextPackSize.Invoke(packRun, new object[] { 1.0 })!).ToArray();
            Check(packSizes.Contains(5) && packSizes.All(size => size is >= 2 and <= 5),
                "m3 pack: the native 2..5 range produces five-member packs with fractional carry");

            var finalPackRun = new CombatInstance(strong, 0, 0, 0, 0, seed: 42);
            finalPackRun.SetWorld(new[] { new MonsterProfile("Bat") }, packs: 2,
                nativeNiflheimRun: true);
            Check(finalPackRun.CombatPacks == 1 && finalPackRun.Monsters.Count == 1
                && !finalPackRun.Monsters[0].IsBoss && finalPackRun.Monsters[0].Rarity == 0,
                "m3 pack: native final pack has one pool-selected mob, not a forced Boss");
            var finalPackFinish = typeof(CombatInstance).GetMethod("FinishPack",
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
            finalPackFinish.Invoke(finalPackRun, new object[] { finalPackRun.PlayerLevel });
            Check(finalPackRun.NiflheimExitReady && finalPackRun.PacksRemaining == 0
                && finalPackRun.DungeonsCleared == 0,
                "m3 pack: clearing the final special pack spawns the exit without auto-completing");
            Check(finalPackRun.CompleteNiflheimExit() && !finalPackRun.CompleteNiflheimExit()
                && finalPackRun.DungeonsCleared == 1 && finalPackRun.Silver == 5000,
                "m3 pack: town portal grants completion and silver exactly once");

            var largeChestRun = new CombatInstance(strong, 0, 0, 0, 0, seed: 84);
            largeChestRun.SetWorld(new[] { new MonsterProfile("Bat") }, packs: 2,
                nativeNiflheimRun: true);
            var largeRng = (CombatRandom)typeof(CombatInstance).GetField("rng",
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.GetValue(largeChestRun)!;
            var largeSeed = Enumerable.Range(1, 10000).Select(i => (ulong)i).First(seed =>
            {
                var probe = new CombatRandom(seed);
                return probe.NextDouble() < 0.2 && (int)(probe.NextDouble() * 101) < 5;
            });
            largeRng.RestoreState(largeSeed);
            finalPackFinish.Invoke(largeChestRun, new object[] { largeChestRun.PlayerLevel });
            var largeLoot = largeChestRun.OpenNiflheimChest();
            Check(largeChestRun.NiflheimChestSize == 2 && largeLoot is >= 100 and <= 125
                && largeChestRun.OpenNiflheimChest() == 0,
                "m3 pack: rare large exit chest gives 100–125 base rolls only once");

            var liveFinalRun = new CombatInstance(strong, 0, 0, 0, 0, seed: 54);
            liveFinalRun.SetWorld(new[] { new MonsterProfile("Bat") }, packs: 2,
                nativeNiflheimRun: true);
            for (var i = 0; i < 6000 && !liveFinalRun.NiflheimExitReady; i++) liveFinalRun.Advance(0.05);
            Check(liveFinalRun.NiflheimExitReady && liveFinalRun.Monsters.All(m => !m.Alive),
                "m3 pack: actual final-pack combat unlocks the exit without auto-return");
        }
        finally { Directory.Delete(dir, true); }
    }
}
