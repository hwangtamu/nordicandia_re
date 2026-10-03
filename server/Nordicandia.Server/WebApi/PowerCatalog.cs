using System.Reflection;
using System.Text.Json;
using Nordicandia.Simulation;

namespace Nordicandia.Server.WebApi;

/// <summary>
/// Per-class active/passive powers extracted from the shipped definitions by
/// <c>tools/web-content/export_powers.py</c>. The generated half lives in
/// <c>Powers.generated.cs</c>; this half holds the lookup and the selectable class pools.
/// </summary>
public static partial class PowerCatalog
{
    /// <summary>Client <c>SkillSlotRules</c> cap on equipped active skills.</summary>
    public const int MaxActiveSkills = 6;
    /// <summary>Client <c>SkillSlotRules</c> cap on equipped passive skills ("masteries").</summary>
    public const int MaxPassiveSkills = 3;

    /// <summary>Powers for a class, falling back to Warrior for classes without a web kit.</summary>
    public static ClassPowers ForClass(int classId)
        => ByClass.TryGetValue(classId, out var powers) ? powers : ByClass[0];

    /// <summary>Masteries for a power name, or empty when it has none.</summary>
    public static IReadOnlyList<MasteryProfile> MasteriesFor(string skillName)
        => MasteriesByPower.TryGetValue(skillName, out var rows) ? rows : Array.Empty<MasteryProfile>();

    /// <summary>One selectable power from a class pool (name/description/icon are ClientVerified).</summary>
    public readonly record struct PowerPoolEntry(
        Guid Id, int IntegerId, string Name, string Description, string Icon, string Type, string? ImplementedBy);

    public readonly record struct ClassPool(IReadOnlyList<PowerPoolEntry> Active, IReadOnlyList<PowerPoolEntry> Passive);

    private static readonly Dictionary<int, string> PoolNames = new()
    {
        [0] = "Warrior", [4] = "Hunter", [5] = "Mage", [6] = "Necromancer",
    };

    private static readonly IReadOnlyDictionary<int, ClassPool> Pools = LoadPools();
    private static readonly Dictionary<Guid, PowerPoolEntry> PoolById =
        Pools.Values.SelectMany(p => p.Active.Concat(p.Passive))
            .GroupBy(e => e.Id).ToDictionary(g => g.Key, g => g.First());

    /// <summary>The selectable active/passive pool for a class (empty for classes without one).</summary>
    public static ClassPool PoolFor(int classId)
        => Pools.TryGetValue(classId, out var pool) ? pool : new ClassPool(Array.Empty<PowerPoolEntry>(), Array.Empty<PowerPoolEntry>());

    /// <summary>Resolves any pooled power by its definition Guid.</summary>
    public static bool TryGetPooled(Guid id, out PowerPoolEntry entry) => PoolById.TryGetValue(id, out entry);

    private static IReadOnlyDictionary<int, ClassPool> LoadPools()
    {
        var assembly = Assembly.GetExecutingAssembly();
        var resource = assembly.GetManifestResourceNames()
            .First(n => n.EndsWith("GameData.power_pools.json", StringComparison.Ordinal));
        using var stream = assembly.GetManifestResourceStream(resource)!;
        var raw = JsonSerializer.Deserialize<Dictionary<string, RawPool>>(stream,
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true })!;
        var result = new Dictionary<int, ClassPool>();
        foreach (var (classId, name) in PoolNames)
        {
            if (!raw.TryGetValue(name, out var pool)) continue;
            result[classId] = new ClassPool(pool.Active.Select(ToEntry).ToList(), pool.Passive.Select(ToEntry).ToList());
        }
        return result;
    }

    private static PowerPoolEntry ToEntry(RawPower p) => new(
        Guid.Parse(p.Id), p.IntegerId, p.Name, p.Description, p.Icon, p.Type, p.ImplementedBy);

    private sealed record RawPool(List<RawPower> Active, List<RawPower> Passive);

    private sealed record RawPower(
        string Id, int IntegerId, string Name, string Description, string Icon, string Type, string? ImplementedBy);
}
