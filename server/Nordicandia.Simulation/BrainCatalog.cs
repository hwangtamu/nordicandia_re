using System.Reflection;
using System.Text.Json;

namespace Nordicandia.Simulation;

/// <summary>
/// Recovered <c>Brains.json</c> weighted action trees, with the integer
/// <c>BrainConditions</c> resolved to <c>Game.AI.MonsterBrainCondition</c> names via
/// <c>StringHashHelper.HashNameSafe</c> (see <c>tools/web-content/export_brains.py</c>).
///
/// A <see cref="Brain"/> is a list of <see cref="Action"/>s; each action is a power plus a
/// weight and the conditions that must hold. <see cref="Choose"/> picks one by weight from the
/// eligible set, which is what <c>Game.AI.WeightedActionBrain</c> does before executing the power.
/// </summary>
public static class BrainCatalog
{
    public sealed record Action(string Power, int Weight, IReadOnlyList<string> Conditions);

    public sealed record Brain(string Name, int Id, IReadOnlyList<Action> Actions);

    private static readonly Lazy<IReadOnlyDictionary<string, Brain>> All = new(Load);

    public static IReadOnlyDictionary<string, Brain> Brains => All.Value;

    public static Brain For(string name)
        => name is not null && All.Value.TryGetValue(name, out var brain) ? brain : null;

    /// <summary>Weighted pick among the actions whose conditions are all fulfilled. Returns null
    /// when no action is eligible, matching the client's "do nothing" fallback.</summary>
    public static Action Choose(Brain brain, Func<string, bool> condition, Func<double> nextDouble)
    {
        if (brain is null || brain.Actions.Count == 0) return null;
        var eligible = new List<Action>();
        var total = 0.0;
        foreach (var action in brain.Actions)
        {
            var ok = true;
            foreach (var c in action.Conditions)
                if (!condition(c)) { ok = false; break; }
            if (!ok) continue;
            eligible.Add(action);
            total += Math.Max(0, action.Weight);
        }
        if (eligible.Count == 0) return null;
        if (total <= 0) return eligible[0];
        var roll = nextDouble() * total;
        foreach (var action in eligible)
        {
            roll -= Math.Max(0, action.Weight);
            if (roll <= 0) return action;
        }
        return eligible[^1];
    }

    private static IReadOnlyDictionary<string, Brain> Load()
    {
        var assembly = Assembly.GetExecutingAssembly();
        var resource = assembly.GetManifestResourceNames()
            .First(n => n.EndsWith("GameData.brains_catalog.json", StringComparison.Ordinal));
        using var stream = assembly.GetManifestResourceStream(resource)!;
        using var document = JsonDocument.Parse(stream);
        var result = new Dictionary<string, Brain>();
        foreach (var entry in document.RootElement.EnumerateObject())
        {
            var actions = new List<Action>();
            if (entry.Value.TryGetProperty("actions", out var list))
                foreach (var action in list.EnumerateArray())
                {
                    var conditions = new List<string>();
                    if (action.TryGetProperty("conditions", out var conds))
                        foreach (var c in conds.EnumerateArray()) conditions.Add(c.GetString() ?? "");
                    actions.Add(new Action(
                        action.GetProperty("power").GetString() ?? "",
                        action.GetProperty("weight").GetInt32(),
                        conditions));
                }
            result[entry.Name] = new Brain(entry.Name,
                entry.Value.TryGetProperty("id", out var id) ? id.GetInt32() : 0, actions);
        }
        return result;
    }
}
