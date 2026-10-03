using System.Reflection;
using System.Text.Json;

namespace Nordicandia.Server.WebApi;

/// <summary>
/// E02: the client's loot-table item-type spawn weights (Droprates.json), extracted by
/// tools/web-content/export_loot_tables.py. <see cref="Weight"/> multiplies a type's table
/// weight by the per-class multiplier (ItemTypeCharacterClassWeightMultipliers).
/// </summary>
public static class LootTableCatalog
{
    private sealed record Raw(IReadOnlyDictionary<string, IReadOnlyDictionary<string, int>> Tables,
        IReadOnlyDictionary<string, IReadOnlyDictionary<string, double>> ClassMultipliers,
        IReadOnlyDictionary<string, string> Parents);

    private static readonly Lazy<Raw> Data = new(Load);

    /// <summary>Default table name when no context is given.</summary>
    public const string DefaultTable = "Default";

    /// <summary>Spawn weight for an item type in a loot table and class (0 = not in the table).</summary>
    public static double Weight(string lootTable, string itemType, int classId)
    {
        if (!Data.Value.Tables.TryGetValue(lootTable, out var table)) return 0;
        // The table lists parent weapon types (Axe2H -> TwoHandMeleeWeapon), so walk the ItemTypes
        // parent chain until a weighted type is found.
        var type = itemType;
        for (var depth = 0; depth < 8 && type is not null; depth++)
        {
            if (table.TryGetValue(type, out var baseWeight))
            {
                var multiplier = 1.0;
                if (Data.Value.ClassMultipliers.TryGetValue(type, out var perClass)
                    && perClass.TryGetValue(classId.ToString(), out var m))
                    multiplier = m;
                return baseWeight * multiplier;
            }
            type = Data.Value.Parents.TryGetValue(type, out var parent) ? parent : null;
        }
        return 0;
    }

    public static bool HasTable(string lootTable) => Data.Value.Tables.ContainsKey(lootTable);

    public static IEnumerable<string> TableNames => Data.Value.Tables.Keys;

    private static Raw Load()
    {
        var assembly = Assembly.GetExecutingAssembly();
        var resource = assembly.GetManifestResourceNames()
            .First(n => n.EndsWith("GameData.loot_table_item_types.json", StringComparison.Ordinal));
        using var stream = assembly.GetManifestResourceStream(resource)!;
        return JsonSerializer.Deserialize<Raw>(stream,
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true })!;
    }
}
