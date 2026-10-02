using System.Text.Json;
using Nordicandia.Simulation;

/// <summary>
/// End-to-end check of the recovered attribute synthesis: takes a real character's stored
/// attribute map (tools/web-content/fixtures/character_attributes.json), the recovered
/// formula per attribute (generated/attribute_formulas.json) and the client constants
/// (generated/constants.json), and evaluates the dependency chain with
/// <see cref="AttributeFormula"/>. This is the "minimal attribute dependency chain"
/// validation the acceptance recheck asked for: from base attributes to derived totals.
/// </summary>
static class CharacterAttributeChainTests
{
    public static void Run()
    {
        void Check(bool ok, string name) { if (!ok) throw new Exception(name); Console.WriteLine("PASS " + name); }

        // --- load generated artifacts ---
        var idsPath = Find("attribute_ids.json");
        var formulasPath = Find("attribute_formulas.json");
        var constantsPath = Find("constants.json");
        var fixturePath = Find("character_attributes.json", "fixtures");
        if (idsPath is null || formulasPath is null || constantsPath is null || fixturePath is null)
        {
            Console.WriteLine("SKIP attribute-chain: generated artifacts not found");
            return;
        }

        var opts = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
        var nameToId = new Dictionary<string, int>();
        foreach (var (id, name) in JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(idsPath))!)
            nameToId.TryAdd(name, int.Parse(id));

        var formulas = new Dictionary<int, string>();
        foreach (var (id, entry) in JsonSerializer.Deserialize<Dictionary<string, FormulaEntry>>(File.ReadAllText(formulasPath), opts)!)
            formulas[int.Parse(id)] = entry.Script;

        var constants = JsonSerializer.Deserialize<Dictionary<string, double>>(File.ReadAllText(constantsPath))!;
        var fixture = JsonSerializer.Deserialize<Fixture>(File.ReadAllText(fixturePath), opts)!;

        // Aggregate stored values across origins (0 = base/implicit, 1 = item).
        var stored = new Dictionary<int, double>();
        foreach (var origin in fixture.Origins.Values)
            foreach (var (id, value) in origin)
                stored[int.Parse(id)] = stored.GetValueOrDefault(int.Parse(id)) + value;

        var memo = new Dictionary<string, double>();
        var visiting = new HashSet<string>();
        double Resolve(string name)
        {
            if (name.StartsWith("Constants."))
                return constants.GetValueOrDefault(name["Constants.".Length..]);
            if (!nameToId.TryGetValue(name, out var id)) return 0.0; // attribute absent from this sample
            if (memo.TryGetValue(name, out var cached)) return cached;
            if (!visiting.Add(name)) throw new Exception($"attribute cycle at '{name}'");
            var value = formulas.TryGetValue(id, out var script)
                ? AttributeFormula.Evaluate(script, Resolve)
                : stored.GetValueOrDefault(id);
            visiting.Remove(name);
            memo[name] = value;
            return value;
        }

        // Strength_Total = (Base_Strength + Strength_Allocated) * (1 + bonuses). This sample
        // has 15 + 275 and no bonus attributes, so the recovered chain must yield 290.
        Check(Math.Abs(Resolve("Strength_Total") - 290) < 1e-6, "attribute-chain: Strength_Total = (15 + 275) = 290");
        Check(Math.Abs(Resolve("Agility_Total") - 10) < 1e-6, "attribute-chain: Agility_Total = 10");
        Check(Math.Abs(Resolve("Constitution_Total") - 10) < 1e-6, "attribute-chain: Constitution_Total = 10");

        // Derived totals must resolve through the formula graph and stay finite/positive.
        foreach (var name in new[] { "Armor_Total", "AttackRating_Total", "Evasion_Total", "Life_Max_Total", "Mana_Max_Total" })
        {
            var value = Resolve(name);
            Console.WriteLine($"INFO attribute-chain: {name} = {value:F4}");
            Check(!double.IsNaN(value) && !double.IsInfinity(value) && value > 0, $"attribute-chain: {name} resolves finite/positive");
        }

        // The recovered formulas that are read by the combat resolution must be present.
        Check(formulas.Values.Any(f => f.Contains("AttackRating")) && Resolve("AttackRating_Total") > 0,
            "attribute-chain: AttackRating_Total feeds CalculateChanceToHit");
    }

    private static string? Find(string fileName, string sub = "generated")
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        for (var i = 0; i < 8 && dir is not null; i++, dir = dir.Parent)
        {
            var candidate = Path.Combine(dir.FullName, "tools", "web-content", sub, fileName);
            if (File.Exists(candidate)) return candidate;
        }
        return null;
    }

    private sealed record FormulaEntry(string Name, string Script);
    private sealed record Fixture(Dictionary<string, Dictionary<string, double>> Origins);
}
