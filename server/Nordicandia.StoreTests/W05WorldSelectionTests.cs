using Game;
using Nordicandia.Server.State;
using Nordicandia.Server.WebApi;
using Nordicandia.Simulation;
using SharedNet.Api;
using SharedNet.Constants.Game;
using System.Reflection;

static class W05WorldSelectionTests
{
    private static void Check(bool value, string name)
    {
        if (!value) throw new Exception(name);
        Console.WriteLine("PASS " + name);
    }

    public static void Run()
    {
        var directory = Path.Combine(Path.GetTempPath(), "nord-w05-worlds-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            Guid owner;
            Guid characterId;
            using (var store = new GameStore(directory))
            {
                owner = store.GetOrCreateUser("device:w05-worlds").UserId;
                characterId = store.CreateCharacter(owner, new CreateCharacterRequest
                {
                    DisplayName = "worlds",
                    CharacterGameMode = GameMode.Normal,
                    CharacterClass = CharacterClass.Warrior,
                    CharacterRace = CharacterRace.Human,
                    Data = new SerializedCharacterData { Data = Defaults.Create<SerializedCharacterData.SerializedData>() },
                }).CharacterId;

                var registry = new CombatRegistry(store, new FrozenClock());
                var initial = registry.WorldProgress(owner, characterId);
                Check(initial.CurrentTier == 1 && initial.UnlockedTier == 1
                    && initial.Worlds.First(w => w.Tier == 1) is { Unlocked: true, CurrentWaypoint: 1, MaxWaypoint: 1 },
                    "world selection: fresh web character starts at first unlocked world and first checkpoint");
                var town = registry.EnterTown(owner, characterId);
                var townInstance = registry.GetOrCreate(owner, characterId);
                Check(town.Combat.InTown && town.Combat.Monsters.Count == 0
                    && townInstance.UseSkill(0).Reason == "in_town",
                    "town: entering creates a safe hub with no hostile monsters or skill casts");
                registry.Reset();
                Check(registry.TownState(owner, characterId).InTown
                    && registry.GetOrCreate(owner, characterId).Snapshot().InTown,
                    "town: safe-zone state survives registry recreation");
                var leaveTown = registry.LeaveTown(owner, characterId);
                Check(!leaveTown.Combat.InTown && leaveTown.Combat.Monsters.Count > 0,
                    "town: return-to-world restores the selected tier and its monster roster");
                var leaveAgain = registry.LeaveTown(owner, characterId);
                Check(!leaveAgain.Combat.InTown && leaveAgain.Combat.Version == leaveTown.Combat.Version,
                    "town: repeated return clicks do not re-enter or reset the world");
                var locked = registry.SelectWorld(owner, characterId, 2);
                Check(!locked.Applied && locked.Reason == "world_locked",
                    "world selection: locked tier cannot be entered");

                store.ApplyWorldProgress(owner, characterId, 2, 3, TimeSpan.FromSeconds(50));
                var unlocked = registry.WorldProgress(owner, characterId);
                Check(unlocked.UnlockedTier == 2 && unlocked.Worlds.First(w => w.Tier == 2).CurrentWaypoint == 3
                    && unlocked.Worlds.First(w => w.Tier == 2).MaxWaypoint == 4,
                    "world selection: native world unlock/checkpoint bounds are exposed");
                var lockedWaypoint = registry.SelectWorld(owner, characterId, 2, 5);
                Check(!lockedWaypoint.Applied && lockedWaypoint.Reason == "checkpoint_locked",
                    "world selection: checkpoint beyond MaxWaypoint is rejected");
                var selected = registry.SelectWorld(owner, characterId, 2, waypoint: 2);
                Check(selected.Applied && selected.State.Map is not null
                    && selected.State.Combat.Monsters.Count > 0,
                    "world selection: selecting an unlocked tier/checkpoint resets the authoritative world and map");
                var selectedProgress = registry.WorldProgress(owner, characterId);
                Check(selectedProgress.CurrentTier == 2
                    && selectedProgress.Worlds.First(w => w.Tier == 2).CurrentWaypoint == 2,
                    "world selection: selected tier and checkpoint persist to character data");
                var instance = registry.GetOrCreate(owner, characterId);
                var createBoss = typeof(CombatInstance).GetMethod("CreateBoss", BindingFlags.Instance | BindingFlags.NonPublic)
                    ?? throw new MissingMethodException(nameof(CombatInstance), "CreateBoss");
                var spawnedBoss = (CombatMonster)createBoss.Invoke(instance, null)!;
                Check(spawnedBoss.Name == WorldCatalog.ForTier(2)?.BossName
                    && spawnedBoss.Brain == MonsterCatalog.ByName(spawnedBoss.Name)?.BrainName,
                    "world selection: boss factory uses the selected world's catalogued boss and brain");
                Check(!instance.IsTierFourOrHigher,
                    "world selection: tier-gated monster brain conditions are false below tier 4");
                store.ApplyWorldProgress(owner, characterId, 4, 1, TimeSpan.FromSeconds(40));
                var tierFour = registry.SelectWorld(owner, characterId, 4);
                var tierFourInstance = registry.GetOrCreate(owner, characterId);
                Check(tierFour.Applied && tierFourInstance.CurrentWorldTier == 4
                    && tierFourInstance.IsTierFourOrHigher,
                    "world selection: selecting tier 4 enables recovered tier-gated brain conditions");
                registry.Reset();
                Check(registry.WorldProgress(owner, characterId).CurrentTier == 4,
                    "world selection: selected tier survives a registry restart");
            }
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, true);
        }
    }

    private sealed class FrozenClock : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => new(2026, 10, 3, 12, 0, 0, TimeSpan.Zero);
    }
}
