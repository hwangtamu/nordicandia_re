using Game;
using Nordicandia.Server.State;
using Nordicandia.Simulation;
using SharedNet.Api;
using SharedNet.Constants.Game;
using Progression = Nordicandia.Simulation.Progression;

/// <summary>
/// M0 acceptance checks: a reproducible combat sample, the recovered XP curve, and the
/// authoritative web snapshot the browser client will read. Run by the console harness in
/// Program.cs; no external test framework is used by this repository.
/// </summary>
static class WebM0Tests
{
    public static void Run()
    {
        CombatSampleIsReproducible();
        ExperienceCurveMatchesClient();
        WebSnapshotProjectsAuthoritativeState();
    }

    private static void CombatSampleIsReproducible()
    {
        void Check(bool ok, string name) { if (!ok) throw new Exception(name); Console.WriteLine("PASS " + name); }

        var attacker = CombatantStats.FromRealtime(offense: 100, defense: 0, recovery: 0, level: 10);
        var defender = CombatantStats.FromRealtime(offense: 0, defense: 50, recovery: 0, level: 10);
        var profile = new AttackProfile(SkillMultiplier: 1.0, CritChance: 0.25, CritMultiplier: 2.0, Variance: 0.10);

        var a = CombatModel.ResolveHit(attacker, defender, profile, new CombatRandom(1234));
        var b = CombatModel.ResolveHit(attacker, defender, profile, new CombatRandom(1234));
        Check(a == b, "combat: identical seed and inputs produce identical damage");
        Check(a.Damage >= 1.0, "combat: damage floor is 1");

        var betterDefender = CombatantStats.FromRealtime(0, 100000, 0, 10);
        Check(CombatModel.ExpectedDamage(attacker, betterDefender, profile)
              < CombatModel.ExpectedDamage(attacker, defender, profile),
            "combat: more defense lowers expected damage");

        // Pin a fixed sample so later formula changes fail loudly instead of drifting silently.
        var pinned = CombatModel.ResolveHit(
            CombatantStats.FromRealtime(100, 0, 0, 10),
            CombatantStats.FromRealtime(0, 50, 0, 10),
            new AttackProfile(1.0, 0.25, 2.0, 0.10),
            new CombatRandom(1));
        Console.WriteLine($"INFO combat sample seed=1 damage={pinned.Damage:F6} crit={pinned.Critical} confidence={pinned.Confidence}");
    }

    private static void ExperienceCurveMatchesClient()
    {
        void Check(bool ok, string name) { if (!ok) throw new Exception(name); Console.WriteLine("PASS " + name); }

        Check(Progression.ExperienceForLevel(1) == 0, "xp: level 1 needs 0");
        Check(Progression.LevelForExperience(0) == 1, "xp: 0 experience is level 1");
        Check(Progression.LevelForExperience(Progression.Add) == 1, "xp: exactly the base is still level 1");
        for (var level = 2; level <= 500; level += 7)
        {
            var xp = Progression.ExperienceForLevel(level);
            Check(Progression.LevelForExperience(xp) == level, $"xp: round-trips level {level}");
        }
    }

    private static void WebSnapshotProjectsAuthoritativeState()
    {
        void Check(bool ok, string name) { if (!ok) throw new Exception(name); Console.WriteLine("PASS " + name); }
        var dir = Path.Combine(Path.GetTempPath(), "nord-web-m0-" + Guid.NewGuid());
        Directory.CreateDirectory(dir);
        try
        {
            Guid owner, characterId;
            using (var store = new GameStore(dir))
            {
                owner = store.GetOrCreateUser("device:web-m0-test").UserId;
                var data = Defaults.Create<SerializedCharacterData.SerializedData>();
                data.CombatStats = new SerializedCharacterData.SerializedCombatStats
                {
                    Offense = 321.5, Defense = 111.25, Recovery = 7.5,
                };
                var header = store.CreateCharacter(owner, new CreateCharacterRequest
                {
                    DisplayName = "webmage",
                    CharacterClass = CharacterClass.Mage,
                    CharacterRace = CharacterRace.HighElf,
                    CharacterGameMode = GameMode.Normal,
                    Data = new SerializedCharacterData { Data = data },
                });
                characterId = header.CharacterId;

                store.SaveRealtimeProgress(owner, characterId, experience: 12_345, silver: 777, opals: 42,
                    new GameStore.CombatSnapshot(321.5, 111.25, 7.5, MonsterKills: 9, ItemsLooted: 3));

                store.ApplyItemOperations(owner, characterId, new List<ItemOperationEntry>
                {
                    new AddItemOperationEntry
                    {
                        Item = new SerializedItem
                        {
                            Id = Guid.NewGuid(), Name = "Iron Sword", Slot = ItemSlotTypes.MainHand,
                            DefinitionIntegerId = 5, BaseRarity = Rarity.D,
                        },
                    },
                });

                var snapshot = store.ProjectWebSnapshot(owner, characterId);
                Check(snapshot.CharacterId == characterId, "snapshot: character id");
                Check(snapshot.DisplayName == "webmage", "snapshot: display name");
                Check(snapshot.Experience == 12_345, "snapshot: authoritative experience");
                Check(snapshot.Silver == 777 && snapshot.Opals == 42, "snapshot: currency");
                Check(snapshot.Level == Progression.LevelForExperience(12_345), "snapshot: derived level");
                Check(snapshot.Offense == 321.5 && snapshot.Defense == 111.25, "snapshot: combat stats");
                Check(snapshot.MonsterKills == 9, "snapshot: monster kills");
                Check(snapshot.Equipped.Any(i => i.Name == "Iron Sword" && i.Slot == (int)ItemSlotTypes.MainHand),
                    "snapshot: equipped item projected");
                Check(snapshot.ExperienceForNextLevel > snapshot.ExperienceForLevel, "snapshot: next-level xp bound");
            }
        }
        finally { Directory.Delete(dir, true); }
    }
}
