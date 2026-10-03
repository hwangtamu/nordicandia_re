using Nordicandia.Server.WebApi;
using Nordicandia.Simulation;

/// <summary>
/// W02/W03: every power a brain references resolves, and summon powers create real minions.
/// </summary>
static class W02W03BrainTests
{
    public static void Run()
    {
        void Check(bool ok, string message)
        {
            if (!ok) throw new Exception(message);
            Console.WriteLine("PASS " + message);
        }

        // Non-power actions executed directly by the brain state machine (CombatInstance.UpdateBrain).
        var utility = new HashSet<string>
        {
            "DefaultAttackProxy", "Flee", "MeleeWeaponSwing", "MinionReturnToMaster",
            "PetLoot", "RunOutOfCombat", "UniqueItemHuginnPull", "Wander",
        };

        var referenced = BrainCatalog.Brains.Values.SelectMany(b => b.Actions).Select(a => a.Power).Distinct().ToList();
        var missing = referenced.Where(p => MonsterPowerCatalog.For(p) is null && !utility.Contains(p)).ToList();
        Check(missing.Count == 0,
            $"W02/W03: every brain power resolves to a catalog entry or a known action ({missing.Count} missing)");
        Check(referenced.Any(p => MonsterPowerCatalog.For(p) is not null),
            "W03: brains reference recovered monster powers");

        // Summon powers must name a real gamedata minion.
        var summons = MonsterPowerCatalog.All.Values
            .Where(p => p.Kind == MonsterPowerKind.Summon && p.Minion.Length > 0).ToList();
        Check(summons.Count > 0, "W03: summon powers carry a minion name");
        var badMinions = summons.Where(p => MonsterCatalog.ByName(p.Minion) is null).Select(p => p.Minion).ToList();
        Check(badMinions.Count == 0, $"W03: every summon minion exists in the monster roster ({string.Join(",", badMinions)})");
    }
}
