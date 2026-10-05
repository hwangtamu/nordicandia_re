using Nordicandia.Server.WebApi;
using Nordicandia.Simulation;

/// <summary>
/// W01: the embedded monster roster exposes the client's combat-relevant fields.
/// </summary>
static class W01MonsterCatalogTests
{
    private static void Check(bool ok, string message)
    {
        if (!ok) throw new Exception(message);
        Console.WriteLine("PASS " + message);
    }

    public static void Run()
    {
        Check(MonsterCatalog.Count == 136, $"W01: full client monster roster loaded ({MonsterCatalog.Count}, expected 136)");
        Check(MonsterCatalog.ByName("CasterDemon1") is { Caster: true, DamageType: 2 },
            "W01: CasterDemon1 carries its caster flag and damage type");
        Check(MonsterCatalog.ByName("Bat") is { TypeName: "Beast" } bat && bat.AvailableRarities.Count == 3,
            "W01: Bat is a Beast with three spawn rarities");
        Check(MonsterCatalog.ByName("StoneGolem") is { Size: 256 } && MonsterCatalog.ByName("Boss_WolfKing") is { Size: 199 },
            "W01: golems/bosses carry their Size");
        Check(MonsterCatalog.Entries.Count(m => m.Caster) == 38 && MonsterCatalog.Entries.Count(m => m.Ranged) == 6,
            "W01: caster/ranged monster counts match the data (38 / 6)");

        // Damage type: Game.DamageType ids Physical=0 Fire=1 Cold=2 Lightning=3 Poison=4.
        Check(MonsterCatalog.DamageTypeName(4) == "Poison" && MonsterCatalog.DamageTypeName(2) == "Cold",
            "W01: DamageType ids map to the client element names");
        Check(MonsterCatalog.DamageBundle("CasterDemon1") == new DamageBundle(Cold: 1),
            "W01: CasterDemon1 deals cold (DamageType 2), not the hand-set fire");
        Check(MonsterCatalog.DamageBundle("Bat") == new DamageBundle(Physical: 1),
            "W01: a monster without an explicit DamageType deals physical");

        // World spawn pools group spawnable monsters by type.
        Check(MonsterCatalog.ByType("Beast").Count() >= 5 && MonsterCatalog.ByType("Undead").Any(),
            "W01: spawnable monsters are grouped by type (Beast/Undead) for world pools");
        var worlds = WorldCatalog.Entries.Where(w => w.Tier is > 0).ToList();
        Check(worlds.Count == 35 && worlds.All(w => !string.IsNullOrEmpty(w.BossName)
            && MonsterCatalog.ByName(w.BossName) is not null),
            "W03: each tiered world resolves its BossMonsterId to a catalogued monster");
        SpawnWeights();
    }

    private static void SpawnWeights()
    {
        var profiles = new[]
        {
            new MonsterProfile("CommonType", SpawnWeight: 9),
            new MonsterProfile("RareType", SpawnWeight: 1),
        };
        var stats = new CombatantStats(100, 100, 0, 1,
            AttackRating: 1e12, CritChance: 0, Damage: new DamageBundle(Physical: 100));
        var common = 0;
        var rare = 0;
        for (ulong seed = 1; seed <= 200; seed++)
        {
            var instance = new CombatInstance(stats, 0, 0, 0, 0, seed,
                monsterCount: 24, monsterProfiles: profiles);
            common += instance.Monsters.Count(m => m.Name == "CommonType");
            rare += instance.Monsters.Count(m => m.Name == "RareType");
        }
        var rareShare = rare / (double)(common + rare);
        Check(Math.Abs(rareShare - 0.1) < 0.025,
            $"W01: spawn profiles honor relative weights over deterministic rolls ({rareShare:P1} expected 10%)");
    }
}
