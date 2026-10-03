using Nordicandia.Server.WebApi;
using Nordicandia.Simulation;

/// <summary>
/// W01: the embedded monster roster exposes the client's combat-relevant fields.
/// </summary>
static class W01MonsterCatalogTests
{
    public static void Run()
    {
        void Check(bool ok, string message)
        {
            if (!ok) throw new Exception(message);
            Console.WriteLine("PASS " + message);
        }

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
    }
}
