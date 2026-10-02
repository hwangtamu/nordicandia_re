using System.Reflection;
using System.Text.Json;

namespace Nordicandia.Server.WebApi;

/// <summary>
/// Item-set bonuses (from gamedata_decrypted/ItemSets.json), extracted to
/// tools/web-content/generated/set_catalog.json and embedded. Each set has breakpoints keyed by
/// the number of equipped pieces; crossing a breakpoint grants its attribute bonuses.
/// </summary>
public static class SetCatalog
{
    public readonly record struct Bonus(int AttributeId, string AttributeName, double Value);
    public readonly record struct Breakpoint(int NumItems, IReadOnlyList<Bonus> Bonuses);
    public readonly record struct Set(string Name, IReadOnlyList<Breakpoint> Breakpoints);

    private static readonly Lazy<Dictionary<int, Set>> All = new(Load);

    public static int Count => All.Value.Count;
    public static IReadOnlyList<int> Ids => All.Value.Keys.ToList();
    public static Set? For(int setId) => All.Value.TryGetValue(setId, out var set) ? set : null;

    /// <summary>Attribute bonuses active at <paramref name="pieceCount"/> equipped pieces (summed).</summary>
    public static Dictionary<int, double> ActiveBonuses(int setId, int pieceCount)
    {
        var result = new Dictionary<int, double>();
        if (!All.Value.TryGetValue(setId, out var set)) return result;
        foreach (var breakpoint in set.Breakpoints)
        {
            if (pieceCount < breakpoint.NumItems) continue;
            foreach (var bonus in breakpoint.Bonuses)
                result[bonus.AttributeId] = result.GetValueOrDefault(bonus.AttributeId) + bonus.Value;
        }
        return result;
    }

    private static Dictionary<int, Set> Load()
    {
        var assembly = Assembly.GetExecutingAssembly();
        var resource = assembly.GetManifestResourceNames()
            .First(n => n.EndsWith("GameData.set_catalog.json", StringComparison.Ordinal));
        using var stream = assembly.GetManifestResourceStream(resource)!;
        var raw = JsonSerializer.Deserialize<Dictionary<string, RawSet>>(stream,
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true })!;
        var result = new Dictionary<int, Set>();
        foreach (var (id, set) in raw)
            result[int.Parse(id)] = new Set(set.Name,
                set.Breakpoints.Select(b => new Breakpoint(b.NumItems,
                    b.Attributes.Select(a => new Bonus(a.AttributeId, a.AttributeName, a.Value)).ToList())).ToList());
        return result;
    }

    private sealed record RawSet(string Name, List<RawBreakpoint> Breakpoints);
    private sealed record RawBreakpoint(int NumItems, List<RawBonus> Attributes);
    private sealed record RawBonus(int AttributeId, string AttributeName, double Value);
}
