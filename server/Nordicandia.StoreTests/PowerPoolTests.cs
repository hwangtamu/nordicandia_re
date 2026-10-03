using Nordicandia.Server.WebApi;
using Nordicandia.Simulation;

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

        // Loadout: 6 active (re-slotted) + 3 passive, caps enforced, unknown names dropped.
        var warrior = PowerCatalog.PoolByClass[0];
        var built = PowerCatalog.BuildPool(0,
            warrior.Active.Take(6).Select(a => a.Name).ToList(),
            warrior.Passive.Take(3).Select(p => p.Name).ToList());
        Check(built.Active.Count == 6 && built.Active.Select(s => s.Slot).SequenceEqual(Enumerable.Range(0, 6)) &&
            built.Passive.Count == 3,
            "power pools: BuildPool equips 6 active (re-slotted 0..5) + 3 passive");
        var over = PowerCatalog.BuildPool(0,
            warrior.Active.Take(9).Select(a => a.Name).ToList(),
            warrior.Passive.Take(6).Select(p => p.Name).ToList());
        Check(over.Active.Count == 6 && over.Passive.Count == 3, "power pools: BuildPool caps at 6 active / 3 passive");
        var unknown = PowerCatalog.BuildPool(0,
            new List<string> { "Slam", "NotAPower", "Pounce" }, new List<string> { "Nope" });
        Check(unknown.Active.Select(s => s.Name).SequenceEqual(new[] { "Slam", "Pounce" }) &&
            unknown.Passive.Count == 1 && unknown.Passive[0].Name == PowerCatalog.DefaultPoolFor(0).Passive[0].Name,
            "power pools: BuildPool drops unknown names and falls back on passives");
        Check(PowerCatalog.BuildPool(0, new List<string>(), new List<string>()).Active.Count ==
            PowerCatalog.ForClass(0).Active.Count,
            "power pools: BuildPool empty selection falls back to the starter kit");

        // CombatInstance consumes the full pool: six skills and summed passive bonuses.
        var stats = new CombatantStats(100, 20, 10, 1);
        var loaded = new CombatInstance(stats, 0, 0, 0, 0, seed: 5, classPowers: built);
        Check(loaded.Snapshot().Skills.Count == 6, "power pools: combat exposes six equipped active skills");
        var single = new CombatInstance(stats, 0, 0, 0, 0, seed: 5, classPowers: PowerCatalog.DefaultPoolFor(0));
        Check(loaded.PlayerMaxHp >= single.PlayerMaxHp,
            "power pools: equipped passives contribute without reducing the health pool");
    }
}
