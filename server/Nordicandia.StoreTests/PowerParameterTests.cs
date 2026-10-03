using Nordicandia.Server.WebApi;

static class PowerParameterTests
{
    public static void Run()
    {
        void Check(bool ok, string message)
        {
            if (!ok) throw new Exception(message);
            Console.WriteLine("PASS " + message);
        }
        double E(string power, string parameter, int rank)
            => PowerParameterCatalog.Evaluate(PowerParameterCatalog.ForPower(power)[parameter], rank);

        Check(PowerParameterCatalog.Powers.Count == 19,
            $"power parameters: all 19 recovered initializers are present ({PowerParameterCatalog.Powers.Count})");

        // TreasureHunter: MagicFindIncrease = 0.30 + 0.02*(r-1); ItemQuantityIncrease = 0.10 + 0.01*(r-1).
        Check(Math.Abs(E("TreasureHunter", "_MagicFindIncrease", 1) - 0.30) < 1e-9 &&
            Math.Abs(E("TreasureHunter", "_MagicFindIncrease", 11) - 0.50) < 1e-9 &&
            Math.Abs(E("TreasureHunter", "_ItemQuantityIncrease", 6) - 0.15) < 1e-9,
            "power parameters: TreasureHunter rank formulas");

        // DeadlyPoison probability attributes cap at 1.0.
        Check(Math.Abs(E("DeadlyPoison", "Poison_Chance_On_Hit", 1) - 0.15) < 1e-9 &&
            Math.Abs(E("DeadlyPoison", "Double_Damage_Chance_On_Crit_On_Poisoned_Target", 1) - 0.80) < 1e-9 &&
            Math.Abs(E("DeadlyPoison", "Poison_Chance_On_Hit", 1000) - 1.0) < 1e-9,
            "power parameters: DeadlyPoison rank formulas with capMax");

        // Solitary / Fork shared shape: 0.10 + 0.005*(r-1).
        Check(Math.Abs(E("Solitary", "Power_More_Weapon_Damage_When_No_Wolf_Is_Active", 21) - 0.20) < 1e-9 &&
            Math.Abs(E("Fork", "Projectile_Auto_Attacks_Fork_Chance", 2) - 0.105) < 1e-9,
            "power parameters: Solitary/Fork linear rank");

        // Constants are kept as constants; expression/runtime inputs are not zero-filled.
        Check(Math.Abs(E("ManaArrowsAbility", "Power_Num_Projectiles", 5) - 1.0) < 1e-9 &&
            Math.Abs(E("ShootExplodingFireArrow", "Power_Projectile_Pierce_Chance", 3) - 0.0) < 1e-9,
            "power parameters: recovered constants");
        Check(!PowerParameterCatalog.ForPower("ShootExplodingFireArrow")["Power_Projectile_Speed"].IsScalar &&
            !PowerParameterCatalog.ForPower("Wander").Values.Any(p => p.IsScalar) &&
            PowerParameterCatalog.EvaluatePower("Whirl", 5).Count == 0,
            "power parameters: runtime/expression/inherited entries stay non-scalar (not invented)");

        Check(PowerParameterCatalog.EvaluatePower("TreasureHunter", 1)
                .OrderBy(kv => kv.Key).Select(kv => kv.Value).SequenceEqual(new[] { 0.10, 0.30 }),
            "power parameters: EvaluatePower returns scalar entries only");
    }
}
