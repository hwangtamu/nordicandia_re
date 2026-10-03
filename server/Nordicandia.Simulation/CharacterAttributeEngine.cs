using System.Reflection;
using System.Text.Json;

namespace Nordicandia.Simulation;

/// <summary>
/// Evaluates the client's recovered scripted-attribute formulas (see
/// <c>tools/web-content/generated/attribute_formulas.json</c>) over a character's stored
/// attribute map, producing the derived ratings the client's <c>CalculateCombatAttributes</c>
/// consumes (AttackRating, Armor, Evasion, Crit chance, Life/Mana max, ...).
///
/// The formula data is embedded from <c>GameData/</c>. Per-character state lives in
/// <see cref="Evaluation"/>, so a single engine instance can evaluate many characters.
/// </summary>
/// <summary>Raw damage split by type (physical + the four elements).</summary>
public readonly record struct DamageBundle(double Physical = 0, double Fire = 0, double Cold = 0,
    double Lightning = 0, double Poison = 0)
{
    public double Total => Physical + Fire + Cold + Lightning + Poison;
    public DamageBundle Scale(double factor)
        => new(Physical * factor, Fire * factor, Cold * factor, Lightning * factor, Poison * factor);
}

/// <summary>Per-element resistance, already capped by Resistance_Max_Total (<= 0.95).</summary>
public readonly record struct ResistanceBundle(double Fire = 0, double Cold = 0, double Lightning = 0, double Poison = 0);

public sealed class CharacterAttributeEngine
{
    private static readonly Lazy<CharacterAttributeEngine> Default = new(Load);
    public static CharacterAttributeEngine Instance => Default.Value;

    private readonly Dictionary<string, int> nameToId;
    private readonly Dictionary<int, string> formulas;
    private readonly Dictionary<string, double> constants;

    private CharacterAttributeEngine(Dictionary<string, int> nameToId,
        Dictionary<int, string> formulas, Dictionary<string, double> constants)
    {
        this.nameToId = nameToId;
        this.formulas = formulas;
        this.constants = constants;
    }

    /// <summary>Number of scripted formulas loaded (sanity check for the embedded data).</summary>
    public int FormulaCount => formulas.Count;

    public bool TryGetId(string name, out int id) => nameToId.TryGetValue(name, out id);

    /// <summary>Begin an evaluation over a stored attribute map (id -> summed value).</summary>
    public Evaluation Evaluate(IReadOnlyDictionary<int, double> stored) => new(this, stored);

    public Evaluation Evaluate(IEnumerable<KeyValuePair<int, double>> stored)
    {
        var map = new Dictionary<int, double>();
        foreach (var kvp in stored) map[kvp.Key] = map.GetValueOrDefault(kvp.Key) + kvp.Value;
        return new Evaluation(this, map);
    }

    private static CharacterAttributeEngine Load()
    {
        var nameToId = new Dictionary<string, int>();
        foreach (var (id, name) in ReadJson<Dictionary<string, string>>("attribute_ids.json"))
            nameToId.TryAdd(name, int.Parse(id));

        var formulas = new Dictionary<int, string>();
        foreach (var (id, entry) in ReadJson<Dictionary<string, FormulaEntry>>("attribute_formulas.json"))
        {
            formulas[int.Parse(id)] = entry.Script;
            // attribute_ids.json does not list every derived attribute, but the *_Final
            // formulas are identity passthroughs whose script is the bare attribute name
            // (e.g. 244 -> "Strength_Bonus_Percent_Final"). Register those names so totals
            // that are MultiplyWith: / Add: them can resolve the stored input value.
            if (entry.Name == "?" && IsBareIdentifier(entry.Script)) nameToId.TryAdd(entry.Script, int.Parse(id));
        }

        var constants = ReadJson<Dictionary<string, double>>("constants.json");
        return new CharacterAttributeEngine(nameToId, formulas, constants);
    }

    private static bool IsBareIdentifier(string script)
    {
        if (string.IsNullOrEmpty(script)) return false;
        if (!(char.IsLetter(script[0]) || script[0] == '_')) return false;
        foreach (var c in script)
            if (!(char.IsLetterOrDigit(c) || c == '_')) return false;
        return true;
    }

    private static T ReadJson<T>(string fileName)
    {
        var assembly = Assembly.GetExecutingAssembly();
        var resource = assembly.GetManifestResourceNames()
            .First(n => n.EndsWith("GameData." + fileName, StringComparison.Ordinal));
        using var stream = assembly.GetManifestResourceStream(resource)!;
        return JsonSerializer.Deserialize<T>(stream, new JsonSerializerOptions { PropertyNameCaseInsensitive = true })!;
    }

    private sealed record FormulaEntry(string Name, string Script);

    /// <summary>A single character's evaluation: resolves a named attribute to its value,
    /// evaluating scripted formulas recursively and memoising the result.</summary>
    public sealed class Evaluation
    {
        private readonly CharacterAttributeEngine engine;
        private readonly IReadOnlyDictionary<int, double> stored;
        private readonly Dictionary<string, double> memo = new();
        private readonly HashSet<string> visiting = new();

        internal Evaluation(CharacterAttributeEngine engine, IReadOnlyDictionary<int, double> stored)
        {
            this.engine = engine;
            this.stored = stored;
        }

        /// <summary>Resolve an attribute by name. Scripted attributes evaluate their formula;
        /// plain attributes return the stored value (0 when absent); <c>Constants.X</c> resolve
        /// from the recovered constants table.</summary>
        public double Resolve(string name)
        {
            if (name.StartsWith("Constants.", StringComparison.Ordinal))
                return engine.constants.GetValueOrDefault(name["Constants.".Length..]);
            if (!engine.nameToId.TryGetValue(name, out var id)) return 0.0;
            if (memo.TryGetValue(name, out var cached)) return cached;
            if (!visiting.Add(name)) throw new InvalidOperationException($"attribute cycle at '{name}'");

            double value;
            if (engine.formulas.TryGetValue(id, out var script))
                // An identity formula (script == its own name) is a passthrough of the stored
                // input value (the client's *_Final attributes), not a recursive reference.
                value = script == name ? stored.GetValueOrDefault(id) : AttributeFormula.Evaluate(script, Resolve);
            else
                value = stored.GetValueOrDefault(id);

            visiting.Remove(name);
            memo[name] = value;
            return value;
        }

        // Derived combat ratings the client's CalculateCombatAttributes reads.
        public double AttackRating => Resolve("AttackRating_Total");
        public double Armor => Resolve("Armor_Total");
        public double Evasion => Resolve("Evasion_Total");
        public double CritChanceMainHand => Resolve("Crit_Chance_MainHand_Total");
        public double CritDamageTotal => Resolve("Crit_Damage_Total");
        public double LifeMax => Resolve("Life_Max_Total");
        public double ManaMax => Resolve("Mana_Max_Total");
        /// <summary>Average main-hand weapon damage per type. Per GameCalculator.FillRawMainHandAverageDamage
        /// the average for a type is <c>Min_Total + Delta_Total * 0.5</c> ("delta" is the max-min spread).</summary>
        public DamageBundle WeaponDamage => new(
            AverageWeapon("Weapon_Physical_Damage"),
            AverageWeapon("Weapon_Fire_Damage"),
            AverageWeapon("Weapon_Cold_Damage"),
            AverageWeapon("Weapon_Lightning_Damage"),
            AverageWeapon("Weapon_Poison_Damage"));

        private double AverageWeapon(string prefix)
            => Resolve(prefix + "_Min_MainHand_Total") + 0.5 * Resolve(prefix + "_Delta_MainHand_Total");

        /// <summary>Capped elemental resistances (Resistance_*_Total_Capped = Min(total, Resistance_Max_Total)).</summary>
        public ResistanceBundle Resistances => new(
            Resolve("Resistance_Fire_Total_Capped"),
            Resolve("Resistance_Cold_Total_Capped"),
            Resolve("Resistance_Lightning_Total_Capped"),
            Resolve("Resistance_Poison_Total_Capped"));

        public double Strength => Resolve("Strength_Total");
        public double Dexterity => Resolve("Dexterity_Total");
        public double Intelligence => Resolve("Intelligence_Total");
        public double Vitality => Resolve("Vitality_Total");
        public double Constitution => Resolve("Constitution_Total");
        public double Agility => Resolve("Agility_Total");
        public double Mindpower => Resolve("Mindpower_Total");
    }
}
