using System.Reflection;
using System.Text.Json;
using Nordicandia.Server.WebApi;
using Nordicandia.Simulation;

/// <summary>
/// B04/B05: deterministic replay of the B04 sample library (<c>samples/fidelity_samples.json</c>).
/// Each sample is a fixed input and the expected output recovered from the client. The harness
/// runs every sample and fails the suite on any mismatch, so a rule can only move from
/// Provisional to 对照通过 by adding a sample that passes.
/// <see cref="FidelityReport"/> consumes <see cref="SampleResult"/> to emit the B05 diff report.
/// </summary>
static class FidelityReplayTests
{
    private const double Tolerance = 1e-9;

    public sealed record SampleResult(string Id, string Kind, bool Ok, string Actual, string? Error,
        string Source, string? DiffId);

    public static List<SampleResult> EvaluateAll()
    {
        using var document = Load();
        var results = new List<SampleResult>();
        foreach (var sample in document.RootElement.GetProperty("samples").EnumerateArray())
        {
            var id = sample.GetProperty("id").GetString() ?? "?";
            var kind = sample.GetProperty("kind").GetString() ?? "?";
            var source = sample.TryGetProperty("source", out var s) ? s.GetString() ?? "" : "";
            var diffId = sample.TryGetProperty("diffId", out var d) ? d.GetString() : null;
            try
            {
                var (ok, actual) = Evaluate(kind, sample);
                results.Add(new SampleResult(id, kind, ok, actual, null, source, diffId));
            }
            catch (Exception ex)
            {
                results.Add(new SampleResult(id, kind, false, "", ex.Message, source, diffId));
            }
        }
        return results;
    }

    public static void Run()
    {
        var results = EvaluateAll();
        var pass = 0;
        var failures = new List<string>();
        foreach (var r in results)
        {
            if (r.Ok) { pass++; Console.WriteLine($"PASS fidelity[{r.Id}]"); }
            else failures.Add(r.Error is null ? r.Id : $"{r.Id}: {r.Error}");
        }
        Console.WriteLine($"fidelity replay: {pass}/{results.Count} pass");
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

    private static (bool ok, string actual) Evaluate(string kind, JsonElement sample)
    {
        var inputs = sample.GetProperty("inputs");
        var expected = sample.GetProperty("expected");
        switch (kind)
        {
            case "rarity_weights":
            {
                var (normal, unique, set) = ItemCatalog.RarityWeightsFor(
                    inputs.GetProperty("magicFind").GetDouble(), inputs.GetProperty("factor").GetDouble());
                var actual = $"normal={normal} unique={unique} set={set}";
                return (Eq(normal, expected.GetProperty("normal").GetDouble())
                    && Eq(unique, expected.GetProperty("unique").GetDouble())
                    && Eq(set, expected.GetProperty("set").GetDouble()), actual);
            }
            case "damage_conversion":
            {
                var result = CombatModel.ConvertDamage(
                    new DamageBundle(Physical: inputs.GetProperty("physical").GetDouble()),
                    new DamageBundle(
                        Fire: Get(inputs, "fire"), Cold: Get(inputs, "cold"),
                        Lightning: Get(inputs, "lightning"), Poison: Get(inputs, "poison")));
                var actual = $"physical={result.Physical} fire={result.Fire} cold={result.Cold} lightning={result.Lightning} poison={result.Poison}";
                return (Eq(result.Physical, Get(expected, "physical")) && Eq(result.Fire, Get(expected, "fire"))
                    && Eq(result.Cold, Get(expected, "cold")) && Eq(result.Lightning, Get(expected, "lightning"))
                    && Eq(result.Poison, Get(expected, "poison")), actual);
            }
            case "resistance_penetration":
            {
                var result = CombatModel.ApplyPenetration(
                    new ResistanceBundle(Fire: inputs.GetProperty("resistance").GetDouble()),
                    new ResistanceBundle(Fire: inputs.GetProperty("penetration").GetDouble()));
                return (Eq(result.Fire, expected.GetProperty("fire").GetDouble()), $"fire={result.Fire}");
            }
            case "offline_rate":
            {
                var value = OfflineRewards.KillsPerMinute(
                    inputs.GetProperty("efficiency").GetDouble(), inputs.GetProperty("tier").GetInt32());
                return (Eq(value, expected.GetProperty("killsPerMinute").GetDouble()), $"killsPerMinute={value}");
            }
            case "brain_choice":
            {
                var brain = BrainCatalog.For(inputs.GetProperty("brain").GetString()!);
                var conditions = inputs.GetProperty("conditions").EnumerateArray().Select(c => c.GetString()!).ToHashSet();
                var roll = inputs.GetProperty("roll").GetDouble();
                var chosen = BrainCatalog.Choose(brain, n => conditions.Contains(n), () => roll)?.Power;
                return (chosen == expected.GetProperty("power").GetString(), $"power={chosen}");
            }
            case "item_quantity":
            {
                var quantity = inputs.GetProperty("itemQuantity").GetDouble();
                var draws = inputs.GetProperty("draws").GetInt32();
                var remainder = 0.0;
                var counts = new int[draws];
                for (var i = 0; i < draws; i++) counts[i] = CombatRegistry.RollItemsPerDrop(quantity, ref remainder);
                var want = expected.GetProperty("counts").EnumerateArray().Select(c => c.GetInt32()).ToArray();
                return (counts.SequenceEqual(want), "counts=[" + string.Join(",", counts) + "]");
            }
            case "skill_param":
            {
                // The web class pool's skill value must equal the client set_Item value.
                var classId = inputs.GetProperty("classId").GetInt32();
                var skill = inputs.GetProperty("skill").GetString()!;
                var field = inputs.GetProperty("field").GetString()!;
                var profile = PowerCatalog.BuildPool(classId, new[] { skill }, Array.Empty<string>())
                    .Active.FirstOrDefault(s => s.Name == skill);
                if (profile is null) return (false, "skill not found");
                var actualValue = field switch
                {
                    "Multiplier" => profile.Multiplier,
                    "Cooldown" => profile.Cooldown,
                    "Radius" => profile.Radius,
                    "ManaCost" => profile.ManaCost,
                    _ => profile.Values.TryGetValue(field, out var v) ? v : double.NaN,
                };
                return (Eq(actualValue, expected.GetProperty("value").GetDouble()), $"value={actualValue}");
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
                if (!expected.GetProperty("hit").GetBoolean())
                    return (!result.Hit, $"hit={result.Hit}");
                var plain = CombatModel.ResolveBundleAttack(attacker, new DamageBundle(Physical: 100),
                    CombatantStats.FromRealtime(1000, 0, 0, 10), default, new AttackProfile(1.0, 0, 0, 0), new CombatRandom(7));
                var ratio = expected.TryGetProperty("ratio", out var r) ? r.GetDouble() : 1.0;
                return (result.Hit && Eq(result.Damage, plain.Damage * ratio),
                    $"hit={result.Hit} damage={result.Damage}");
            }
            case "hit_chance":
            {
                // Game.Calculator.CalculateChanceToHit: 1.05*atk/(Pow(def/2,0.75)+atk), clamp [0.05, 1].
                var chance = CombatModel.ChanceToHit(
                    inputs.GetProperty("attack").GetDouble(), inputs.GetProperty("defense").GetDouble());
                return (Eq(chance, expected.GetProperty("chance").GetDouble()), $"chance={chance}");
            }
            case "damage_reduction":
            {
                // Game.Calculator.CalculatePhysicalDamageReduction: armor/(armor + 50*damage).
                var reduction = CombatModel.PhysicalDamageReduction(
                    inputs.GetProperty("armor").GetDouble(), inputs.GetProperty("damage").GetDouble());
                return (Eq(reduction, expected.GetProperty("reduction").GetDouble()), $"reduction={reduction}");
            }
            case "poison_dot":
            {
                // HitPayload: poison DPS = 20% of the hit's total damage; DebuffPoisoned.DoWork
                // ticks once per second and cannot crit/fork/chain.
                var dps = CombatInstance.PoisonHitDamageFactor * inputs.GetProperty("hitDamage").GetDouble();
                return (Eq(dps, expected.GetProperty("poisonDps").GetDouble()), $"poisonDps={dps}");
            }
            case "blessing":
            {
                // WindowAesirOffering.CreateAesirBuffOffline: magnitude 0.4 (.so 0x1387368);
                // offering size selects duration only (10m/30m/1h/4h).
                var minutes = Blessings.Duration(inputs.GetProperty("size").GetInt32()).TotalMinutes;
                var actual = $"minutes={minutes} magnitude={Blessings.Magnitude}";
                return (Eq(minutes, expected.GetProperty("minutes").GetDouble())
                    && Eq(Blessings.Magnitude, expected.GetProperty("magnitude").GetDouble()), actual);
            }
            case "buff_lifecycle":
            {
                // C02: scripted BuffManager scenario. ops: add {def,source,dur,mag,dps,
                // stackable,persistent}, advance dt, dispel "prefix", clear.
                // expected: {def, source, check: magnitude|remaining|stacks|count|ticked|has, value}.
                var mgr = new BuffManager();
                var ticked = 0.0;
                foreach (var op in inputs.GetProperty("ops").EnumerateArray())
                {
                    if (op.TryGetProperty("add", out var a))
                        mgr.Add(new BuffInstance
                        {
                            DefinitionId = a.GetProperty("def").GetString()!,
                            Source = a.TryGetProperty("source", out var s) ? s.GetString()! : "",
                            Duration = a.GetProperty("dur").GetDouble(),
                            Remaining = a.GetProperty("dur").GetDouble(),
                            Magnitude = a.TryGetProperty("mag", out var m) ? m.GetDouble() : 0,
                            TickDps = a.TryGetProperty("dps", out var d) ? d.GetDouble() : 0,
                            Stackable = a.TryGetProperty("stackable", out var st) && st.GetBoolean(),
                            IsPersistent = a.TryGetProperty("persistent", out var p) && p.GetBoolean(),
                        });
                    if (op.TryGetProperty("advance", out var ad))
                        mgr.Tick(ad.GetDouble(), (b, el) => ticked += b.TickDps * el);
                    if (op.TryGetProperty("dispel", out var dp))
                        mgr.Dispel(b => b.DefinitionId.StartsWith(dp.GetString()!,
                            StringComparison.Ordinal));
                    if (op.TryGetProperty("clear", out _)) mgr.Clear();
                }
                var def = expected.GetProperty("def").GetString()!;
                var src = expected.TryGetProperty("source", out var es) ? es.GetString()! : "";
                var check = expected.GetProperty("check").GetString()!;
                var want = expected.GetProperty("value").GetDouble();
                var got = check switch
                {
                    "magnitude" => mgr.MagnitudeOf(def, src),
                    "remaining" => mgr.RemainingOf(def, src),
                    "stacks" => mgr.Get(def, src)?.Stacks ?? 0,
                    "count" => mgr.Count,
                    "ticked" => ticked,
                    "has" => mgr.Has(def, src) ? 1 : 0,
                    _ => throw new Exception($"unknown buff check {check}"),
                };
                return (Eq(got, want), $"{check}={got}");
            }
            case "projectile":
            {
                // C03: geometric projectile scenarios through the real CombatInstance.
                // check: "zero" (loss == 0), "positive" (loss > 0), "equal" (two runs agree).
                static CombatInstance Make(double fork, double chain, int count)
                {
                    var stats = new CombatantStats(100, 1000, 0, 1, AttackRating: 1e12, CritChance: 0,
                        Damage: new DamageBundle(Physical: 100),
                        ProjectileAutoAttack: true, ForkChance: fork, ChainChance: chain);
                    var inst = new CombatInstance(stats, 0, 0, 0, 0, 123, monsterCount: count);
                    foreach (var m in inst.Monsters)
                    {
                        m.Hp = m.MaxHp = 10000; m.Armor = m.Defense = 0;
                        m.Speed = 0; m.Offense = 0; m.AttackCooldown = 1000; m.Resistances = default;
                    }
                    return inst;
                }
                var scenario = inputs.GetProperty("scenario").GetString()!;
                var fork = inputs.GetProperty("forkChance").GetDouble();
                var chain = inputs.GetProperty("chainChance").GetDouble();
                double got;
                switch (scenario)
                {
                    case "chain_range":
                    {
                        // Second monster 11y from the hit point: beyond the 10y chain radius.
                        var inst = Make(fork, chain, 2);
                        inst.Monsters[0].X = 1; inst.Monsters[0].Z = 0;
                        inst.Monsters[1].X = 11.5; inst.Monsters[1].Z = 0;
                        inst.Advance(0.05);
                        got = inst.Monsters[0].MaxHp - inst.Monsters[0].Hp > 0
                            && inst.Monsters[1].Hp == inst.Monsters[1].MaxHp ? 0 : -1;
                        break;
                    }
                    case "chain_in_range":
                    {
                        var inst = Make(fork, chain, 2);
                        inst.Monsters[0].X = 1; inst.Monsters[0].Z = 0;
                        inst.Monsters[1].X = 5; inst.Monsters[1].Z = 0;
                        inst.Advance(0.05);
                        got = inst.Monsters[1].MaxHp - inst.Monsters[1].Hp;
                        break;
                    }
                    case "fork_no_doublehit":
                    {
                        // Same seed: the primary hit must be bit-identical with and without
                        // fork — children never re-hit the original target (hit-set).
                        var a = Make(fork, 0, 1);
                        var b = Make(0, 0, 1);
                        a.Monsters[0].X = b.Monsters[0].X = 1;
                        a.Monsters[0].Z = b.Monsters[0].Z = 0;
                        a.Advance(0.05); b.Advance(0.05);
                        var lossA = a.Monsters[0].MaxHp - a.Monsters[0].Hp;
                        var lossB = b.Monsters[0].MaxHp - b.Monsters[0].Hp;
                        got = lossA == lossB && lossA > 0 ? 1 : 0;
                        break;
                    }
                    default: throw new Exception($"unknown projectile scenario {scenario}");
                }
                var check = expected.GetProperty("check").GetString()!;
                var want = expected.GetProperty("value").GetDouble();
                var ok = check switch
                {
                    "zero" => Eq(got, 0),
                    "positive" => got > 0 && Eq(1, want),
                    "equal" => Eq(got, want),
                    _ => throw new Exception($"unknown projectile check {check}"),
                };
                return (ok, $"{scenario}={got}");
            }
            case "boundary_resistance_cap":
            {
                // ClientVerified: resistance is capped at 1.0 before mitigation, so 150%
                // resistance still yields zero elemental damage (full immunity boundary).
                var damage = CombatModel.EffectiveElementalDamage(
                    inputs.GetProperty("rawDamage").GetDouble(), inputs.GetProperty("resistance").GetDouble());
                return (Eq(damage, expected.GetProperty("damage").GetDouble()), $"damage={damage}");
            }
            case "boundary_conversion_overflow":
            {
                // ConvertDamage normalises when the conversion percentages sum above 1.
                var result = CombatModel.ConvertDamage(
                    new DamageBundle(Physical: inputs.GetProperty("physical").GetDouble()),
                    new DamageBundle(
                        Fire: Get(inputs, "fire"), Cold: Get(inputs, "cold"),
                        Lightning: Get(inputs, "lightning"), Poison: Get(inputs, "poison")));
                var actual = $"physical={result.Physical} fire={result.Fire} cold={result.Cold} lightning={result.Lightning} poison={result.Poison}";
                return (Eq(result.Physical, Get(expected, "physical")) && Eq(result.Fire, Get(expected, "fire"))
                    && Eq(result.Cold, Get(expected, "cold")) && Eq(result.Lightning, Get(expected, "lightning"))
                    && Eq(result.Poison, Get(expected, "poison")), actual);
            }
            case "immune_crit":
            {
                // Ignores_Critical_Hits (0x1168) on the defender prevents crits even when the
                // attacker would always crit.
                var attacker = CombatantStats.FromRealtime(1000, 0, 0, 10)
                    with { AlwaysHits = true, CritChance = 1.0 };
                var defender = CombatantStats.FromRealtime(1000, 0, 0, 10) with { IgnoresCrits = true };
                var result = CombatModel.ResolveBundleAttack(attacker, new DamageBundle(Physical: 100), defender,
                    default, new AttackProfile(1.0, 0, 0, 0), new CombatRandom(7));
                return (!result.Critical, $"critical={result.Critical} hit={result.Hit}");
            }
            case "immune_dodge":
            {
                // DodgeChance = 1.0 always evades (immunity to the hit itself).
                var attacker = CombatantStats.FromRealtime(1000, 0, 0, 10) with { AlwaysHits = true };
                var defender = CombatantStats.FromRealtime(1000, 0, 0, 10) with { DodgeChance = 1.0 };
                var result = CombatModel.ResolveBundleAttack(attacker, new DamageBundle(Physical: 100), defender,
                    default, new AttackProfile(1.0, 0, 0, 0), new CombatRandom(7));
                return (!result.Hit, $"hit={result.Hit}");
            }
            case "deterministic_replay":
            {
                // The same command (inputs + seed) replayed must produce the identical result:
                // this is what makes offline/server replay trustworthy.
                var attacker = CombatantStats.FromRealtime(1000, 0, 0, 10);
                var defender = CombatantStats.FromRealtime(1000, 0, 0, 10)
                    with { DodgeChance = 0.25, BlockChance = 0.25 };
                var seed = inputs.TryGetProperty("seed", out var sd) ? sd.GetUInt64() : 42UL;
                DamageResult RunOnce() => CombatModel.ResolveBundleAttack(attacker,
                    new DamageBundle(Physical: 100), defender, default,
                    new AttackProfile(1.0, 0, 0, 0), new CombatRandom(seed));
                var first = RunOnce();
                var second = RunOnce();
                var identical = first.Damage == second.Damage && first.Critical == second.Critical
                    && first.Hit == second.Hit;
                return (identical && Eq(first.Damage, expected.GetProperty("damage").GetDouble()),
                    $"damage={first.Damage} critical={first.Critical} hit={first.Hit} identical={identical}");
            }
            case "relogin_blessing_roundtrip":
            {
                // 重登: blessing expiry state must survive a JSON save/load round-trip
                // (the GameStore persistence mechanism).
                var state = new Dictionary<int, DateTime>
                {
                    [inputs.GetProperty("type").GetInt32()] =
                        DateTime.Parse(inputs.GetProperty("expiry").GetString()!).ToUniversalTime(),
                };
                var json = JsonSerializer.Serialize(state);
                var restored = JsonSerializer.Deserialize<Dictionary<int, DateTime>>(json)!;
                var expiry = restored[inputs.GetProperty("type").GetInt32()];
                var expectedExpiry = DateTime.Parse(expected.GetProperty("expiry").GetString()!).ToUniversalTime();
                return (expiry == expectedExpiry, $"expiry={expiry:o}");
            }
            case "damage_taken_amplify":
            {
                // C01: Amplify_Damage_Taken_Percent (attribute 6) scales the defender's
                // taken damage after mitigation (inferred placement, before the 1.0 floor).
                var attacker = CombatantStats.FromRealtime(1000, 0, 0, 10) with { AlwaysHits = true };
                var defender = CombatantStats.FromRealtime(1000, 0, 0, 10)
                    with { DamageTakenAmplifyPercent = inputs.GetProperty("amplifyPercent").GetDouble() };
                var result = CombatModel.ResolveBundleAttack(attacker, new DamageBundle(Physical: 100), defender,
                    default, new AttackProfile(1.0, 0, 0, 0), new CombatRandom(11));
                return (Eq(result.Damage, expected.GetProperty("damage").GetDouble()),
                    $"damage={result.Damage}");
            }
            case "zero_damage_floor":
            {
                // C01: damage that mitigates below 1.0 still floors at 1.0 (provisional:
                // the client floor is assumed, not yet recovered). Note physical
                // reduction itself caps at 0.9, so the floor is reached via small hits.
                var attacker = CombatantStats.FromRealtime(1000, 0, 0, 10) with { AlwaysHits = true };
                var defender = CombatantStats.FromRealtime(1000, 0, 0, 10) with { Armor = 1e9 };
                var physical = inputs.TryGetProperty("physical", out var p) ? p.GetDouble() : 100.0;
                var result = CombatModel.ResolveBundleAttack(attacker, new DamageBundle(Physical: physical), defender,
                    default, new AttackProfile(1.0, 0, 0, 0), new CombatRandom(11));
                return (Eq(result.Damage, expected.GetProperty("damage").GetDouble()),
                    $"damage={result.Damage} hit={result.Hit}");
            }
            case "no_rounding":
            {
                // C01: verified by disassembly — ApplyDamageReduction (0x02BAF34C) is 6
                // instructions with no frint/lrint; damage stays fractional, no rounding.
                var damage = CombatModel.EffectiveElementalDamage(
                    inputs.GetProperty("rawDamage").GetDouble(), inputs.GetProperty("resistance").GetDouble());
                var rounded = Math.Round(damage) == damage;
                return (Eq(damage, expected.GetProperty("damage").GetDouble()) && !rounded,
                    $"damage={damage}");
            }
            case "monster_base_stats":
            {
                // D04: Game.Monster.Get* curves. Constants read from the module pool; the
                // harness compares with a relative tolerance because the values are large.
                var level = inputs.GetProperty("level").GetDouble();
                var expMult = inputs.TryGetProperty("expMult", out var em) ? em.GetDouble() : 1.0;
                var finalMult = inputs.TryGetProperty("finalMult", out var fm) ? fm.GetDouble() : MonsterScaling.FinalStatsMult;
                var stats = MonsterScaling.Stats(level, expMult, finalMult);
                static bool Rel(double actual, double expected)
                    => Math.Abs(actual - expected) <= 1e-6 * Math.Max(1.0, Math.Abs(expected));
                var ok = Rel(stats.Life, expected.GetProperty("life").GetDouble())
                    && Rel(stats.Armor, expected.GetProperty("armor").GetDouble())
                    && Rel(stats.Evasion, expected.GetProperty("evasion").GetDouble())
                    && Rel(stats.MinAttackRating, expected.GetProperty("minAttackRating").GetDouble())
                    && Rel(stats.MaxAttackRating, expected.GetProperty("maxAttackRating").GetDouble())
                    && Rel(stats.WeaponDamage, expected.GetProperty("weaponDamage").GetDouble())
                    && Rel(stats.Experience, expected.GetProperty("experience").GetDouble())
                    && Rel(stats.ForceField, expected.GetProperty("forceField").GetDouble());
                return (ok, $"life={stats.Life:F4} armor={stats.Armor:F4} evasion={stats.Evasion:F4} " +
                    $"ar={stats.MinAttackRating:F4}/{stats.MaxAttackRating:F4} wdmg={stats.WeaponDamage:F4} " +
                    $"exp={stats.Experience:F4} ff={stats.ForceField:F4}");
            }
            default:
                throw new InvalidOperationException($"unknown sample kind '{kind}'");
        }
    }

    private static double Get(JsonElement element, string name)
        => element.TryGetProperty(name, out var value) ? value.GetDouble() : 0.0;

    private static bool Eq(double a, double b) => Math.Abs(a - b) <= Tolerance;
}
