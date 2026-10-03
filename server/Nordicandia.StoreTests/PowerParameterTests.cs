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

        // Recovered passives resolve to client attribute ids for the loadout bonus map.
        void Bonus(string power, int attributeId, double expected)
        {
            var bonuses = PowerParameterCatalog.AttributeBonuses(power);
            Check(bonuses.TryGetValue(attributeId, out var value) && Math.Abs(value - expected) < 1e-9,
                $"power attributes: {power} -> {attributeId} = {expected}");
        }
        Bonus("FireArmor", 1000, 0.5);       // Resistance_Fire
        Bonus("ColdArmor", 1011, 0.5);       // Resistance_Cold
        Bonus("LightningArmor", 1012, 0.5);  // Resistance_Lightning
        Bonus("DeadlyPoison", 428, 0.15);    // Poison_Chance_On_Hit
        Bonus("DeadlyPoison", 429, 0.80);    // Double_Damage_..._Poisoned_Target
        Bonus("Solitary", 427, 0.10);        // Power_More_Weapon_Damage_...
        Bonus("Fork", 431, 0.10);            // Projectile_Auto_Attacks_Fork_Chance
        Bonus("TreasureHunter", 355, 0.30);  // Base_Magic_Find
        Bonus("TreasureHunter", 367, 0.10);  // Item_Quantity_Bonus_Percent
        Bonus("RepelMagic", 1019, 0.03);     // Resistance_Max_Bonus
        Bonus("RepelMagic", 271, 0.03);      // Base_Physical_Damage_Reduction_Bonus
        Bonus("MasterSummoner", 734, 0.10);  // Minion_Inheritance_Life_Bonus_Percent
        Bonus("MasterSummoner", 738, 0.05);  // Minion_Inheritance_Weapon_Damage_Bonus_Percent
        Bonus("VileTouch", 428, 0.60);       // _PoisonChanceOnHit
        Bonus("VileTouch", 1704, 0.20);      // _MorePoisonDamage -> Weapon_Poison_Damage_Bonus_Percent
        Bonus("ShootExplodingFireArrow", 230, 0.0); // Power_Projectile_Pierce_Chance
        Bonus("ShootExplodingFireArrow", 432, 0.0); // Power_Projectile_Fork_Chance

        // Rank scaling and the un-wired damage-conversion entry (not zero-invented).
        Check(Math.Abs(PowerParameterCatalog.AttributeBonuses("FireArmor", 3)[1000] - 0.7) < 1e-9,
            "power attributes: rank 3 FireArmor resistance = 0.5 + 0.1*2");
        Check(!PowerParameterCatalog.AttributeBonuses("FireArmor").ContainsKey(575),
            "power attributes: un-wired damage conversion is not invented");

        // BuildPool attaches the recovered bonuses to the selected passive.
        var magePool = PowerCatalog.BuildPool(5, new[] { "IceNova" }, new[] { "FireArmor" });
        Check(magePool.Passive[0].AttributeBonuses?.GetValueOrDefault(1000) == 0.5,
            "power attributes: BuildPool attaches FireArmor's resistance bonus");

        // The attribute engine turns the bonus into the resistance total.
        var eval = Nordicandia.Simulation.CharacterAttributeEngine.Instance.Evaluate(
            new Dictionary<int, double> { [1000] = 0.5 });
        var fireTotal = eval.Resolve("Resistance_Fire_Total");
        Check(fireTotal >= 0.49,
            $"power attributes: Resistance_Fire bonus flows into Resistance_Fire_Total ({fireTotal:F3})");

        // With the character default Resistance_Max=0.75 (seeded by CharacterBaseline) the bonus
        // survives capping, unlike the 0 default which floors every resistance at 0.
        var cappedEval = Nordicandia.Simulation.CharacterAttributeEngine.Instance.Evaluate(
            new Dictionary<int, double> { [1000] = 0.5, [1001] = 0.75, [411] = 0.75 });
        Check(Math.Abs(cappedEval.Resistances.Fire - 0.5) < 1e-6,
            $"power attributes: Resistance_Fire=0.5 survives the 0.75 cap ({cappedEval.Resistances.Fire:F3})");
    }
}
