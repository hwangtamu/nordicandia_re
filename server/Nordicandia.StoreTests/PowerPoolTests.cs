using Nordicandia.Server.WebApi;

static class PowerPoolTests
{
    public static void Run()
    {
        void Check(bool ok, string message)
        {
            if (!ok) throw new Exception(message);
            Console.WriteLine("PASS " + message);
        }
        Check(PowerCatalog.MaxActiveSkills == 6 && PowerCatalog.MaxPassiveSkills == 3,
            "power pools: client slot caps are 6 active / 3 passive");

        var classes = new[] { (0, "Warrior"), (4, "Hunter"), (5, "Mage"), (6, "Necromancer") };
        foreach (var (classId, name) in classes)
        {
            var pool = PowerCatalog.PoolFor(classId);
            Check(pool.Active.Count >= PowerCatalog.MaxActiveSkills && pool.Passive.Count >= PowerCatalog.MaxPassiveSkills,
                $"power pools: {name} offers at least 6 active / 3 passive ({pool.Active.Count}/{pool.Passive.Count})");
            Check(pool.Active.All(p => p.Id != Guid.Empty && !string.IsNullOrEmpty(p.Name) && p.IntegerId >= 0) &&
                pool.Passive.All(p => p.Id != Guid.Empty && !string.IsNullOrEmpty(p.Name) && p.IntegerId >= 0),
                $"power pools: {name} entries carry id/name/integerId");
        }

        // The 19 recovered initializers' passives are selectable in the class pools they belong to.
        void Has(string className, string passive)
        {
            var classId = className switch { "Warrior" => 0, "Hunter" => 4, "Mage" => 5, "Necromancer" => 6, _ => -1 };
            var entry = PowerCatalog.PoolFor(classId).Passive.FirstOrDefault(p => p.Name == passive);
            Check(entry.Name == passive && PowerCatalog.TryGetPooled(entry.Id, out var resolved) && resolved.Name == passive,
                $"power pools: {className} can select {passive}");
        }
        Has("Warrior", "TreasureHunter");
        Has("Hunter", "DeadlyPoison");
        Has("Mage", "FireArmor");
        Has("Necromancer", "MasterSummoner");
    }
}
