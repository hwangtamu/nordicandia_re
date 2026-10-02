using System.Text.Json;
using Nordicandia.Simulation;

/// <summary>
/// End-to-end check of the recovered attribute synthesis using the production
/// <see cref="CharacterAttributeEngine"/>. Takes a real character's stored attribute map
/// (tools/web-content/fixtures/character_attributes.json) and evaluates the formulas'
/// dependency chain: base attributes -> scripted totals -> combat ratings.
/// </summary>
static class CharacterAttributeChainTests
{
    public static void Run()
    {
        void Check(bool ok, string name) { if (!ok) throw new Exception(name); Console.WriteLine("PASS " + name); }

        var fixturePath = Find("character_attributes.json", "fixtures");
        if (fixturePath is null)
        {
            Console.WriteLine("SKIP attribute-chain: fixture not found");
            return;
        }

        var fixture = JsonSerializer.Deserialize<Fixture>(File.ReadAllText(fixturePath),
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true })!;

        // Aggregate stored values across origins (0 = base/implicit, 1 = item).
        var stored = new Dictionary<int, double>();
        foreach (var origin in fixture.Origins.Values)
            foreach (var (id, value) in origin)
                stored[int.Parse(id)] = stored.GetValueOrDefault(int.Parse(id)) + value;

        var engine = CharacterAttributeEngine.Instance;
        Check(engine.FormulaCount >= 226, $"attribute-engine: loaded {engine.FormulaCount} scripted formulas");

        var eval = engine.Evaluate(stored);
        Check(Math.Abs(eval.Strength - 290) < 1e-6, "attribute-engine: Strength_Total = (15 + 275) = 290");
        Check(Math.Abs(eval.Agility - 10) < 1e-6, "attribute-engine: Agility_Total = 10");
        Check(Math.Abs(eval.Constitution - 10) < 1e-6, "attribute-engine: Constitution_Total = 10");

        foreach (var (name, value) in new[]
        {
            ("AttackRating", eval.AttackRating), ("Armor", eval.Armor), ("Evasion", eval.Evasion),
            ("CritChanceMainHand", eval.CritChanceMainHand), ("LifeMax", eval.LifeMax), ("ManaMax", eval.ManaMax),
        })
        {
            Console.WriteLine($"INFO attribute-engine: {name} = {value:F4}");
            Check(!double.IsNaN(value) && !double.IsInfinity(value) && value > 0,
                $"attribute-engine: {name} resolves finite/positive");
        }

        // The sample equips a weapon, so the recovered average weapon damage must be non-zero.
        var weapon = eval.WeaponDamage;
        Console.WriteLine($"INFO attribute-engine: weapon total={weapon.Total:F2} physical={weapon.Physical:F2} fire={weapon.Fire:F2} " +
            $"cold={weapon.Cold:F2} lightning={weapon.Lightning:F2} poison={weapon.Poison:F2}");
        Check(weapon.Total > 0, "attribute-engine: weapon damage resolves from equipped item attributes");
        var res = eval.Resistances;
        Check(res.Fire <= 0.95 + 1e-9 && res.Cold <= 0.95 + 1e-9 && res.Lightning <= 0.95 + 1e-9 && res.Poison <= 0.95 + 1e-9,
            "attribute-engine: elemental resistances respect Resistance_Max_Total (<=0.95)");
    }

    private static string? Find(string fileName, string sub)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        for (var i = 0; i < 8 && dir is not null; i++, dir = dir.Parent)
        {
            var candidate = Path.Combine(dir.FullName, "tools", "web-content", sub, fileName);
            if (File.Exists(candidate)) return candidate;
        }
        return null;
    }

    private sealed record Fixture(Dictionary<string, Dictionary<string, double>> Origins);
}
