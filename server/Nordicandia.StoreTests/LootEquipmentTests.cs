using Game;
using Nordicandia.Server.State;
using Nordicandia.Server.WebApi;
using Nordicandia.Simulation;
using SharedNet.Api;
using SharedNet.Constants.Game;

/// <summary>
/// M2 acceptance checks: kills drop persistent loot, equipping changes the authoritative
/// combat stats, equip commands are idempotent, and the boss/dungeon loop completes.
/// </summary>
static class LootEquipmentTests
{
    public static void Run()
    {
        LootPersistsEquipChangesStatsAndBossClears();
        SlotsSkillsAndEnemyVariety();
    }

    private static void SlotsSkillsAndEnemyVariety()
    {
        void Check(bool ok, string name) { if (!ok) throw new Exception(name); Console.WriteLine("PASS " + name); }
        var dir = Path.Combine(Path.GetTempPath(), "nord-m2b-" + Guid.NewGuid());
        Directory.CreateDirectory(dir);
        try
        {
            using var store = new GameStore(dir);
            var clock = new FakeClock();
            var owner = store.GetOrCreateUser("device:m2b-test").UserId;
            var data = Defaults.Create<SerializedCharacterData.SerializedData>();
            data.CombatStats = new SerializedCharacterData.SerializedCombatStats { Offense = 200, Defense = 50, Recovery = 5 };
            var characterId = store.CreateCharacter(owner, new CreateCharacterRequest
            {
                DisplayName = "m2b",
                CharacterClass = CharacterClass.Warrior,
                CharacterRace = CharacterRace.Human,
                CharacterGameMode = GameMode.Normal,
                Data = new SerializedCharacterData { Data = data },
            }).CharacterId;
            var registry = new CombatRegistry(store, clock);

            // Enemy variety: the instance cycles through several archetypes.
            clock.Advance(TimeSpan.FromSeconds(6));
            var state = registry.Advance(owner, characterId);
            var names = state.Combat.Monsters.Where(m => !m.IsBoss).Select(m => m.Name).Distinct().ToList();
            Check(names.Count >= 2, "m2 enemies: more than one archetype spawns");

            // Skills: Warrior skill 1 is "Might" (a rally/buff). It always casts and applies a buff.
            Check(state.Combat.Skills.Count == 3 && state.Combat.Skills.Select(s => s.Name).SequenceEqual(new[] { "Slam", "Might", "Pounce" }),
                "m2 skills: real Warrior kit Slam/Might/Pounce is loaded");
            var rally = registry.ApplyCommand(owner, characterId, "sk-rally", state.Combat.Version,
                new WebCommandRequest("skill", SkillId: 1));
            Check(rally.Applied && rally.Reason == "ok", "m2 skills: rally casts");
            Check(rally.State.Combat.OffenseBuffRemaining > 0, "m2 skills: rally applies an offence buff");
            Check(rally.State.Combat.Skills.Count == 3, "m2 skills: three cooldowns are tracked");
            var rallyAgain = registry.ApplyCommand(owner, characterId, "sk-rally-2", rally.State.Combat.Version,
                new WebCommandRequest("skill", SkillId: 1));
            Check(!rallyAgain.Applied && rallyAgain.Reason == "cooldown", "m2 skills: repeated rally is on cooldown");

            // One item per equip slot: equipping B must unequip A.
            var itemA = LootTable.CreateItem(new LootDrop(3, 2, 5, false, 1111));
            var itemB = LootTable.CreateItem(new LootDrop(3, 3, 5, false, 8));
            var offA = LootTable.AttributeOf(itemA, LootTable.AttrOffense);
            var offB = LootTable.AttributeOf(itemB, LootTable.AttrOffense);
            store.GrantItems(owner, characterId, new List<SerializedItem> { itemA, itemB });
            var before = registry.Inventory(owner, characterId).Offense;
            var version = registry.Advance(owner, characterId).Combat.Version;
            var equipA = registry.ApplyCommand(owner, characterId, "slot-a", version, new WebCommandRequest("equip", ItemId: itemA.Id));
            var equipB = registry.ApplyCommand(owner, characterId, "slot-b", equipA.State.Combat.Version, new WebCommandRequest("equip", ItemId: itemB.Id));
            Check(equipA.Applied && equipB.Applied, "m2 slots: both equip commands applied");
            var inventory = registry.Inventory(owner, characterId);
            Check(!inventory.Items.First(i => i.Id == itemA.Id).Equipped, "m2 slots: earlier item is unequipped");
            Check(inventory.Items.First(i => i.Id == itemB.Id).Equipped, "m2 slots: latest item stays equipped");
            Check(inventory.Offense >= before + offB - 0.001 && inventory.Offense < before + offA + offB + 0.001,
                "m2 slots: only one slot bonus is counted");

            // A portal has no equipment-slot attribute. Missing data must not silently
            // default to Head (slot 0) and displace the actual helm.
            var portalProduct = MerchantCatalog.Find(Guid.Parse("15900000-0000-0000-0000-000000000159"))!.Value;
            var portal = MerchantCatalog.CreateItem(portalProduct);
            store.GrantItems(owner, characterId, new List<SerializedItem> { portal });
            var portalView = registry.Inventory(owner, characterId).Items.Single(i => i.Id == portal.Id);
            Check(portalView.EquipSlot == -1 && !portalView.CanEquip
                && portalView.EquipReason == "not_equippable",
                "m2 equip: consumable portal is projected as non-equipment");
            var rejected = registry.ApplyCommand(owner, characterId, "slot-portal", equipB.State.Combat.Version,
                new WebCommandRequest("equip", ItemId: portal.Id));
            Check(!rejected.Applied && rejected.Reason == "not_equippable"
                && registry.Inventory(owner, characterId).Items.Single(i => i.Id == portal.Id).Slot == (int)ItemSlotTypes.Inventory,
                "m2 equip: server rejects non-equipment instead of putting it in the Head slot");
            var nativeHelmetDefinition = ItemCatalog.Definitions.First(d => d.Type == "HeavyHelmet");
            var nativeHelmet = new SerializedItem
            {
                Id = Guid.NewGuid(), Name = nativeHelmetDefinition.Name,
                DefinitionIntegerId = nativeHelmetDefinition.IntegerId, Slot = ItemSlotTypes.Inventory,
            };
            store.GrantItems(owner, characterId, new List<SerializedItem> { nativeHelmet });
            var nativeView = registry.Inventory(owner, characterId).Items.Single(i => i.Id == nativeHelmet.Id);
            Check(nativeView.EquipSlot == 0 && nativeView.CanEquip,
                "m2 equip: native helmet without web slot metadata remains equippable by definition");
            var nativeEquip = registry.ApplyCommand(owner, characterId, "slot-native-helmet", rejected.State.Combat.Version,
                new WebCommandRequest("equip", ItemId: nativeHelmet.Id));
            Check(nativeEquip.Applied && registry.Inventory(owner, characterId).Items.Single(i => i.Id == nativeHelmet.Id).Slot == (int)ItemSlotTypes.Head,
                "m2 equip: native helmet can actually be equipped in the Head slot");

            // Affixes are generated and exposed.
            var looted = inventory.Items.First(i => i.Id == itemB.Id);
            Check(looted.Affixes.Count > 0, "m2 affixes: items carry named affixes");
        }
        finally { Directory.Delete(dir, true); }
    }

    private static void LootPersistsEquipChangesStatsAndBossClears()
    {
        void Check(bool ok, string name) { if (!ok) throw new Exception(name); Console.WriteLine("PASS " + name); }
        var dir = Path.Combine(Path.GetTempPath(), "nord-m2-" + Guid.NewGuid());
        Directory.CreateDirectory(dir);
        try
        {
            using var store = new GameStore(dir);
            var clock = new FakeClock();
            var owner = store.GetOrCreateUser("device:m2-test").UserId;
            var data = Defaults.Create<SerializedCharacterData.SerializedData>();
            // The combat seed derives from the (random) character Guid, so give the character enough
            // offence that a boss is reliably cleared within the window regardless of the seed.
            data.CombatStats = new SerializedCharacterData.SerializedCombatStats { Offense = 2000, Defense = 50, Recovery = 5 };
            var characterId = store.CreateCharacter(owner, new CreateCharacterRequest
            {
                DisplayName = "m2",
                CharacterClass = CharacterClass.Warrior,
                CharacterRace = CharacterRace.Human,
                CharacterGameMode = GameMode.Normal,
                Data = new SerializedCharacterData { Data = data },
            }).CharacterId;

            var registry = new CombatRegistry(store, clock);

            // Run the dungeon long enough to drop loot and clear at least one boss. Drive the player
            // toward the nearest monster each step (the web client's auto-move) so the random layout
            // seed cannot leave both sides idle outside aggro range.
            var loot = new List<WebItemDetail>();
            for (var i = 0; i < 30; i++)
            {
                clock.Advance(TimeSpan.FromSeconds(5));
                var snapshot = registry.Advance(owner, characterId);
                var nearest = default(MonsterSnapshot);
                var bestDistance = double.MaxValue;
                foreach (var monster in snapshot.Combat.Monsters)
                {
                    if (!monster.Alive) continue;
                    var distance = (monster.X - snapshot.Combat.PlayerX) * (monster.X - snapshot.Combat.PlayerX)
                        + (monster.Z - snapshot.Combat.PlayerZ) * (monster.Z - snapshot.Combat.PlayerZ);
                    if (distance < bestDistance) { bestDistance = distance; nearest = monster; }
                }
                if (bestDistance < double.MaxValue)
                    registry.ApplyCommand(owner, characterId, $"mv-{i}", snapshot.Combat.Version,
                        new WebCommandRequest("move", X: nearest.X, Z: nearest.Z));
            }
            var afterCombat = registry.Advance(owner, characterId);
            var inventory = registry.Inventory(owner, characterId);
            loot.AddRange(inventory.Items);
            Check(inventory.Items.Count > 0, "m2 loot: kills produced persistent items");
            Check(afterCombat.Combat.DungeonsCleared >= 1, "m2 dungeon: boss cleared at least once");
            var worldProgress = store.GetWorldProgress(owner, characterId);
            var checkpoint = worldProgress.Waypoints.GetValueOrDefault(worldProgress.CurrentTier);
            Check(checkpoint.CurrentWaypoint >= 2 && checkpoint.MaxWaypoint >= 2,
                "W05: boss clear persists the completed checkpoint and unlocks the next waypoint");

            var equippedCandidate = inventory.Items.First(i => i.EquipSlot is >= 0 and <= 13);
            Check(!equippedCandidate.Equipped, "m2 equip: candidate starts unequipped");
            var beforeOffense = inventory.Offense;

            var equip = registry.ApplyCommand(owner, characterId, "eq-1", afterCombat.Combat.Version,
                new WebCommandRequest("equip", ItemId: equippedCandidate.Id));
            Check(equip.Applied && equip.Reason == "ok", "m2 equip: command applies");

            var afterEquip = registry.Inventory(owner, characterId);
            var equippedItem = afterEquip.Items.First(i => i.Id == equippedCandidate.Id);
            Check(equippedItem.Equipped, "m2 equip: item marked equipped");
            Check(afterEquip.Offense >= beforeOffense + equippedCandidate.Offense - 0.001,
                "m2 equip: offense gains the item bonus");

            var duplicate = registry.ApplyCommand(owner, characterId, "eq-1", afterCombat.Combat.Version,
                new WebCommandRequest("equip", ItemId: equippedCandidate.Id));
            Check(duplicate.Reason == equip.Reason && duplicate.Applied == equip.Applied,
                "m2 equip: retried command replays the original result");
            Check(Math.Abs(registry.Inventory(owner, characterId).Offense - afterEquip.Offense) < 0.001,
                "m2 equip: duplicate does not double-apply stats");

            var unequip = registry.ApplyCommand(owner, characterId, "eq-2", duplicate.State.Combat.Version,
                new WebCommandRequest("unequip", ItemId: equippedCandidate.Id));
            Check(unequip.Applied, "m2 equip: unequip applies");
            Check(registry.Inventory(owner, characterId).Offense < afterEquip.Offense, "m2 equip: unequip removes the bonus");

            // The equipment persists across a simulated restart.
            registry.Reset();
            registry.ApplyCommand(owner, characterId, "eq-3", 0, new WebCommandRequest("equip", ItemId: equippedCandidate.Id));
            registry.Reset();
            Check(registry.Inventory(owner, characterId).Items.First(i => i.Id == equippedCandidate.Id).Equipped,
                "m2 equip: equipped state survives restart");
        }
        finally { Directory.Delete(dir, true); }
    }

    private sealed class FakeClock : TimeProvider
    {
        private DateTimeOffset now = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => now;
        public void Advance(TimeSpan delta) => now += delta;
    }
}
