using System.Reflection;
using System.Text.Json;
using Nordicandia.Simulation;

namespace Nordicandia.Server.WebApi;

/// <summary>
/// W01: the client monster roster (gamedata_decrypted/Monsters.json), extracted by
/// tools/web-content/export_monster_catalog_full.py and embedded. Exposes the combat-relevant
/// fields (damage type, caster/ranged, size, available rarities) so monsters can be spawned from
/// the real definitions instead of generic archetypes.
/// </summary>
public static class MonsterCatalog
{
    public readonly record struct Monster(string Name, int IntegerId, string? TypeName, int? DamageType,
        bool Caster, bool Ranged, int? Size, IReadOnlyList<int> AvailableRarities);

    private static readonly Lazy<IReadOnlyDictionary<string, Monster>> All = new(Load);

    public static IEnumerable<Monster> Entries => All.Value.Values;
    public static int Count => All.Value.Count;

    public static Monster? ByName(string name)
        => name is not null && All.Value.TryGetValue(name, out var m) ? m : null;

    /// <summary>Game.DamageType ids (Physical=0, Fire=1, Cold=2, Lightning=3, Poison=4; Pure=5).</summary>
    public static string DamageTypeName(int? id) => id switch
    {
        1 => "Fire",
        2 => "Cold",
        3 => "Lightning",
        4 => "Poison",
        _ => "Physical",
    };

    /// <summary>W01: the monster's dominant damage as a single-element bundle, from the client's
    /// DamageType. A monster without an explicit type deals physical damage.</summary>
    public static DamageBundle DamageBundle(string monsterName)
        => DamageTypeName(ByName(monsterName)?.DamageType) switch
        {
            "Fire" => new DamageBundle(Fire: 1),
            "Cold" => new DamageBundle(Cold: 1),
            "Lightning" => new DamageBundle(Lightning: 1),
            "Poison" => new DamageBundle(Poison: 1),
            _ => new DamageBundle(Physical: 1),
        };

    private static IReadOnlyDictionary<string, Monster> Load()
    {
        var assembly = Assembly.GetExecutingAssembly();
        var resource = assembly.GetManifestResourceNames()
            .First(n => n.EndsWith("GameData.monster_catalog.json", StringComparison.Ordinal));
        using var stream = assembly.GetManifestResourceStream(resource)!;
        var raw = JsonSerializer.Deserialize<RawCatalog>(stream,
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true })!;
        var result = new Dictionary<string, Monster>();
        foreach (var item in raw.Items)
            if (item.Name is not null)
                result[item.Name] = new Monster(item.Name, item.IntegerId, item.TypeName, item.DamageType,
                    item.Caster, item.Ranged, item.Size, item.AvailableRarities ?? new List<int>());
        return result;
    }

    private sealed record RawCatalog(List<RawMonster> Items);
    private sealed record RawMonster(string? Name, int IntegerId, string? TypeName, int? DamageType,
        bool Caster, bool Ranged, int? Size, List<int>? AvailableRarities);
}
