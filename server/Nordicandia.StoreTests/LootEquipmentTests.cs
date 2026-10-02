using Game;
using Nordicandia.Server.State;
using Nordicandia.Server.WebApi;
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
            data.CombatStats = new SerializedCharacterData.SerializedCombatStats { Offense = 200, Defense = 50, Recovery = 5 };
            var characterId = store.CreateCharacter(owner, new CreateCharacterRequest
            {
                DisplayName = "m2",
                CharacterClass = CharacterClass.Warrior,
                CharacterRace = CharacterRace.Human,
                CharacterGameMode = GameMode.Normal,
                Data = new SerializedCharacterData { Data = data },
            }).CharacterId;

            var registry = new CombatRegistry(store, clock);

            // Run the dungeon long enough to drop loot and clear at least one boss.
            var loot = new List<WebItemDetail>();
            for (var i = 0; i < 12; i++)
            {
                clock.Advance(TimeSpan.FromSeconds(5));
                registry.Advance(owner, characterId);
            }
            var afterCombat = registry.Advance(owner, characterId);
            var inventory = registry.Inventory(owner, characterId);
            loot.AddRange(inventory.Items);
            Check(inventory.Items.Count > 0, "m2 loot: kills produced persistent items");
            Check(afterCombat.Combat.DungeonsCleared >= 1, "m2 dungeon: boss cleared at least once");

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
            Check(duplicate.Reason == "duplicate", "m2 equip: retried command is idempotent");
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
