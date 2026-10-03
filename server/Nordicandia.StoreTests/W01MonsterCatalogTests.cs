using Nordicandia.Server.WebApi;

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
    }
}
