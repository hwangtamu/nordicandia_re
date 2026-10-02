using System.Text.Json;
using Nordicandia.Simulation;

/// <summary>
/// Verifies the scripted-attribute formula evaluator against the extracted client formulas
/// (tools/web-content/generated/attribute_formulas.json). The formulas are the recovered
/// client synthesis expressions; this checks the parser handles the real syntax and the
/// helpers (Pin/Min/Pow/Constants/MultiplyWith/ternary) compute correctly.
/// </summary>
static class AttributeFormulaTests
{
    public static void Run()
    {
        void Check(bool ok, string name) { if (!ok) throw new Exception(name); Console.WriteLine("PASS " + name); }

        // Arithmetic and precedence.
        var r = Names();
        Check(Math.Abs(AttributeFormula.Evaluate("(Base_Strength + Strength_Allocated) * 2",
            Names(("Base_Strength", 1), ("Strength_Allocated", 2))) - 6) < 1e-9,
            "formula: parenthesis and multiplication");
        Check(Math.Abs(AttributeFormula.Evaluate("1 + 2 * 3", r) - 7) < 1e-9, "formula: multiplication before addition");
        Check(Math.Abs(AttributeFormula.Evaluate("10 - 2 - 3", r) - 5) < 1e-9, "formula: left-associative subtraction");

        // Helpers.
        Check(Math.Abs(AttributeFormula.Evaluate("Pin(A, 0, 1)", Names(("A", 5))) - 1) < 1e-9, "formula: Pin clamps high");
        Check(Math.Abs(AttributeFormula.Evaluate("Pin(A, 0, 1)", Names(("A", -2))) - 0) < 1e-9, "formula: Pin clamps low");
        Check(Math.Abs(AttributeFormula.Evaluate("Min(A, B)", Names(("A", 3), ("B", 1))) - 1) < 1e-9, "formula: Min");
        Check(Math.Abs(AttributeFormula.Evaluate("Max(A, B)", Names(("A", 3), ("B", 1))) - 3) < 1e-9, "formula: Max");
        Check(Math.Abs(AttributeFormula.Evaluate("Pow(A, 2)", Names(("A", 3))) - 9) < 1e-9, "formula: Pow");
        Check(Math.Abs(AttributeFormula.Evaluate("Constants.K", Names(("Constants.K", 2.5))) - 2.5) < 1e-9, "formula: Constants.X");
        Check(Math.Abs(AttributeFormula.Evaluate("(Cond ? A : B)", Names(("Cond", 1), ("A", 7), ("B", 9))) - 7) < 1e-9,
            "formula: ternary picks the true branch");
        Check(Math.Abs(AttributeFormula.Evaluate("(Cond ? A : B)", Names(("Cond", 0), ("A", 7), ("B", 9))) - 9) < 1e-9,
            "formula: ternary picks the false branch");
        Check(Math.Abs(AttributeFormula.Evaluate("Base MultiplyWith:Bonus", Names(("Base", 10), ("Bonus", 0.5))) - 15) < 1e-9,
            "formula: MultiplyWith = * (1 + X)");

        // Concrete recovered formula: Armor_SubTotal = (Armor + Flat) * (1 + Factor).
        var armorSubTotal = AttributeFormula.Evaluate(
            "((Armor + Flat_Armor_From_Constitution) * (1 + Armor_Factor_Constitution))",
            Names(("Armor", 10), ("Flat_Armor_From_Constitution", 2), ("Armor_Factor_Constitution", 0.5)));
        Check(Math.Abs(armorSubTotal - 18) < 1e-9, "formula: Armor_SubTotal = (10 + 2) * 1.5");

        // Every extracted client formula must parse and evaluate to a finite number.
        var path = FindFormulasFile();
        if (path is null)
        {
            Console.WriteLine("SKIP formula: attribute_formulas.json not found");
            return;
        }
        var formulas = JsonSerializer.Deserialize<Dictionary<string, FormulaEntry>>(File.ReadAllText(path))!;
        var parsed = 0;
        var nonFinite = new List<string>();
        foreach (var entry in formulas.Values)
        {
            double value;
            try { value = AttributeFormula.Evaluate(entry.Script, One); }
            catch (Exception ex) { throw new Exception($"formula '{entry.Name}' failed to parse: {ex.Message}"); }
            parsed++;
            if (double.IsNaN(value) || double.IsInfinity(value)) nonFinite.Add(entry.Name);
        }
        Check(parsed == formulas.Count && parsed >= 226, $"formula: all {formulas.Count} client formulas parse");
        Check(nonFinite.Count == 0, $"formula: all client formulas evaluate finite ({string.Join(",", nonFinite)})");
    }

    private static Func<string, double> Names(params (string Name, double Value)[] pairs)
    {
        var map = pairs.ToDictionary(p => p.Name, p => p.Value);
        return name => map.TryGetValue(name, out var v) ? v : 1.0;
    }

    // Resolves every name (and Constants.X) to 1 so a formula yields a finite number.
    private static double One(string name) => 1.0;

    private static string? FindFormulasFile()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        for (var i = 0; i < 8 && dir is not null; i++, dir = dir.Parent)
        {
            var candidate = Path.Combine(dir.FullName, "tools", "web-content", "generated", "attribute_formulas.json");
            if (File.Exists(candidate)) return candidate;
        }
        return null;
    }

    private sealed record FormulaEntry(string Name, string Script);
}
