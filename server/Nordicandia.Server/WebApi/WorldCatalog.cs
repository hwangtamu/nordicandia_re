using System.Reflection;
using System.Text.Json;

namespace Nordicandia.Server.WebApi;

/// <summary>
/// W04/W05: the client's world roster (gamedata_decrypted/Worlds.json), extracted by
/// tools/web-content/export_world_catalog_full.py and embedded. Each world has a tier, a theme, a
/// boss and the monster-type spawn weights used to build its spawn pool.
/// </summary>
public static class WorldCatalog
{
    public readonly record struct World(string Name, int IntegerId, int? Tier, int? ThemeId,
        string? BossName, IReadOnlyDictionary<string, int> SpawnWeights);

    private static readonly Lazy<IReadOnlyList<World>> All = new(Load);

    public static IReadOnlyList<World> Entries => All.Value;
    public static int Count => All.Value.Count;

    /// <summary>The world for a tier (the first world with that tier), or null.</summary>
    public static World? ForTier(int tier)
    {
        foreach (var world in All.Value)
            if (world.Tier == tier) return world;
        return null;
    }

    public static World? ById(int integerId)
    {
        foreach (var world in All.Value)
            if (world.IntegerId == integerId) return world;
        return null;
    }

    private static IReadOnlyList<World> Load()
    {
        var assembly = Assembly.GetExecutingAssembly();
        var resource = assembly.GetManifestResourceNames()
            .First(n => n.EndsWith("GameData.world_catalog.json", StringComparison.Ordinal));
        using var stream = assembly.GetManifestResourceStream(resource)!;
        var raw = JsonSerializer.Deserialize<RawCatalog>(stream,
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true })!;
        return raw.Items.Select(i => new World(i.Name ?? "", i.IntegerId, i.Tier, i.ThemeId, i.BossName,
            (IReadOnlyDictionary<string, int>?)(i.SpawnWeights) ?? new Dictionary<string, int>())).ToList();
    }

    private sealed record RawCatalog(List<RawWorld> Items);
    private sealed record RawWorld(string? Name, int IntegerId, int? Tier, int? ThemeId, string? BossName,
        Dictionary<string, int>? SpawnWeights);
}
