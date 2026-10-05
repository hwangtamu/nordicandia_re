using Game;
using Nordicandia.Server.State;
using Nordicandia.Server.WebApi;
using Nordicandia.Simulation;
using SharedNet.Api;
using SharedNet.Constants.Game;

static class MonsterSpawnRecoveryTests
{
    static void Check(bool value, string message)
    {
        if (!value) throw new Exception(message);
        Console.WriteLine("PASS " + message);
    }
    public static void Run()
    {
        Check((int)MonsterRarity.Champion == 1 && (int)MonsterRarity.Unique == 4,
            "spawn: actual contract enum Champion=1, Rare=2, Unique=4");
        int count = 0;
        double Zero() { count++; return 0; }
        Check(MonsterSpawnRules.RollRarity(9, 1, Zero) == 0 && count == 0,
            "spawn: level 9 tier 1 has no elite rolls");
        Check(MonsterSpawnRules.RollRarity(10, 1, Zero) == 1,
            "spawn: level 10 unlocks Champion");
        Check(MonsterSpawnRules.RollRarity(25, 1, Zero) == 2,
            "spawn: level 25 Rare precedes Champion");
        Check(MonsterSpawnRules.RollRarity(50, 1, Zero) == 4
            && MonsterSpawnRules.RollRarity(1, 2, Zero) == 4,
            "spawn: level 50 or tier >= 2 unlocks Unique");
        count = 0;
        Check(MonsterSpawnRules.RollRarity(100, 4, Zero, normalOnly: true) == 0 && count == 0,
            "spawn: normalOnly bypasses automatic rolls");
        Check(MonsterSpawnRules.RollRarity(1, 1, Zero, normalOnly: true, forceRarity: 2) == 2 && count == 0,
            "spawn: explicit rarity is retained when normalOnly bypasses rolls");
        Check(MonsterSpawnRules.RollRarity(1, 1, Zero, lastLevelAndFirstBossFight: true, canRollBoss: true) == 6,
            "spawn: last-level first-boss path takes priority");
        count = 0;
        Check(MonsterSpawnRules.RollRarity(100, 4, Zero, 0, 0, 0) == 0 && count == 0,
            "spawn: zero multipliers disable rolls without consuming RNG");
        Check(MonsterSpawnRules.RollRarity(10, 1, () => .04, champSpawnMult: 2) == 1
            && MonsterSpawnRules.RollRarity(10, 1, () => .040001, champSpawnMult: 2) == 0,
            "spawn: Champion multiplier doubles chance and exact boundary uses <=");
        var rolls = new Queue<double>(new[] { .006, .011, .019 });
        Check(MonsterSpawnRules.RollRarity(50, 2, () => rolls.Dequeue()) == 1 && rolls.Count == 0,
            "spawn: Unique then Rare then Champion are separate conditional draws");
        const int draws = 400000;
        var rng = new CombatRandom(91761); var histogram = new int[7];
        for (var i = 0; i < draws; i++) histogram[MonsterSpawnRules.RollRarity(50, 4, rng.NextDouble)]++;
        foreach (var (rarity, p) in new[] { (4,.005), (2,.995*.01), (1,.995*.99*.02), (0,.995*.99*.98) })
            Check(Math.Abs(histogram[rarity] - draws*p) < 6*Math.Sqrt(draws*p*(1-p)),
                $"spawn: seeded marginal rarity {rarity} = {histogram[rarity]}/{draws}, expected {p:P4}");
        var profiles = new[] {
            new MonsterProfile("NormalOnly", AvailableRarities: new[] { 0 }, SpawnGroup: "Beast"),
            new MonsterProfile("ChampionAllowed", AvailableRarities: new[] { 0, 1, 2 }, SpawnGroup: "Beast") };
        Check(MonsterSpawnRules.SelectProfile(profiles, 1, rng) is { Name: "ChampionAllowed", Rarity: 1, Champion: true },
            "spawn: definition eligibility filters the rolled rarity");
        Check(MonsterSpawnRules.SelectProfile(profiles, 4, rng) == null,
            "spawn: missing eligible definition does not downgrade or spawn a boss");
        Respawn();
        EmptySpawnPools();
        RealWorld();
    }
    static void EmptySpawnPools()
    {
        var profiles = new[] { new MonsterProfile("NormalOnly", AvailableRarities: new[] { 0 }, SpawnGroup: "Beast") };
        var blocked = new CombatantStats(1, 1000, 1, 1, UniqueMonsterFindBonus: 199);
        var normal = blocked with { UniqueMonsterFindBonus = -1, ChampionMonsterFindBonus = -1 };
        var instance = new CombatInstance(blocked, 0, 0, 0, 0, 123, monsterProfiles: profiles, currentWorldTier: 4);
        Check(instance.Monsters.Count == 0, "spawn: unsupported forced-probability Unique creates no fake normal monster");
        instance.UpdateStats(normal);
        instance.Advance(.1);
        Check(instance.Monsters.Count > 0 && instance.Monsters.Select(m => m.Index).Distinct().Count() == instance.Monsters.Count,
            "spawn: empty arena retries and entity indices stay unique");
        instance.SetWorld(profiles, packs: 2);
        Check(instance.Monsters.Count == 1, "spawn: pack starts with one eligible member");
        instance.UpdateStats(blocked);
        instance.Monsters[0].Alive = false;
        instance.Advance(.5);
        Check(instance.PacksRemaining == 1, "spawn: failed final pack members do not stall a cleared pack");
        for (var i = 0; i < 15; i++) instance.Advance(.5);
        Check(instance.PacksRemaining == 1 && instance.DungeonsCleared == 0,
            "spawn: wholly ineligible pack retries without granting completion rewards");
        instance.UpdateStats(normal);
        for (var i = 0; i < 15; i++) instance.Advance(.5);
        Check(instance.Monsters.Any(m => m.Alive), "spawn: empty pack recovers when eligible spawns become available");
    }
    static void Respawn()
    {
        var profiles = new[] { new MonsterProfile("AllRarities", AvailableRarities: new[] { 0, 1, 2, 4 }, SpawnGroup: "Beast") };
        var stats = new CombatantStats(1, 1000, 1, 1, ChampionMonsterFindBonus: 49, UniqueMonsterFindBonus: -1);
        var instance = new CombatInstance(stats, 0, 0, 0, 0, 193, monsterCount: 24, monsterProfiles: profiles, currentWorldTier: 4);
        Check(instance.Monsters.Any(m => m.Rarity == 1 && m.Champion && !m.IsBoss)
            && instance.Monsters.All(m => m.Rarity is 1 or 2),
            "spawn: actual tier-4 combat creates non-boss Champions using character multiplier");
        instance.UpdateStats(stats with { UniqueMonsterFindBonus = 199 });
        foreach (var monster in instance.Monsters) { monster.Alive = false; monster.RespawnTimer = 0; }
        instance.Advance(.1);
        Check(instance.Monsters.All(m => m.Rarity == 4 && !m.Champion && !m.IsBoss),
            "spawn: respawn rerolls rarity and observes updated attributes; Unique is not Champion");
        Check(instance.Snapshot().Monsters.All(m => m.Rarity == 4),
            "spawn: snapshots expose the runtime rarity to the browser");
    }
    static void RealWorld()
    {
        var dir = Path.Combine(Path.GetTempPath(), "nord-spawn-" + Guid.NewGuid());
        try
        {
            using var store = new GameStore(dir);
            var owner = store.GetOrCreateUser("device:spawn-recovery").UserId;
            var data = Defaults.Create<SerializedCharacterData.SerializedData>();
            data.Attributes = new SerializedAttributes { Values = new() { [AttributeOrigin.Character] = new()
            {
                [8] = new() { ValueD = 4 }, [9] = new() { ValueD = 4 },
                [12] = new() { ValueD = -1 }, [164] = new() { ValueD = 49 },
            } }, MultiplicativeValues = new() };
            var id = store.CreateCharacter(owner, new CreateCharacterRequest { DisplayName = "spawn",
                CharacterGameMode = GameMode.Normal, Data = new SerializedCharacterData { Data = data } }).CharacterId;
            var instance = new CombatRegistry(store).GetOrCreate(owner, id);
            Check(instance.IsTierFourOrHigher && instance.Monsters.Any(m => m.Rarity == 1 && !m.IsBoss),
                "spawn: persisted character -> ratings -> world roster -> non-boss Champion works end-to-end");
            Check(instance.Monsters.All(m => MonsterCatalog.ByName(m.Name)?.AvailableRarities.Contains(m.Rarity) == true),
                "spawn: all real-world definitions support their selected rarity");
            Check(instance.PlayerStatsSnapshot().ChampionMonsterFindBonus == 49,
                "spawn: character attribute 164 survives the full ratings path");
        }
        finally { if (Directory.Exists(dir)) Directory.Delete(dir, true); }
    }
}
