using System.Reflection;
using System.Text.Json;
using Nordicandia.Simulation;

namespace Nordicandia.Server.WebApi;

/// <summary>
/// Real item definitions (from gamedata_decrypted/Items.json), extracted with their implicit
/// base attributes (weapon damage/speed/crit, armour, evasion) to
/// tools/web-content/generated/item_catalog.json and embedded. Loot picks a definition for the
/// slot and rolls its implicit ranges, so base item values are calibrated to the client data
/// rather than to an arbitrary curve.
/// </summary>
public static class ItemCatalog
{
    public readonly record struct Implicit(string Name, double Min, double Max);
    public readonly record struct Definition(string Name, int IntegerId, string Type, IReadOnlyList<Implicit> Implicits)
    {
        public bool TryGet(string attributeName, out Implicit value)
        {
            foreach (var implicitValue in Implicits)
                if (implicitValue.Name == attributeName) { value = implicitValue; return true; }
            value = default;
            return false;
        }
    }

    // Equip slot -> candidate item type names (client ItemTypes).
    private static readonly string[][] SlotTypes =
    {
        new[] { "HeavyHelmet", "MediumHelmet", "LightHelmet" },
        new[] { "Amulet" },
        new[] { "HeavyShoulders", "MediumShoulders", "LightShoulders" },
        new[] { "HeavyChest", "MediumChest", "LightChest" },
        new[] { "Cloak" },
        new[] { "HeavyBracers", "MediumBracers", "LightBracers" },
        new[] { "HeavyGloves", "MediumGloves", "LightGloves" },
        new[] { "Belt" },
        new[] { "HeavyLeggings", "MediumLeggings", "LightLeggings" },
        new[] { "HeavyBoots", "MediumBoots", "LightBoots" },
        new[] { "Ring" },
        new[] { "Ring" },
        new[]
        {
            "Axe1H", "Mace1H", "Sword1H", "Crossbow1H", "ElementalWand1H", "FireWand1H", "ColdWand1H",
            "LightningWand1H", "PoisonWand1H", "Axe2H", "Mace2H", "Sword2H", "Spear2H", "Bow",
            "ElementalStaff2H", "FireStaff2H", "ColdStaff2H", "LightningStaff2H", "PoisonStaff2H",
        },
        new[] { "Shield", "Tome" },
    };

    private static readonly Lazy<IReadOnlyList<Definition>> All = new(Load);
    public static IReadOnlyList<Definition> Definitions => All.Value;

    /// <summary>Picks a definition matching the equip slot; falls back to any definition.</summary>
    public static Definition? Pick(int slot, CombatRandom rng)
    {
        var all = All.Value;
        if (slot >= 0 && slot < SlotTypes.Length)
        {
            var candidates = all.Where(d => SlotTypes[slot].Contains(d.Type)).ToList();
            if (candidates.Count > 0) return candidates[(int)(rng.NextDouble() * candidates.Count) % candidates.Count];
        }
        return all.Count > 0 ? all[(int)(rng.NextDouble() * all.Count) % all.Count] : null;
    }

    private static IReadOnlyList<Definition> Load()
    {
        var assembly = Assembly.GetExecutingAssembly();
        var resource = assembly.GetManifestResourceNames()
            .First(n => n.EndsWith("GameData.item_catalog.json", StringComparison.Ordinal));
        using var stream = assembly.GetManifestResourceStream(resource)!;
        var raw = JsonSerializer.Deserialize<Dictionary<string, RawDefinition>>(stream,
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true })!;
        return raw.Select(kv => new Definition(kv.Key, kv.Value.IntegerId, kv.Value.Type,
            kv.Value.Implicit.Select(i => new Implicit(i.Key, i.Value[0], i.Value[1])).ToList())).ToList();
    }

    private sealed record RawDefinition(int IntegerId, string Type, Dictionary<string, double[]> Implicit);
}
