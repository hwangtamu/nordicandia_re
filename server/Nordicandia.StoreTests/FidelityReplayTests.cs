using System.Reflection;
using System.Text.Json;
using Nordicandia.Server.WebApi;
using Nordicandia.Simulation;

/// <summary>
/// B05: deterministic replay of the B04 sample library (<c>samples/fidelity_samples.json</c>).
/// Each sample is a fixed input and the expected output recovered from the client. The harness
/// runs every sample and fails the suite on any mismatch, so a rule can only move from
/// Provisional to 对照通过 by adding a sample that passes.
/// </summary>
static class FidelityReplayTests
{
    private const double Tolerance = 1e-9;

    public static void Run()
    {
        using var document = Load();
        var samples = document.RootElement.GetProperty("samples").EnumerateArray().ToList();
        var pass = 0;
        var failures = new List<string>();
        foreach (var sample in samples)
        {
            var id = sample.GetProperty("id").GetString() ?? "?";
            var kind = sample.GetProperty("kind").GetString() ?? "?";
            bool ok;
            try { ok = Run(kind, sample); }
            catch (Exception ex) { ok = false; failures.Add($"{id}: {ex.Message}"); }
            if (ok) { pass++; Console.WriteLine($"PASS fidelity[{id}]"); }
            else failures.Add(id);
        }
        Console.WriteLine($"fidelity replay: {pass}/{samples.Count} pass");
        if (failures.Count > 0) throw new Exception("fidelity replay failures: " + string.Join(", ", failures));
    }

    private static JsonDocument Load()
    {
        var assembly = Assembly.GetExecutingAssembly();
        var resource = assembly.GetManifestResourceNames()
            .First(n => n.EndsWith("samples.fidelity_samples.json", StringComparison.Ordinal));
        using var stream = assembly.GetManifestResourceStream(resource)!;
        return JsonDocument.Parse(stream);
    }

    private static bool Run(string kind, JsonElement sample)
    {
        var inputs = sample.GetProperty("inputs");
        var expected = sample.GetProperty("expected");
        switch (kind)
        {
            case "rarity_weights":
            {
                var (normal, unique, set) = ItemCatalog.RarityWeightsFor(
                    inputs.GetProperty("magicFind").GetDouble(), inputs.GetProperty("factor").GetDouble());
                return Eq(normal, expected.GetProperty("normal").GetDouble())
                    && Eq(unique, expected.GetProperty("unique").GetDouble())
                    && Eq(set, expected.GetProperty("set").GetDouble());
            }
            case "damage_conversion":
            {
                var result = CombatModel.ConvertDamage(
                    new DamageBundle(Physical: inputs.GetProperty("physical").GetDouble()),
                    new DamageBundle(
                        Fire: Get(inputs, "fire"), Cold: Get(inputs, "cold"),
                        Lightning: Get(inputs, "lightning"), Poison: Get(inputs, "poison")));
                return Eq(result.Physical, Get(expected, "physical")) && Eq(result.Fire, Get(expected, "fire"))
                    && Eq(result.Cold, Get(expected, "cold")) && Eq(result.Lightning, Get(expected, "lightning"))
                    && Eq(result.Poison, Get(expected, "poison"));
            }
            case "resistance_penetration":
            {
                var result = CombatModel.ApplyPenetration(
                    new ResistanceBundle(Fire: inputs.GetProperty("resistance").GetDouble()),
                    new ResistanceBundle(Fire: inputs.GetProperty("penetration").GetDouble()));
                return Eq(result.Fire, expected.GetProperty("fire").GetDouble());
            }
            case "offline_rate":
            {
                var value = OfflineRewards.KillsPerMinute(
                    inputs.GetProperty("efficiency").GetDouble(), inputs.GetProperty("tier").GetInt32());
                return Eq(value, expected.GetProperty("killsPerMinute").GetDouble());
            }
            case "brain_choice":
            {
                var brain = BrainCatalog.For(inputs.GetProperty("brain").GetString()!);
                var conditions = inputs.GetProperty("conditions").EnumerateArray().Select(c => c.GetString()!).ToHashSet();
                var roll = inputs.GetProperty("roll").GetDouble();
                var chosen = BrainCatalog.Choose(brain, n => conditions.Contains(n), () => roll)?.Power;
                return chosen == expected.GetProperty("power").GetString();
            }
            case "item_quantity":
            {
                var quantity = inputs.GetProperty("itemQuantity").GetDouble();
                var draws = inputs.GetProperty("draws").GetInt32();
                var remainder = 0.0;
                var counts = new int[draws];
                for (var i = 0; i < draws; i++) counts[i] = CombatRegistry.RollItemsPerDrop(quantity, ref remainder);
                var want = expected.GetProperty("counts").EnumerateArray().Select(c => c.GetInt32()).ToArray();
                return counts.SequenceEqual(want);
            }
            case "skill_param":
            {
                // The web class pool's skill value must equal the client set_Item value.
                var classId = inputs.GetProperty("classId").GetInt32();
                var skill = inputs.GetProperty("skill").GetString()!;
                var field = inputs.GetProperty("field").GetString()!;
                var profile = PowerCatalog.BuildPool(classId, new[] { skill }, Array.Empty<string>())
                    .Active.FirstOrDefault(s => s.Name == skill);
                if (profile is null) return false;
                var actual = field switch
                {
                    "Multiplier" => profile.Multiplier,
                    "Cooldown" => profile.Cooldown,
                    "Radius" => profile.Radius,
                    "ManaCost" => profile.ManaCost,
                    _ => profile.Values.TryGetValue(field, out var v) ? v : double.NaN,
                };
                return Eq(actual, expected.GetProperty("value").GetDouble());
            }
            case "damage_order":
            {
                // AttackPayload.Resolve order: miss -> dodge -> block -> crit.
                var dodge = Get(inputs, "dodge");
                var block = Get(inputs, "block");
                var mult = inputs.TryGetProperty("blockMultiplier", out var bm) ? bm.GetDouble() : 1.0;
                var attacker = CombatantStats.FromRealtime(1000, 0, 0, 10);
                var defender = CombatantStats.FromRealtime(1000, 0, 0, 10)
                    with { DodgeChance = dodge, BlockChance = block, BlockedDamageMultiplier = mult };
                var result = CombatModel.ResolveBundleAttack(attacker, new DamageBundle(Physical: 100), defender,
                    default, new AttackProfile(1.0, 0, 0, 0), new CombatRandom(7));
                if (!expected.GetProperty("hit").GetBoolean()) return !result.Hit;
                var plain = CombatModel.ResolveBundleAttack(attacker, new DamageBundle(Physical: 100),
                    CombatantStats.FromRealtime(1000, 0, 0, 10), default, new AttackProfile(1.0, 0, 0, 0), new CombatRandom(7));
                var ratio = expected.TryGetProperty("ratio", out var r) ? r.GetDouble() : 1.0;
                return result.Hit && Eq(result.Damage, plain.Damage * ratio);
            }
            default:
                throw new InvalidOperationException($"unknown sample kind '{kind}'");
        }
    }

    private static double Get(JsonElement element, string name)
        => element.TryGetProperty(name, out var value) ? value.GetDouble() : 0.0;

    private static bool Eq(double a, double b) => Math.Abs(a - b) <= Tolerance;
}
